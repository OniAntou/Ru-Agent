using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using RuAgent.Protocol;
using RuBackend.Business.Activity;
using RuBackend.Business.Sessions;
using RuBackend.Business.Settings;
using RuBackend.Agent;
using RuBackend.Diagnostics;
using RuBackend.Memory;
using RuBackend.Worker;

namespace RuBackend.Conversation;

public interface IConversationCoordinator
{
    IAsyncEnumerable<ChatEvent> HandleAsync(
        ChatRequest request,
        CancellationToken cancellationToken);
}

public sealed class ConversationCoordinator : IConversationCoordinator
{
    public const string ProcessingHistoryErrorText =
        "(Lỗi xử lý: không phản hồi được yêu cầu này.)";

    private const string ProcessingPublicErrorText = "Đã xảy ra lỗi xử lý yêu cầu.";
    private const string BusyText =
        "Ru is already replying; wait for the current reply to finish.";
    private const string MemoryBusyText =
        "Ru is busy with self-learning; try again in a few seconds.";
    private const int MaximumContextTurns = 8;

    private readonly ISessionStore _sessions;
    private readonly IMemoryService _memory;
    private readonly ISettingsStore _settings;
    private readonly IActivityLogStore _activity;
    private readonly IWorkerClient _worker;
    private readonly IMetadataPolicy _metadata;
    private readonly IStreamSanitizer _sanitizer;
    private readonly ConversationTurnGate _gate;
    private readonly IDiagnosticEmitter? _diagnosticEmitter;
    private readonly IAgentTurnCoordinator? _agent;

    public ConversationCoordinator(
        ISessionStore sessions,
        IMemoryService memory,
        ISettingsStore settings,
        IActivityLogStore activity,
        IWorkerClient worker,
        IMetadataPolicy metadata,
        IStreamSanitizer sanitizer,
        ConversationTurnGate gate,
        IDiagnosticEmitter? diagnosticEmitter = null,
        IAgentTurnCoordinator? agent = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _sanitizer = sanitizer ?? throw new ArgumentNullException(nameof(sanitizer));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _diagnosticEmitter = diagnosticEmitter;
        _agent = agent;
    }

    public IAsyncEnumerable<ChatEvent> HandleAsync(
        ChatRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var channel = Channel.CreateUnbounded<ChatEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
        var producer = ProduceAsync(request, channel.Writer, cancellationToken);
        return ReadEventsAsync(channel.Reader, producer, cancellationToken);
    }

    private async Task ProduceAsync(
        ChatRequest request,
        ChannelWriter<ChatEvent> writer,
        CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        string? sessionId = null;
        var userAppended = false;
        Exception? completionError = null;
        try
        {
            var prompt = request.Prompt?.Trim() ?? string.Empty;
            if (prompt.Length == 0 || prompt.Length > ChatRequest.MaxPromptCharacters)
            {
                await WriteAsync(
                    writer,
                    Error("invalid_prompt", "Invalid prompt."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var resolved = await ResolveSessionAsync(
                request.SessionId,
                writer,
                cancellationToken).ConfigureAwait(false);
            if (resolved is null)
            {
                return;
            }

            sessionId = resolved.Value.SessionId;
            using var turnLease = _gate.TryEnterTurn();
            if (turnLease is null)
            {
                await WriteAsync(
                    writer,
                    Error("busy", BusyText, sessionId),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await using var memoryLease = await _gate.EnterMemoryAsync(cancellationToken)
                .ConfigureAwait(false);
            if (memoryLease is null)
            {
                await WriteAsync(
                    writer,
                    Error("memory_busy", MemoryBusyText, sessionId),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var settings = await _settings.ReadAsync(cancellationToken).ConfigureAwait(false);
            var modelTools = _agent is null
                ? Array.Empty<AgentToolCapability>()
                : (await _agent.GetModelCapabilitiesAsync(cancellationToken).ConfigureAwait(false))
                    .Where(tool => tool.ModelEnabled)
                    .OrderBy(tool => tool.ToolId, StringComparer.Ordinal)
                    .ThenBy(tool => tool.ToolVersion, StringComparer.Ordinal)
                    .ToArray();
            var turns = await _sessions.GetContextTurnsAsync(
                sessionId,
                MaximumContextTurns,
                cancellationToken).ConfigureAwait(false);
            var cleanPrompt = _metadata.StripVoiceMarkers(prompt);
            var memories = await _memory.RetrieveAsync(cleanPrompt, cancellationToken)
                .ConfigureAwait(false);
            if (!await _sessions.AppendAsync(
                    sessionId,
                    [new SessionMessage("user", cleanPrompt)],
                    cancellationToken).ConfigureAwait(false))
            {
                throw new ConversationException("session_write_failed");
            }

            userAppended = true;
            var requestId = $"chat-{Guid.NewGuid():N}";
            var catalog = await TryGetCatalogAsync(requestId, cancellationToken)
                .ConfigureAwait(false);
            var workerRequest = new WorkerChatRequest(
                requestId,
                cleanPrompt,
                sessionId,
                turns.Select(turn => new WorkerTurn(turn.Prompt, turn.Response)).ToArray(),
                ShouldShareMemory(settings) ? RenderMemory(memories) : string.Empty,
                BuildVtsInstruction(catalog),
                BuildWorkerSettings(settings, request.AgentMode, modelTools));
            if (_agent is not null && modelTools.Length > 0)
            {
                workerRequest = workerRequest with
                {
                    ToolHandler = (tool, toolCancellation) =>
                        _agent.ExecuteToolAsync(
                            new AgentTurnRequest(
                                tool,
                                value => WriteAsync(writer, value, toolCancellation)),
                            toolCancellation),
                };
            }

            var rawStream = string.Empty;
            var visibleStream = string.Empty;
            await foreach (var item in _worker.GenerateChatAsync(workerRequest, cancellationToken)
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                switch (item.Type)
                {
                    case WorkerMessageTypes.ChatReset:
                        rawStream = string.Empty;
                        visibleStream = string.Empty;
                        await WriteAsync(
                            writer,
                            new ChatEvent("reset"),
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case WorkerMessageTypes.ChatChunk:
                        var delta = ReadRequiredString(item.Payload, "text", "invalid_worker_response");
                        rawStream = _sanitizer.Append(rawStream, delta);
                        var nextVisible = _sanitizer.Visible(rawStream);
                        await WriteVisibleDeltaAsync(
                            writer,
                            visibleStream,
                            nextVisible,
                            cancellationToken).ConfigureAwait(false);
                        visibleStream = nextVisible;
                        break;
                    case WorkerMessageTypes.ChatDone:
                        var rawReply = ReadRequiredString(
                            item.Payload,
                            "raw_reply",
                            "invalid_worker_response");
                        var (splitVisible, codes, emotion) = _metadata.SplitReply(rawReply, catalog);
                        var fullReply = _metadata.StripVoiceMarkers(splitVisible);
                        await ReconcileFinalVisibleAsync(
                            writer,
                            visibleStream,
                            fullReply,
                            cancellationToken).ConfigureAwait(false);
                        await TryRememberAsync(
                            settings,
                            cleanPrompt,
                            fullReply,
                            cancellationToken).ConfigureAwait(false);
                        if (!await _sessions.AppendAsync(
                                sessionId,
                                [new SessionMessage("ai", fullReply)],
                                cancellationToken).ConfigureAwait(false))
                        {
                            throw new ConversationException("session_write_failed");
                        }

                        await TryTriggerVtsAsync(requestId, codes, cancellationToken)
                            .ConfigureAwait(false);
                        TryEnqueueLearning(settings, cleanPrompt, fullReply);
                        _ = TryAppendActivityAsync(
                            sessionId,
                            cleanPrompt,
                            fullReply,
                            emotion);
                        await WriteAsync(
                            writer,
                            new ChatEvent("done", fullReply, sessionId),
                            cancellationToken).ConfigureAwait(false);

                        _diagnosticEmitter?.TryEmit(
                            "info",
                            "host.conversation",
                            DiagnosticCodes.TurnDone,
                            durationMs: stopwatch.Elapsed.TotalMilliseconds,
                            status: "succeeded");
                        return;
                    case WorkerMessageTypes.ChatToolRequest:
                        // AgentTurnCoordinator owns requested and terminal activity.
                        break;
                    case WorkerMessageTypes.Error:
                        throw new ConversationException(ReadWorkerErrorCode(item.Payload));
                    default:
                        throw new ConversationException("invalid_worker_response");
                }
            }

            throw new ConversationException("missing_worker_response");
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
        {
            completionError = error;
            _diagnosticEmitter?.TryEmit(
                "warning",
                "host.conversation",
                DiagnosticCodes.TurnCancelled,
                durationMs: stopwatch.Elapsed.TotalMilliseconds,
                status: "cancelled",
                exceptionKind: "cancelled");
        }
        catch (Exception error)
        {
            var code = error is ConversationException conversationError
                ? conversationError.Code
                : error is WorkerProtocolException protocolError
                    ? protocolError.Code
                    : error is WorkerUnavailableException
                        ? "worker_unavailable"
                        : "processing_failed";
            if (userAppended && sessionId is not null)
            {
                await TryAppendProcessingFailureAsync(sessionId).ConfigureAwait(false);
            }

            _diagnosticEmitter?.TryEmit(
                "error",
                "host.conversation",
                DiagnosticCodes.TurnFailed,
                durationMs: stopwatch.Elapsed.TotalMilliseconds,
                status: "failed",
                errorCode: code,
                exceptionKind: "unknown");

            await TryWriteTerminalErrorAsync(writer, code, sessionId).ConfigureAwait(false);
        }
        finally
        {
            writer.TryComplete(completionError);
        }
    }

    private async Task<(string SessionId, bool Created)?> ResolveSessionAsync(
        string? requestedSessionId,
        ChannelWriter<ChatEvent> writer,
        CancellationToken cancellationToken)
    {
        var candidate = requestedSessionId?.Trim() ?? string.Empty;
        if (candidate.Length > 0
            && await _sessions.GetAsync(candidate, cancellationToken).ConfigureAwait(false) is not null)
        {
            return (candidate, false);
        }

        var created = await _sessions.CreateAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(created))
        {
            await WriteAsync(
                writer,
                Error("session_create_failed", "Không tạo được session mới."),
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        await WriteAsync(
            writer,
            new ChatEvent("session", SessionId: created),
            cancellationToken).ConfigureAwait(false);
        return (created, true);
    }

    private async Task<VtsCatalog?> TryGetCatalogAsync(
        string requestId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _worker.GetVtsCatalogAsync(
                $"{requestId}-catalog",
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is WorkerProtocolException or WorkerUnavailableException)
        {
            return null;
        }
    }

    private async Task TryRememberAsync(
        SettingsSnapshot settings,
        string prompt,
        string response,
        CancellationToken cancellationToken)
    {
        if (!settings.EnableSelfLearning)
        {
            return;
        }

        try
        {
            await _memory.RememberTurnAsync(prompt, response, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Memory persistence is best effort and must not fail the user's reply.
        }
    }

    private async Task TryTriggerVtsAsync(
        string requestId,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken)
    {
        if (codes.Count == 0)
        {
            return;
        }

        try
        {
            await _worker.TriggerVtsActionsAsync(
                $"{requestId}-action",
                codes,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is WorkerProtocolException or WorkerUnavailableException)
        {
            // Avatar state is private and non-critical to the chat response.
        }
    }

    private void TryEnqueueLearning(SettingsSnapshot settings, string prompt, string response)
    {
        if (!settings.EnableSelfLearning || string.IsNullOrWhiteSpace(prompt) || string.IsNullOrWhiteSpace(response))
        {
            return;
        }

        try
        {
            _memory.EnqueueLearning(prompt, response);
        }
        catch
        {
            // Background learning is best effort.
        }
    }

    private Task TryAppendActivityAsync(
        string sessionId,
        string prompt,
        string response,
        string? emotion)
    {
        return PersistActivityAsync();

        async Task PersistActivityAsync()
        {
            try
            {
                await _activity.AppendAsync(
                    Activity("user", sessionId, prompt, null),
                    CancellationToken.None).ConfigureAwait(false);
                await _activity.AppendAsync(
                    Activity("assistant", sessionId, response, emotion),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Activity history must not replace a completed chat response.
            }
        }
    }

    private async Task TryAppendProcessingFailureAsync(string sessionId)
    {
        try
        {
            await _sessions.AppendAsync(
                sessionId,
                [new SessionMessage("ai", ProcessingHistoryErrorText)],
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original redacted failure code.
        }
    }

    private static async Task TryWriteTerminalErrorAsync(
        ChannelWriter<ChatEvent> writer,
        string code,
        string? sessionId)
    {
        try
        {
            await writer.WriteAsync(
                Error(code, ProcessingPublicErrorText, sessionId),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
        }
    }

    private static async Task ReconcileFinalVisibleAsync(
        ChannelWriter<ChatEvent> writer,
        string streamed,
        string final,
        CancellationToken cancellationToken)
    {
        if (string.Equals(streamed.TrimEnd(), final, StringComparison.Ordinal))
        {
            return;
        }

        if (final.StartsWith(streamed, StringComparison.Ordinal))
        {
            await WriteAsync(
                writer,
                new ChatEvent("chunk", final[streamed.Length..]),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteAsync(writer, new ChatEvent("reset"), cancellationToken).ConfigureAwait(false);
        if (final.Length > 0)
        {
            await WriteAsync(
                writer,
                new ChatEvent("chunk", final),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteVisibleDeltaAsync(
        ChannelWriter<ChatEvent> writer,
        string previous,
        string visible,
        CancellationToken cancellationToken)
    {
        if (visible.StartsWith(previous, StringComparison.Ordinal))
        {
            if (visible.Length > previous.Length)
            {
                await WriteAsync(
                    writer,
                    new ChatEvent("chunk", visible[previous.Length..]),
                    cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        await WriteAsync(writer, new ChatEvent("reset"), cancellationToken).ConfigureAwait(false);
        if (visible.Length > 0)
        {
            await WriteAsync(
                writer,
                new ChatEvent("chunk", visible),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static JsonObject BuildWorkerSettings(
        SettingsSnapshot settings,
        bool agentMode,
        IReadOnlyList<AgentToolCapability> modelTools) => new()
        {
            ["max_tokens"] = settings.MaxTokens,
            ["temperature"] = settings.Temperature,
            ["top_k"] = settings.TopK,
            ["allow_web_search"] = settings.AllowWebSearch,
            ["agent_mode"] = agentMode,
            ["model_tools"] = new JsonArray(modelTools.Select(tool => new JsonObject
            {
                ["tool_id"] = tool.ToolId,
                ["tool_version"] = tool.ToolVersion,
                ["scopes"] = new JsonArray(tool.Scopes
                    .Order(StringComparer.Ordinal)
                    .Select(scope => (JsonNode?)scope)
                    .ToArray()),
                ["availability"] = tool.Availability,
                ["model_enabled"] = tool.ModelEnabled,
                ["capability_class"] = string.Equals(
                    tool.ToolId,
                    "system.volume",
                    StringComparison.Ordinal)
                        ? "native_mutation"
                        : "internal",
            }).ToArray()),
        };

    private static bool ShouldShareMemory(SettingsSnapshot settings) =>
        string.Equals(settings.Backend, "llama.cpp", StringComparison.Ordinal)
        || settings.ShareMemoryWithCloud;

    private static string RenderMemory(IEnumerable<MemoryRecord> records) =>
        string.Join(
            Environment.NewLine,
            records.Select(record =>
                $"{record.Kind.ToString().ToLowerInvariant()}: {record.Content.Trim()}"));

    private static string BuildVtsInstruction(VtsCatalog? catalog)
    {
        if (catalog is null)
        {
            return string.Empty;
        }

        var codes = catalog.HotkeyCodes
            .Concat(catalog.ExpressionCodes)
            .Order(StringComparer.Ordinal)
            .Take(20)
            .ToArray();
        return codes.Length == 0
            ? string.Empty
            : "Private VTS codes: " + string.Join(", ", codes)
                + ". Append at most one terminal [[VTS:code]] suffix using only listed codes.";
    }

    private static ActivityEvent Activity(
        string role,
        string sessionId,
        string content,
        string? emotion)
    {
        var data = new JsonObject
        {
            ["role"] = role,
            ["content"] = content,
        };
        if (!string.IsNullOrWhiteSpace(emotion))
        {
            data["emotion"] = emotion;
        }

        return new ActivityEvent(0, string.Empty, "chat", sessionId, data);
    }

    private static string ReadRequiredString(
        JsonObject payload,
        string key,
        string errorCode)
    {
        if (payload[key] is JsonValue value
            && value.TryGetValue<string>(out var result))
        {
            return result;
        }

        throw new ConversationException(errorCode);
    }

    private static string ReadWorkerErrorCode(JsonObject payload) =>
        payload["code"] is JsonValue value
        && value.TryGetValue<string>(out var code)
        && !string.IsNullOrWhiteSpace(code)
            ? code
            : "worker_error";

    private static ChatEvent Error(string code, string content, string? sessionId = null) =>
        new("error", content, sessionId, code);

    private static ValueTask WriteAsync(
        ChannelWriter<ChatEvent> writer,
        ChatEvent value,
        CancellationToken cancellationToken) =>
        writer.WriteAsync(value, cancellationToken);

    private static async IAsyncEnumerable<ChatEvent> ReadEventsAsync(
        ChannelReader<ChatEvent> reader,
        Task producer,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in reader.ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            await producer.ConfigureAwait(false);
        }
    }

    private sealed class ConversationException : Exception
    {
        public ConversationException(string code)
            : base(code)
        {
            Code = code;
        }

        public string Code { get; }
    }
}
