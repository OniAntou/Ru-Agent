using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Terminal;

namespace RuAgent.Broker.Tools.Mcp;

/// <summary>
/// The MCP servers of one Host, run by the Broker execution authority (ADR 0025). A server
/// starts on its first use and at most one instance runs per Host; it is restarted when it
/// has exited or its declaration changed, and every server is stopped with the Host.
/// </summary>
/// <remarks>
/// Servers run as the current Windows user with the sanitized agent environment plus the
/// variables they declare. A credential reference is read from Windows Credential Manager
/// only when the server starts, and every resolved value is redacted from text this host
/// returns. Server output is bounded and labelled untrusted; only text content is returned.
/// </remarks>
public sealed class McpServerHost : IAsyncDisposable
{
    public const int MaxOutputChars = 256 * 1024;
    public const int MaxArgumentsBytes = 64 * 1024;
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    private readonly Func<string?> _userHome;
    private readonly Func<string, string?> _readCredential;
    private readonly Func<McpServerDefinition, IReadOnlyDictionary<string, string>, string, McpStdioClient> _start;
    private readonly Dictionary<string, Running> _running = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private sealed record Running(McpServerDefinition Definition, McpStdioClient Client, IReadOnlyList<string> Secrets);

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
    {
        _userHome = userHome ?? throw new ArgumentNullException(nameof(userHome));
        _readCredential = readCredential ?? throw new ArgumentNullException(nameof(readCredential));
        _start = start ?? throw new ArgumentNullException(nameof(start));
    }

    public McpConfiguration Configuration => McpConfiguration.Load(_userHome());

    /// <summary>The declared servers, without starting any.</summary>
    public JsonObject ListServers()
    {
        var servers = new JsonArray();
        foreach (var server in Configuration.Servers)
        {
            servers.Add(new JsonObject { ["name"] = server.Name, ["description"] = server.Description });
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
            list.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description is null ? null : McpStdioClient.Bounded(Redact(tool.Description, running.Secrets), 1024),
                ["input_schema"] = tool.InputSchema.DeepClone(),
            });
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
        var omitted = 0;
        if (result["content"] is JsonArray content)
        {
            foreach (var item in content.OfType<JsonObject>())
            {
                if (item["type"]?.GetValue<string>() == "text" && item["text"] is JsonValue value && value.TryGetValue<string>(out var part))
                {
                    if (text.Length > 0) text.Append('\n');
                    text.Append(part);
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

        var receipt = new JsonObject
        {
            ["server"] = serverName,
            ["tool"] = tool,
            ["output"] = output,
            ["output_truncated"] = truncated,
            ["untrusted"] = true,
        };
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

            var client = _start(definition, environment, _userHome() ?? Path.GetTempPath());
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
        left.Command == right.Command
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
            if (secret.Length >= 4) text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        }

        return text;
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
