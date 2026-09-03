using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuAgent.Broker.Mcp;

public sealed class McpClient : IAsyncDisposable, IDisposable
{
    private readonly Stream _readerStream;
    private readonly Stream _writerStream;
    private readonly StreamWriter _writer;
    private readonly StreamReader _reader;
    private readonly Process? _process;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoopTask;
    private long _nextId;
    private bool _disposed;

    public McpClient(Stream readerStream, Stream writerStream, Process? process = null)
    {
        _readerStream = readerStream ?? throw new ArgumentNullException(nameof(readerStream));
        _writerStream = writerStream ?? throw new ArgumentNullException(nameof(writerStream));
        _writer = new StreamWriter(_writerStream, Encoding.UTF8, 4096, leaveOpen: true) { AutoFlush = true };
        _reader = new StreamReader(_readerStream, Encoding.UTF8, false, 4096, leaveOpen: true);
        _process = process;
        _readLoopTask = Task.Run(ReadLoopAsync);
    }

    public const int MaxFrameChars = 16 * 1024 * 1024; // 16 MB maximum JSON-RPC frame limit

    public static McpClient StartProcess(string executable, IReadOnlyList<string> arguments, string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        if (!string.IsNullOrEmpty(workingDirectory) && !Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"Working directory does not exist: {workingDirectory}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = Encoding.UTF8,
        };

        if (!string.IsNullOrEmpty(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        var proc = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start MCP server: {executable}");

        return new McpClient(proc.StandardOutput.BaseStream, proc.StandardInput.BaseStream, proc);
    }

    public async Task<JsonNode?> SendRequestAsync(string method, JsonNode? parameters, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (parameters is not null)
        {
            request["params"] = parameters.DeepClone();
        }

        var json = request.ToJsonString();
        using (var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
        {
            await _writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            return await tcs.Task.ConfigureAwait(false);
        }
    }

    public async Task SendNotificationAsync(string method, JsonNode? parameters, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        };
        if (parameters is not null)
        {
            notification["params"] = parameters.DeepClone();
        }

        var json = notification.ToJsonString();
        await _writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var initParams = new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject
            {
                ["roots"] = new JsonObject { ["listChanged"] = true },
            },
            ["clientInfo"] = new JsonObject
            {
                ["name"] = "RuAgent",
                ["version"] = "1.0.0",
            },
        };

        await SendRequestAsync("initialize", initParams, cancellationToken).ConfigureAwait(false);
        await SendNotificationAsync("notifications/initialized", new JsonObject(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<McpToolDefinition>> ListToolsAsync(CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync("tools/list", new JsonObject(), cancellationToken).ConfigureAwait(false);
        if (response is not JsonObject obj || obj["tools"] is not JsonArray array)
        {
            return Array.Empty<McpToolDefinition>();
        }

        var list = new List<McpToolDefinition>();
        foreach (var item in array)
        {
            if (item is JsonObject toolObj && toolObj["name"] is JsonValue nameVal)
            {
                var name = nameVal.GetValue<string>();
                var desc = toolObj["description"] is JsonValue descVal ? descVal.GetValue<string>() : null;
                var inputSchema = toolObj["inputSchema"] as JsonObject ?? new JsonObject { ["type"] = "object" };
                list.Add(new McpToolDefinition(name, desc, inputSchema));
            }
        }

        return list;
    }

    public async Task<McpToolCallResult> CallToolAsync(string toolName, JsonObject arguments, CancellationToken cancellationToken)
    {
        var callParams = new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = arguments.DeepClone(),
        };

        var response = await SendRequestAsync("tools/call", callParams, cancellationToken).ConfigureAwait(false);
        if (response is not JsonObject obj)
        {
            return new McpToolCallResult(null, IsError: true);
        }

        var isError = obj["isError"] is JsonValue errVal && errVal.TryGetValue<bool>(out var err) && err;
        var contents = new List<McpContent>();
        if (obj["content"] is JsonArray contentArray)
        {
            foreach (var c in contentArray)
            {
                if (c is JsonObject cObj && cObj["type"] is JsonValue typeVal)
                {
                    var text = cObj["text"] is JsonValue textVal ? textVal.GetValue<string>() : null;
                    contents.Add(new McpContent(typeVal.GetValue<string>(), text));
                }
            }
        }

        return new McpToolCallResult(contents, isError);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Length > MaxFrameChars) continue;

                try
                {
                    var node = JsonNode.Parse(line);
                    if (node is JsonObject obj && obj.TryGetPropertyValue("id", out var idNode) && idNode is not null)
                    {
                        var id = idNode.GetValue<long>();
                        if (_pending.TryRemove(id, out var tcs))
                        {
                            if (obj.TryGetPropertyValue("error", out var errorNode) && errorNode is not null)
                            {
                                var errorMsg = errorNode["message"]?.ToString() ?? "MCP Error";
                                tcs.TrySetException(new InvalidOperationException(errorMsg));
                            }
                            else if (obj.TryGetPropertyValue("result", out var resultNode))
                            {
                                tcs.TrySetResult(resultNode);
                            }
                            else
                            {
                                tcs.TrySetResult(null);
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    // Ignore malformed JSON-RPC frames
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
            foreach (var kvp in _pending)
            {
                kvp.Value.TrySetCanceled();
            }
            _pending.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _writer.Dispose();
        _reader.Dispose();

        if (_process is not null && !_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
            _process.Dispose();
        }

        _cts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        try
        {
            await _readLoopTask.ConfigureAwait(false);
        }
        catch { }
    }
}
