using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net.Http;
using RuAgent.Broker.Tools.Terminal;

namespace RuAgent.Broker.Tools.Mcp;

/// <summary>
/// The MCP servers of one Host, run by the Broker execution authority (ADR 0025). A server
/// starts on its first use and at most one instance runs per Host; it is restarted when it
/// has exited or its declaration changed, and every server is stopped with the Host.
/// </summary>
/// <remarks>
/// Stdio servers run as the current Windows user with the sanitized agent environment plus
/// declared variables. Streamable HTTP uses only the fixed URL the user configured. Credential
/// references are resolved through the existing credential lookup, and resolved values are
/// redacted from returned text and JSON metadata. Server output is bounded and untrusted.
/// </remarks>
public sealed class McpServerHost : IAsyncDisposable
{
    public const int MaxOutputChars = 256 * 1024;
    public const int MaxArgumentsBytes = 64 * 1024;
    public const int MaxSchemaBytes = 64 * 1024;
    public const int MaxListToolsBytes = 1024 * 1024;
    public const int MaxStructuredContentBytes = 128 * 1024;
    public const int MaxResourceLinks = 64;
    public const int MaxResourceLinkBytes = 8 * 1024;
    public const int MaxResourceLinksBytes = 256 * 1024;
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    private readonly Func<string?> _userHome;
    private readonly Func<string, string?> _readCredential;
    private readonly Func<McpServerDefinition, IReadOnlyDictionary<string, string>, string, IMcpClient> _start;
    private readonly Func<McpServerDefinition, HttpMessageHandler>? _httpHandler;
    private readonly Dictionary<string, Running> _running = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private sealed record Running(McpServerDefinition Definition, IMcpClient Client, IReadOnlyList<string> Secrets);

    /// <param name="userHome">The profile folder that holds <c>.ru\mcp.json</c>.</param>
    /// <param name="readCredential">Reads one Credential Manager entry; null when it is missing.</param>
    public McpServerHost(Func<string?> userHome, Func<string, string?> readCredential)
        : this(userHome, readCredential, McpStdioClient.Start)
    {
    }

    internal McpServerHost(
        Func<string?> userHome,
        Func<string, string?> readCredential,
        Func<McpServerDefinition, IReadOnlyDictionary<string, string>, string, McpStdioClient> start)
        : this(userHome, readCredential, start, null)
    {
    }

    internal McpServerHost(
        Func<string?> userHome,
        Func<string, string?> readCredential,
        Func<McpServerDefinition, IReadOnlyDictionary<string, string>, string, McpStdioClient> start,
        Func<McpServerDefinition, HttpMessageHandler>? httpHandler)
    {
        _userHome = userHome ?? throw new ArgumentNullException(nameof(userHome));
        _readCredential = readCredential ?? throw new ArgumentNullException(nameof(readCredential));
        ArgumentNullException.ThrowIfNull(start);
        _start = (definition, environment, directory) => start(definition, environment, directory);
        _httpHandler = httpHandler;
    }

    public McpConfiguration Configuration => McpConfiguration.Load(_userHome());

    /// <summary>The declared servers, without starting any.</summary>
    public JsonObject ListServers()
    {
        var servers = new JsonArray();
        foreach (var server in Configuration.Servers)
        {
            var credentialsConfigured = server.BearerCredential is not null
                || server.Environment.Values.Any(value => value.Credential is not null);
            servers.Add(new JsonObject
            {
                ["name"] = server.Name,
                ["description"] = server.Description,
                ["transport"] = server.Transport == McpServerTransport.StreamableHttp ? "streamable_http" : "stdio",
                ["client_capabilities"] = new JsonArray(JsonValue.Create("tools/list"), JsonValue.Create("tools/call")),
                ["authentication"] = new JsonObject
                {
                    ["scheme"] = server.BearerCredential is not null ? "bearer" : credentialsConfigured ? "environment" : "none",
                    ["credential_configured"] = credentialsConfigured,
                },
            });
        }

        return new JsonObject { ["servers"] = servers };
    }

    public async Task<JsonObject> ListToolsAsync(string serverName, CancellationToken cancellationToken)
    {
        var running = await EnsureStartedAsync(serverName, cancellationToken).ConfigureAwait(false);
        var tools = await Guard(running, () => running.Client.ListToolsAsync(running.Definition.CallTimeout, cancellationToken)).ConfigureAwait(false);
        var list = new JsonArray();
        foreach (var tool in tools)
        {
            var name = McpStdioClient.Bounded(Redact(tool.Name, running.Secrets), 128);
            var schema = RedactJson(tool.InputSchema, running.Secrets) as JsonObject ?? new JsonObject { ["type"] = "object" };
            if (Encoding.UTF8.GetByteCount(schema.ToJsonString()) > MaxSchemaBytes)
                throw new McpException("mcp_output_too_large", "An MCP tool input schema exceeded its byte limit.");
            list.Add(new JsonObject
            {
                ["name"] = name,
                ["description"] = tool.Description is null ? null : McpStdioClient.Bounded(Redact(tool.Description, running.Secrets), 1024),
                ["input_schema"] = schema,
            });
            if (Encoding.UTF8.GetByteCount(list.ToJsonString()) > MaxListToolsBytes)
                throw new McpException("mcp_output_too_large", "The MCP tool list exceeded its byte limit.");
        }

        return new JsonObject { ["server"] = serverName, ["tools"] = list, ["untrusted"] = true };
    }

    public async Task<JsonObject> CallToolAsync(string serverName, string tool, string argumentsJson, CancellationToken cancellationToken)
    {
        var arguments = ParseArguments(argumentsJson);
        var running = await EnsureStartedAsync(serverName, cancellationToken).ConfigureAwait(false);
        var result = await Guard(running, () => running.Client.CallToolAsync(tool, arguments, running.Definition.CallTimeout, cancellationToken))
            .ConfigureAwait(false);
        var text = new StringBuilder();
        var resourceLinks = new JsonArray();
        var omitted = 0;
        if (result["content"] is JsonArray content)
        {
            foreach (var item in content.OfType<JsonObject>())
            {
                var contentType = item["type"] is JsonValue type && type.TryGetValue<string>(out var typeName) ? typeName : null;
                if (contentType == "text" && item["text"] is JsonValue value && value.TryGetValue<string>(out var part))
                {
                    if (text.Length > 0) text.Append('\n');
                    text.Append(part);
                }
                else if (contentType == "resource_link")
                {
                    if (resourceLinks.Count >= MaxResourceLinks)
                    {
                        omitted++;
                        continue;
                    }

                    var link = CreateSafeResourceLink(item, running.Secrets);
                    if (link is null)
                    {
                        omitted++;
                        continue;
                    }
                    resourceLinks.Add(link);
                    if (Encoding.UTF8.GetByteCount(link.ToJsonString()) > MaxResourceLinkBytes
                        || Encoding.UTF8.GetByteCount(resourceLinks.ToJsonString()) > MaxResourceLinksBytes)
                        throw new McpException("mcp_output_too_large", "The MCP resource links exceeded their metadata limit.");
                }
                else
                {
                    omitted++;
                }
            }
        }

        var output = Redact(text.ToString(), running.Secrets);
        var truncated = output.Length > MaxOutputChars;
        if (truncated) output = output[..MaxOutputChars];
        var isError = result["isError"] is JsonValue flag && flag.TryGetValue<bool>(out var error) && error;
        if (isError) throw new McpException("mcp_error", McpStdioClient.Bounded(output.Length == 0 ? "The tool reported an error." : output));

        JsonObject? structuredContent = null;
        if (result["structuredContent"] is JsonObject structured)
        {
            structuredContent = RedactJson(structured, running.Secrets) as JsonObject;
            if (structuredContent is not null
                && Encoding.UTF8.GetByteCount(structuredContent.ToJsonString()) > MaxStructuredContentBytes)
                throw new McpException("mcp_output_too_large", "The structured MCP result exceeded its byte limit.");
        }

        var receipt = new JsonObject
        {
            ["server"] = Redact(serverName, running.Secrets),
            ["tool"] = Redact(tool, running.Secrets),
            ["output"] = output,
            ["output_truncated"] = truncated,
            ["untrusted"] = true,
        };
        if (resourceLinks.Count > 0) receipt["resource_links"] = resourceLinks;
        if (structuredContent is not null) receipt["structured_content"] = structuredContent;
        if (omitted > 0) receipt["omitted_content"] = $"{omitted} non-text content item(s) omitted";
        return receipt;
    }

    internal static JsonObject ParseArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return new JsonObject();
        if (Encoding.UTF8.GetByteCount(argumentsJson) > MaxArgumentsBytes)
            throw new McpException("mcp_arguments_invalid", $"arguments exceed {MaxArgumentsBytes / 1024} KB.");
        try
        {
            return JsonNode.Parse(argumentsJson) as JsonObject
                ?? throw new McpException("mcp_arguments_invalid", "arguments must be a JSON object encoded as a string.");
        }
        catch (JsonException)
        {
            throw new McpException("mcp_arguments_invalid", "arguments must be a JSON object encoded as a string.");
        }
    }

    private async Task<Running> EnsureStartedAsync(string serverName, CancellationToken cancellationToken)
    {
        var definition = Configuration.Find(serverName)
            ?? throw new McpException("mcp_server_unknown", "No enabled server with this name is declared in ~/.ru/mcp.json.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running.TryGetValue(serverName, out var current))
            {
                if (current.Client.IsAlive && SameDefinition(current.Definition, definition))
                    return _running[serverName] = current with { Definition = definition };
                _running.Remove(serverName);
                await current.Client.DisposeAsync().ConfigureAwait(false);
            }

            var secrets = new List<string>();
            var environment = new Dictionary<string, string>(new AgentCommandEnvironment().ChildEnvironment, StringComparer.OrdinalIgnoreCase);
            string? bearerToken = null;
            if (definition.BearerCredential is { } credentialName)
            {
                bearerToken = _readCredential(credentialName)
                    ?? throw new McpException("mcp_credential_missing", $"Credential Manager has no entry named '{credentialName}'.");
                secrets.Add(bearerToken);
            }
            else
            {
                foreach (var (name, value) in definition.Environment)
                {
                    if (value.Credential is { } credential)
                    {
                        var secret = _readCredential(credential)
                            ?? throw new McpException("mcp_credential_missing", $"Credential Manager has no entry named '{credential}'.");
                        secrets.Add(secret);
                        environment[name] = secret;
                    }
                    else
                    {
                        environment[name] = value.Literal ?? string.Empty;
                    }
                }
            }

            IMcpClient client = definition.Transport == McpServerTransport.StreamableHttp
                ? new McpStreamableHttpClient(definition.Endpoint!, bearerToken, _httpHandler?.Invoke(definition))
                : _start(definition, environment, _userHome() ?? Path.GetTempPath());
            var running = new Running(definition, client, secrets);
            try
            {
                await Guard(running, async () =>
                {
                    await client.InitializeAsync(StartTimeout, cancellationToken).ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _running[serverName] = running;
            return running;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool SameDefinition(McpServerDefinition left, McpServerDefinition right) =>
        left.Transport == right.Transport
        && left.Endpoint == right.Endpoint
        && left.BearerCredential == right.BearerCredential
        && left.CallTimeout == right.CallTimeout
        && left.Command == right.Command
        && left.WorkingDirectory == right.WorkingDirectory
        && left.Arguments.SequenceEqual(right.Arguments)
        && left.Environment.Count == right.Environment.Count
        && left.Environment.All(pair => right.Environment.TryGetValue(pair.Key, out var other) && other == pair.Value);

    private static async Task<T> Guard<T>(Running running, Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (McpException error)
        {
            var detail = error.Detail;
            if (error.Code is "mcp_server_unavailable" && running.Client.StderrTail is { Length: > 0 } stderr)
                detail = $"{detail} Server stderr: {McpStdioClient.Bounded(stderr, 1024)}";
            throw new McpException(error.Code, detail is null ? null : Redact(detail, running.Secrets));
        }
    }

    private static string Redact(string text, IReadOnlyList<string> secrets)
    {
        foreach (var secret in secrets)
        {
            if (secret.Length > 0) text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        }

        return text;
    }

    private static JsonObject? CreateSafeResourceLink(JsonObject source, IReadOnlyList<string> secrets)
    {
        if (source["uri"] is not JsonValue uriValue || !uriValue.TryGetValue<string>(out var uri)
            || source["name"] is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var name))
            return null;

        var truncated = false;
        static string BoundedString(string value, int limit, ref bool wasTruncated)
        {
            var safe = value.Trim();
            if (safe.Length <= limit) return safe;
            wasTruncated = true;
            return safe[..limit];
        }

        uri = uri.Trim();
        name = name.Trim();
        var link = new JsonObject
        {
            ["uri"] = McpStdioClient.Bounded(Redact(uri, secrets), 2048),
            ["name"] = McpStdioClient.Bounded(Redact(name, secrets), 128),
        };
        truncated = uri.Length > 2048 || name.Length > 128;
        foreach (var (field, limit) in new[]
        {
            ("title", 512), ("description", 1024), ("mimeType", 128),
        })
        {
            if (source[field] is JsonValue value && value.TryGetValue<string>(out var text))
                link[field] = BoundedString(Redact(text, secrets), limit, ref truncated);
        }
        if (source["size"] is JsonValue size)
        {
            if (size.TryGetValue<long>(out var byteSize) && byteSize >= 0) link["size"] = byteSize;
            else if (size.TryGetValue<int>(out var intSize) && intSize >= 0) link["size"] = intSize;
        }
        if (source["annotations"] is JsonObject annotations)
        {
            var safeAnnotations = RedactJson(annotations, secrets);
            if (safeAnnotations is not null && Encoding.UTF8.GetByteCount(safeAnnotations.ToJsonString()) <= 4096)
                link["annotations"] = safeAnnotations;
            else
                truncated = true;
        }
        if (truncated) link["metadata_truncated"] = true;
        return link;
    }

    private static JsonNode? RedactJson(JsonNode? node, IReadOnlyList<string> secrets)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            return JsonValue.Create(Redact(text, secrets));
        if (node is JsonArray array)
        {
            var clone = new JsonArray();
            foreach (var item in array) clone.Add(RedactJson(item, secrets));
            return clone;
        }
        if (node is JsonObject obj)
        {
            var clone = new JsonObject();
            foreach (var (key, child) in obj)
            {
                var safeKey = Redact(key, secrets);
                if (clone.ContainsKey(safeKey)) throw new McpException("mcp_output_invalid", "Credential redaction made MCP object keys ambiguous.");
                clone[safeKey] = RedactJson(child, secrets);
            }
            return clone;
        }

        return node?.DeepClone();
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var running in _running.Values) await running.Client.DisposeAsync().ConfigureAwait(false);
            _running.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }
}
