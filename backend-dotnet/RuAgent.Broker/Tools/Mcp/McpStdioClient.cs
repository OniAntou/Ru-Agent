using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Terminal;

namespace RuAgent.Broker.Tools.Mcp;

/// <summary>A named MCP failure; <see cref="Exception.Message"/> is the stable code.</summary>
public sealed class McpException(string code, string? detail = null) : Exception(code)
{
    public string Code { get; } = code;

    /// <summary>Bounded, redacted text for the model; never a credential.</summary>
    public string? Detail { get; } = detail;
}

/// <summary>One tool a server lists.</summary>
public sealed record McpToolInfo(string Name, string? Description, JsonObject InputSchema);

/// <summary>
/// A JSON-RPC 2.0 client for one stdio MCP server. Frames are newline-delimited JSON on the
/// server's stdout; stderr is always drained into a small ring so a chatty server cannot
/// fill its pipe and stall. Every request has a deadline, and an id the client did not issue
/// (numeric or string) is ignored rather than ending the read loop.
/// </summary>
public sealed class McpStdioClient : IMcpClient
{
    public const int MaxFrameChars = 4 * 1024 * 1024;
    public const int MaxTools = 256;
    public const int MaxToolPages = 64;
    private const int StderrTailChars = 4096;
    public const string ProtocolVersion = "2024-11-05";

    private readonly StreamWriter _writer;
    private readonly Stream _output;
    private readonly Process? _process;
    private readonly IProcessTreeController? _tree;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StringBuilder _stderrTail = new();
    private readonly Task _readLoop;
    private readonly Task _stderrLoop;
    private long _nextId;
    private int _disposed;

    internal McpStdioClient(Stream serverOutput, Stream serverInput, Stream? serverError = null, Process? process = null, IProcessTreeController? tree = null)
    {
        _output = serverOutput ?? throw new ArgumentNullException(nameof(serverOutput));
        _writer = new StreamWriter(serverInput ?? throw new ArgumentNullException(nameof(serverInput)), new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = false,
            NewLine = "\n",
        };
        _process = process;
        _tree = tree;
        _readLoop = Task.Run(ReadLoopAsync);
        _stderrLoop = serverError is null ? Task.CompletedTask : Task.Run(() => DrainStderrAsync(serverError));
    }

    /// <summary>Whether the server process (when there is one) is still running.</summary>
    public bool IsAlive => _process is null ? !_readLoop.IsCompleted : !_process.HasExited && !_readLoop.IsCompleted;

    /// <summary>The last few kilobytes the server wrote to stderr, for a failure report.</summary>
    public string StderrTail
    {
        get { lock (_stderrTail) return _stderrTail.ToString(); }
    }

    /// <summary>Starts the server process inside a kill-on-close job object.</summary>
    public static McpStdioClient Start(McpServerDefinition server, IReadOnlyDictionary<string, string> environment, string fallbackDirectory)
    {
        ArgumentNullException.ThrowIfNull(server);
        var start = new ProcessStartInfo
        {
            FileName = server.Command,
            WorkingDirectory = server.WorkingDirectory ?? fallbackDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in server.Arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var (name, value) in environment) start.Environment[name] = value;

        WindowsJobProcessTree? tree = null;
        Process? process = null;
        try
        {
            tree = WindowsJobProcessTree.Create();
            process = Process.Start(start) ?? throw new McpException("mcp_server_unavailable");
            tree.Assign(process.Id);
            return new McpStdioClient(process.StandardOutput.BaseStream, process.StandardInput.BaseStream,
                process.StandardError.BaseStream, process, tree);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            try { process?.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process?.Dispose();
            tree?.Dispose();
            throw new McpException("mcp_server_unavailable", "The server process could not be started.");
        }
    }

    public async Task InitializeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "Ru", ["version"] = "1.0.0" },
        }, timeout, cancellationToken).ConfigureAwait(false);
        await NotifyAsync("notifications/initialized", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var tools = new List<McpToolInfo>();
        string? cursor = null;
        var pages = 0;
        do
        {
            if (++pages > MaxToolPages)
                throw new McpException("mcp_page_limit", "tools/list exceeded its page limit.");
            var parameters = new JsonObject();
            if (cursor is not null) parameters["cursor"] = cursor;
            var result = await RequestAsync("tools/list", parameters, timeout, cancellationToken).ConfigureAwait(false) as JsonObject
                ?? throw new McpException("mcp_error", "tools/list returned no result.");
            if (result["tools"] is JsonArray page)
            {
                foreach (var item in page.OfType<JsonObject>())
                {
                    if (tools.Count >= MaxTools) return tools;
                    if (Text(item["name"]) is not { Length: > 0 and <= 128 } name) continue;
                    tools.Add(new McpToolInfo(name, Text(item["description"]), item["inputSchema"] as JsonObject ?? new JsonObject { ["type"] = "object" }));
                }
            }

            cursor = Text(result["nextCursor"]);
        }
        while (cursor is not null);

        return tools;
    }

    public async Task<JsonObject> CallToolAsync(string tool, JsonObject arguments, TimeSpan timeout, CancellationToken cancellationToken) =>
        await RequestAsync("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments.DeepClone() }, timeout, cancellationToken)
            .ConfigureAwait(false) as JsonObject
        ?? throw new McpException("mcp_error", "tools/call returned no result.");

    private async Task<JsonNode?> RequestAsync(string method, JsonObject parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var id = Interlocked.Increment(ref _nextId);
        var reply = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        try
        {
            await WriteFrameAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }, cancellationToken)
                .ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            deadline.CancelAfter(timeout);
            try
            {
                return await reply.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw _readLoop.IsCompleted
                    ? new McpException("mcp_server_unavailable", "The server stopped before it answered.")
                    : new McpException("mcp_timeout", $"The server did not answer {method} within {timeout.TotalSeconds:0} seconds.");
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private Task NotifyAsync(string method, CancellationToken cancellationToken) =>
        WriteFrameAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method }, cancellationToken);

    private async Task WriteFrameAsync(JsonObject frame, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(frame.ToJsonString().AsMemory(), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            throw new McpException("mcp_server_unavailable", "The server closed its input.");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        var reader = new StreamReader(_output, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, 8192, leaveOpen: true);
        var line = new StringBuilder();
        var buffer = new char[8192];
        var oversized = false;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var read = await reader.ReadAsync(buffer, _lifetime.Token).ConfigureAwait(false);
                if (read == 0) break;
                for (var index = 0; index < read; index++)
                {
                    var c = buffer[index];
                    if (c == '\n')
                    {
                        if (!oversized) Dispatch(line.ToString());
                        line.Clear();
                        oversized = false;
                    }
                    else if (!oversized)
                    {
                        // A frame past the bound is dropped whole: the line is skipped up to
                        // its newline instead of being buffered without limit.
                        if (line.Length >= MaxFrameChars) { oversized = true; line.Clear(); }
                        else line.Append(c);
                    }
                }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
        finally
        {
            foreach (var pending in _pending.Values)
                pending.TrySetException(new McpException("mcp_server_unavailable", "The server stopped before it answered."));
        }
    }

    private void Dispatch(string frame)
    {
        if (string.IsNullOrWhiteSpace(frame)) return;
        JsonObject? message;
        try
        {
            message = JsonNode.Parse(frame) as JsonObject;
        }
        catch (JsonException)
        {
            return;
        }

        if (message?["id"] is not JsonValue idValue || !TryReadId(idValue, out var id) || !_pending.TryGetValue(id, out var reply)) return;
        if (message["error"] is JsonObject error)
        {
            reply.TrySetException(new McpException("mcp_error", Bounded(Text(error["message"]) ?? "The server returned an error.")));
        }
        else
        {
            reply.TrySetResult(message["result"]?.DeepClone());
        }
    }

    private static bool TryReadId(JsonValue value, out long id)
    {
        if (value.TryGetValue(out id)) return true;
        return value.TryGetValue<string>(out var text) && long.TryParse(text, out id);
    }

    private async Task DrainStderrAsync(Stream stderr)
    {
        var reader = new StreamReader(stderr, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        var buffer = new char[4096];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer, _lifetime.Token).ConfigureAwait(false)) > 0)
            {
                lock (_stderrTail)
                {
                    _stderrTail.Append(buffer, 0, read);
                    if (_stderrTail.Length > StderrTailChars) _stderrTail.Remove(0, _stderrTail.Length - StderrTailChars);
                }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    internal static string Bounded(string text, int max = 2048) => text.Length <= max ? text : text[..max];

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try { _writer.Dispose(); } catch (IOException) { }
        if (_process is not null)
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            _process.Dispose();
        }

        _tree?.Dispose();
        try { await Task.WhenAll(_readLoop, _stderrLoop).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (TimeoutException) { }
        _lifetime.Dispose();
        _writeGate.Dispose();
    }
}
