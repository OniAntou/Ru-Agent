using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace RuAgent.Broker.Tools.Mcp;

/// <summary>A JSON-RPC client for the MCP 2025-11-25 Streamable HTTP transport.</summary>
internal sealed class McpStreamableHttpClient : IMcpClient
{
    public const string ProtocolVersion = "2025-11-25";
    public const int MaxResponseBytes = 4 * 1024 * 1024;
    public const int MaxSseEventBytes = 1024 * 1024;
    public const int MaxSseEvents = 256;
    private static readonly TimeSpan CancellationNotificationTimeout = TimeSpan.FromSeconds(1);

    private readonly Uri _endpoint;
    private readonly string? _bearerToken;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private long _nextId;
    private string? _sessionId;
    private string? _negotiatedProtocolVersion;
    private bool _initialized;
    private bool _disposed;

    internal McpStreamableHttpClient(Uri endpoint, string? bearerToken, HttpMessageHandler? handler = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        if (!McpConfiguration.IsValidEndpoint(endpoint))
            throw new McpException("mcp_configuration_invalid", "The configured HTTP endpoint must use HTTPS or loopback HTTP.");
        if (bearerToken is not null && !IsValidBearerToken(bearerToken))
            throw new McpException("mcp_credential_invalid", "The configured bearer credential is not a valid token.");
        _bearerToken = bearerToken;
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public bool IsAlive => !_disposed;

    public string StderrTail => string.Empty;

    public async Task InitializeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var initializationDeadline = CreateDeadline(timeout, cancellationToken, out var initializationToken);
        var gateEntered = false;
        try
        {
            await _initializeGate.WaitAsync(initializationToken).ConfigureAwait(false);
            gateEntered = true;
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized) return;

            _sessionId = null;
            _negotiatedProtocolVersion = null;
            var result = await RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "Ru", ["version"] = "1.0.0" },
            }, timeout, initializationToken, isInitialize: true).ConfigureAwait(false) as JsonObject
                ?? throw new McpException("mcp_protocol_error", "initialize returned no result.");

            var negotiated = Text(result["protocolVersion"]);
            if (!string.Equals(negotiated, ProtocolVersion, StringComparison.Ordinal))
                throw new McpException("mcp_protocol_version_unsupported", "The server selected an unsupported MCP protocol version.");
            _negotiatedProtocolVersion = negotiated;
            _initialized = true;
            await SendNotificationAsync("notifications/initialized", timeout, initializationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            _initialized = false;
            throw new McpException("mcp_timeout", $"MCP initialization did not complete within {timeout.TotalSeconds:0} seconds.");
        }
        catch
        {
            _initialized = false;
            throw;
        }
        finally
        {
            if (gateEntered) _initializeGate.Release();
        }
    }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CreateDeadline(timeout, cancellationToken, out var token);
        try
        {
            await InitializeAsync(timeout, token).ConfigureAwait(false);
            var tools = new List<McpToolInfo>();
            string? cursor = null;
            var pages = 0;
            do
            {
                if (++pages > McpStdioClient.MaxToolPages)
                    throw new McpException("mcp_page_limit", "tools/list exceeded its page limit.");
                var parameters = new JsonObject();
                if (cursor is not null) parameters["cursor"] = cursor;
                var result = await RequestAsync("tools/list", parameters, timeout, token).ConfigureAwait(false) as JsonObject
                    ?? throw new McpException("mcp_error", "tools/list returned no result.");
                if (result["tools"] is JsonArray page)
                {
                    foreach (var item in page.OfType<JsonObject>())
                    {
                        if (tools.Count >= McpStdioClient.MaxTools) return tools;
                        if (Text(item["name"]) is not { Length: > 0 and <= 128 } name) continue;
                        tools.Add(new McpToolInfo(name, Text(item["description"]),
                            item["inputSchema"] as JsonObject ?? new JsonObject { ["type"] = "object" }));
                    }
                }

                cursor = Text(result["nextCursor"]);
            }
            while (cursor is not null);

            return tools;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new McpException("mcp_timeout", $"The server did not finish tools/list within {timeout.TotalSeconds:0} seconds.");
        }
    }

    public async Task<JsonObject> CallToolAsync(
        string tool,
        JsonObject arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
            throw new McpException("mcp_session_expired", "The MCP session needs to be listed again before another tool call.");
        return await RequestAsync("tools/call", new JsonObject
        {
            ["name"] = tool,
            ["arguments"] = arguments.DeepClone(),
        }, timeout, cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new McpException("mcp_error", "tools/call returned no result.");
    }

    private async Task SendNotificationAsync(string method, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CreateDeadline(timeout, cancellationToken, out var token);
        using var request = CreateRequest(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method });
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            HandleStatus(response, isRequest: false, hasSession: _sessionId is not null);
            if (response.StatusCode != HttpStatusCode.Accepted || response.Content.Headers.ContentLength is > 0)
                throw new McpException("mcp_protocol_error", "The server did not accept the initialized notification.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new McpException("mcp_timeout", $"The server did not answer {method} within {timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException)
        {
            throw new McpException("mcp_server_unavailable", "The configured MCP endpoint could not be reached.");
        }
    }

    private async Task<JsonNode?> RequestAsync(
        string method,
        JsonObject parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool isInitialize = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!isInitialize && !_initialized)
            throw new McpException("mcp_session_expired", "The MCP session has expired; list tools to initialize a new session.");

        var id = Interlocked.Increment(ref _nextId);
        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters.DeepClone(),
        };
        using var deadline = CreateDeadline(timeout, cancellationToken, out var token);
        using var request = CreateRequest(payload);
        var requestAttempted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            requestAttempted = true;
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            HandleStatus(response, isRequest: true, hasSession: _sessionId is not null);
            if (isInitialize)
                _sessionId = ReadSessionId(response);
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            var responseNode = mediaType switch
            {
                var value when string.Equals(value, "application/json", StringComparison.OrdinalIgnoreCase) =>
                    await ReadJsonResponseAsync(response, id, token).ConfigureAwait(false),
                var value when string.Equals(value, "text/event-stream", StringComparison.OrdinalIgnoreCase) =>
                    await ReadEventStreamResponseAsync(response, id, token).ConfigureAwait(false),
                _ => throw new McpException("mcp_protocol_error", "The server returned an unsupported MCP response type."),
            };
            if (responseNode is not JsonObject { } json || Text(json["jsonrpc"]) != "2.0"
                || !HasMatchingId(json["id"], id))
                throw new McpException("mcp_protocol_error", "The server response did not match the outstanding MCP request.");
            if (json["error"] is JsonObject error)
                throw new McpException("mcp_error", McpStdioClient.Bounded(Text(error["message"]) ?? "The server returned an error."));
            return json["result"]?.DeepClone();
        }
        catch (McpException error) when (error.Code == "mcp_session_expired")
        {
            _initialized = false;
            _sessionId = null;
            _negotiatedProtocolVersion = null;
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            if (requestAttempted) await SendCancellationNotificationAsync(id).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            if (requestAttempted) await SendCancellationNotificationAsync(id).ConfigureAwait(false);
            throw new McpException("mcp_timeout", $"The server did not answer {method} within {timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException)
        {
            throw new McpException("mcp_server_unavailable", "The configured MCP endpoint could not be reached.");
        }
    }

    private async Task SendCancellationNotificationAsync(long requestId)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(CancellationNotificationTimeout);
        using var request = CreateRequest(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/cancelled",
            ["params"] = new JsonObject
            {
                ["requestId"] = requestId,
                ["reason"] = "The client stopped waiting for this request.",
            },
        });
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            HandleStatus(response, isRequest: false, hasSession: _sessionId is not null);
        }
        catch (McpException error) when (error.Code == "mcp_session_expired")
        {
            _initialized = false;
            _sessionId = null;
            _negotiatedProtocolVersion = null;
        }
        catch (Exception error) when (error is OperationCanceledException or HttpRequestException or McpException)
        {
            // Cancellation is best-effort; preserve the original caller cancellation or timeout.
        }
    }

    private HttpRequestMessage CreateRequest(JsonObject body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (_bearerToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
        if (_sessionId is not null)
            request.Headers.TryAddWithoutValidation("MCP-Session-Id", _sessionId);
        if (_negotiatedProtocolVersion is not null)
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _negotiatedProtocolVersion);
        return request;
    }

    private static void HandleStatus(HttpResponseMessage response, bool isRequest, bool hasSession)
    {
        var status = (int)response.StatusCode;
        if (status == (int)HttpStatusCode.Unauthorized)
            throw new McpException("mcp_auth_required", "The MCP server requires valid bearer authentication.");
        if (status is >= 300 and < 400)
            throw new McpException("mcp_redirect_rejected", "The configured MCP endpoint returned a redirect; redirects are not followed.");
        if (status == (int)HttpStatusCode.NotFound && hasSession)
            throw new McpException("mcp_session_expired", "The MCP server no longer recognizes this session.");
        if (!response.IsSuccessStatusCode)
            throw new McpException("mcp_http_error", $"The configured MCP endpoint returned HTTP {status.ToString(CultureInfo.InvariantCulture)}.");
        if (!isRequest && response.StatusCode != HttpStatusCode.Accepted)
            throw new McpException("mcp_protocol_error", "The server did not accept an MCP notification.");
    }

    private static string? ReadSessionId(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("MCP-Session-Id", out var values)) return null;
        var headers = values.ToArray();
        if (headers.Length != 1)
            throw new McpException("mcp_protocol_error", "The server returned an invalid MCP session identifier.");
        var sessionId = headers[0];
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Any(character => character is < (char)0x21 or > (char)0x7e))
            throw new McpException("mcp_protocol_error", "The server returned an invalid MCP session identifier.");
        return sessionId;
    }

    private async Task<JsonObject> ReadJsonResponseAsync(HttpResponseMessage response, long id, CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        try
        {
            var envelope = JsonNode.Parse(new UTF8Encoding(false, true).GetString(bytes)) as JsonObject
                ?? throw new McpException("mcp_protocol_error", "The server returned invalid JSON-RPC data.");
            if (Text(envelope["jsonrpc"]) != "2.0" || !HasMatchingId(envelope["id"], id))
                throw new McpException("mcp_protocol_error", "The server response did not match the outstanding MCP request.");
            return envelope;
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or DecoderFallbackException)
        {
            throw new McpException("mcp_protocol_error", "The server returned invalid JSON-RPC data.");
        }
    }

    private async Task<JsonObject> ReadEventStreamResponseAsync(HttpResponseMessage response, long id, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            throw new McpException("mcp_output_too_large", "The MCP response exceeded its byte limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var line = new List<byte>(1024);
        var eventData = new StringBuilder();
        var buffer = new byte[8192];
        var totalBytes = 0;
        var events = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            totalBytes += read;
            if (totalBytes > MaxResponseBytes)
                throw new McpException("mcp_output_too_large", "The MCP response exceeded its byte limit.");

            for (var index = 0; index < read; index++)
            {
                var value = buffer[index];
                if (value == (byte)'\n')
                {
                    string text;
                    try { text = new UTF8Encoding(false, true).GetString(line.ToArray()); }
                    catch (DecoderFallbackException) { throw new McpException("mcp_protocol_error", "The server returned invalid UTF-8 event data."); }
                    line.Clear();
                    if (text.EndsWith('\r')) text = text[..^1];
                    var matched = ProcessSseLine(text, id, eventData, ref events);
                    if (matched is not null) return matched;
                    continue;
                }

                if (line.Count >= MaxSseEventBytes)
                    throw new McpException("mcp_output_too_large", "An MCP event line exceeded its byte limit.");
                line.Add(value);
            }
        }

        if (line.Count > 0)
        {
            string text;
            try { text = new UTF8Encoding(false, true).GetString(line.ToArray()); }
            catch (DecoderFallbackException) { throw new McpException("mcp_protocol_error", "The server returned invalid UTF-8 event data."); }
            if (text.EndsWith('\r')) text = text[..^1];
            var matched = ProcessSseLine(text, id, eventData, ref events);
            if (matched is not null) return matched;
        }

        return ProcessSseLine(string.Empty, id, eventData, ref events)
            ?? throw new McpException("mcp_response_missing", "The MCP event stream ended without the request response.");
    }

    private static JsonObject? ProcessSseLine(string line, long id, StringBuilder eventData, ref int events)
    {
        if (line.Length == 0)
        {
            if (eventData.Length == 0) return null;
            events++;
            if (events > MaxSseEvents) throw new McpException("mcp_event_limit", "The server exceeded the MCP event limit.");
            var data = eventData.ToString();
            eventData.Clear();
            if (Encoding.UTF8.GetByteCount(data) > MaxSseEventBytes)
                throw new McpException("mcp_output_too_large", "An MCP event exceeded the response limit.");
            return ParseSseData(data, id);
        }

        if (line.StartsWith(':') || !line.StartsWith("data:", StringComparison.Ordinal)) return null;
        var value = line.AsSpan(5);
        if (!value.IsEmpty && value[0] == ' ') value = value[1..];
        if (eventData.Length > 0) eventData.Append('\n');
        eventData.Append(value);
        if (Encoding.UTF8.GetByteCount(eventData.ToString()) > MaxSseEventBytes)
            throw new McpException("mcp_output_too_large", "An MCP event exceeded the response limit.");
        return null;
    }

    private static JsonObject? ParseSseData(string data, long id)
    {
        JsonObject? message;
        try { message = JsonNode.Parse(data) as JsonObject; }
        catch (System.Text.Json.JsonException) { throw new McpException("mcp_protocol_error", "The MCP event was not valid JSON-RPC data."); }
        if (message is null) throw new McpException("mcp_protocol_error", "The MCP event was not a JSON-RPC object.");
        if (message["method"] is not null && message["id"] is not null)
            throw new McpException("mcp_protocol_unsupported", "The server sent a request that Ru cannot answer.");
        return HasMatchingId(message["id"], id) ? message : null;
    }

    private static bool HasMatchingId(JsonNode? node, long id) =>
        node is JsonValue value && value.TryGetValue<long>(out var numeric) && numeric == id;

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaxResponseBytes)
            throw new McpException("mcp_output_too_large", "The MCP response exceeded its byte limit.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaxResponseBytes)
                throw new McpException("mcp_output_too_large", "The MCP response exceeded its byte limit.");
            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private CancellationTokenSource CreateDeadline(TimeSpan timeout, CancellationToken cancellationToken, out CancellationToken token)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        deadline.CancelAfter(timeout);
        token = deadline.Token;
        return deadline;
    }

    private static bool IsValidBearerToken(string token)
    {
        if (token.Length is 0 or > 4096) return false;
        var paddingStart = token.IndexOf('=');
        var payload = paddingStart < 0 ? token : token[..paddingStart];
        var padding = paddingStart < 0 ? string.Empty : token[paddingStart..];
        return payload.Length > 0
            && payload.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~' or '+' or '/')
            && padding.All(character => character == '=');
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _http.Dispose();
        _initializeGate.Dispose();
        _lifetime.Dispose();
    }
}
