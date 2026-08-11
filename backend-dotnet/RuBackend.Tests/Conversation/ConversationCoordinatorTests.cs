using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using RuBackend.Business.Activity;
using RuBackend.Business.Sessions;
using RuBackend.Business.Settings;
using RuBackend.Conversation;
using RuBackend.Memory;
using RuBackend.Worker;
using Xunit;

namespace RuBackend.Tests.Conversation;

public sealed class ConversationCoordinatorTests
{
    [Fact]
    public async Task Successful_turn_preserves_order_and_never_publishes_private_metadata()
    {
        var log = new List<string>();
        var sessions = new FakeSessionStore(log);
        var memory = new FakeMemoryService(log);
        var worker = new FakeWorkerClient(log)
        {
            Events =
            [
                Event(WorkerMessageTypes.ChatReset, 0, []),
                Event(WorkerMessageTypes.ChatChunk, 1, new JsonObject { ["text"] = "Em nghe đây." }),
                Event(
                    WorkerMessageTypes.ChatDone,
                    2,
                    new JsonObject
                    {
                        ["raw_reply"] = "Em nghe đây. [[EMO:joy]] [[VTS:vts_01]] [[VOICE_PACE:quick]]",
                    }),
            ],
        };
        var coordinator = CreateCoordinator(log, sessions, memory, worker);

        var events = await CollectAsync(coordinator.HandleAsync(
            new ChatRequest("Anh gọi em. [[VTS:private_input]]", "session-1"),
            CancellationToken.None));

        Assert.Equal(["reset", "chunk", "done"], events.Select(item => item.Type));
        Assert.Equal("Em nghe đây.", events[1].Content);
        Assert.Equal("Em nghe đây.", events[2].Content);
        Assert.All(events, item =>
        {
            var serialized = System.Text.Json.JsonSerializer.Serialize(item);
            Assert.DoesNotContain("[[VTS:", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("VOICE_PACE", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("emotion", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("vts_01", serialized, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(
            [
                "session.get",
                "settings.read",
                "session.context",
                "memory.retrieve",
                "session.append.user",
                "worker.catalog",
                "worker.chat",
                "memory.remember",
                "session.append.ai",
                "worker.action",
                "memory.enqueue",
                "activity.user",
                "activity.assistant",
            ],
            log);
        Assert.Equal(
            ["Anh gọi em.", "Em nghe đây."],
            sessions.Messages.Select(message => message.Content));
        Assert.Equal("Anh gọi em.", memory.RememberedPrompt);
        Assert.Equal("Em nghe đây.", memory.RememberedResponse);
        Assert.Equal(["vts_01"], worker.TriggeredCodes);
        Assert.DoesNotContain("secret", worker.LastChatRequest!.Settings.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("preference: trả lời ngắn", worker.LastChatRequest.MemoryContext, StringComparison.Ordinal);
        Assert.Contains("vts_01", worker.LastChatRequest.VtsInstruction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_session_is_created_and_emitted_before_generation()
    {
        var log = new List<string>();
        var sessions = new FakeSessionStore(log) { ExistingSession = false };
        var worker = SuccessfulWorker(log);
        var coordinator = CreateCoordinator(log, sessions, new FakeMemoryService(log), worker);

        var events = await CollectAsync(coordinator.HandleAsync(
            new ChatRequest("Xin chào", "missing"),
            CancellationToken.None));

        Assert.Equal("session", events[0].Type);
        Assert.Equal("created-session", events[0].SessionId);
        Assert.Equal("done", events[^1].Type);
        Assert.True(log.IndexOf("session.create") < log.IndexOf("worker.chat"));
    }

    [Fact]
    public async Task Busy_turn_emits_one_error_without_appending_messages()
    {
        var log = new List<string>();
        var gate = new ConversationTurnGate(TimeSpan.FromMilliseconds(50));
        using var held = gate.TryEnterTurn();
        Assert.NotNull(held);
        var sessions = new FakeSessionStore(log);
        var coordinator = CreateCoordinator(
            log,
            sessions,
            new FakeMemoryService(log),
            SuccessfulWorker(log),
            gate);

        var events = await CollectAsync(coordinator.HandleAsync(
            new ChatRequest("Xin chào", "session-1"),
            CancellationToken.None));

        var error = Assert.Single(events);
        Assert.Equal("error", error.Type);
        Assert.Equal("busy", error.ErrorCode);
        Assert.Empty(sessions.Messages);
    }

    [Fact]
    public async Task Memory_timeout_emits_one_error_and_releases_the_turn_gate()
    {
        var log = new List<string>();
        var gate = new ConversationTurnGate(TimeSpan.FromMilliseconds(20));
        await using var heldMemory = await gate.EnterMemoryAsync(CancellationToken.None);
        Assert.NotNull(heldMemory);
        var coordinator = CreateCoordinator(
            log,
            new FakeSessionStore(log),
            new FakeMemoryService(log),
            SuccessfulWorker(log),
            gate);

        var events = await CollectAsync(coordinator.HandleAsync(
            new ChatRequest("Xin chào", "session-1"),
            CancellationToken.None));

        Assert.Equal("memory_busy", Assert.Single(events).ErrorCode);
        using var nextTurn = gate.TryEnterTurn();
        Assert.NotNull(nextTurn);
    }

    [Fact]
    public async Task Worker_error_appends_one_controlled_assistant_message_and_one_terminal_error()
    {
        var log = new List<string>();
        var sessions = new FakeSessionStore(log);
        var worker = new FakeWorkerClient(log)
        {
            Events =
            [
                Event(
                    WorkerMessageTypes.Error,
                    0,
                    new JsonObject { ["code"] = "generation_failed", ["retryable"] = true }),
            ],
        };
        var coordinator = CreateCoordinator(log, sessions, new FakeMemoryService(log), worker);

        var events = await CollectAsync(coordinator.HandleAsync(
            new ChatRequest("Xin chào", "session-1"),
            CancellationToken.None));

        var error = Assert.Single(events);
        Assert.Equal("error", error.Type);
        Assert.Equal("generation_failed", error.ErrorCode);
        Assert.Equal(2, sessions.Messages.Count);
        Assert.Equal(ConversationCoordinator.ProcessingHistoryErrorText, sessions.Messages[1].Content);
        Assert.Equal(1, sessions.Messages.Count(message => message.Role == "ai"));
    }

    [Fact]
    public async Task Cancellation_stops_the_worker_and_releases_both_gates()
    {
        var log = new List<string>();
        var gate = new ConversationTurnGate(TimeSpan.FromSeconds(1));
        var worker = new FakeWorkerClient(log) { BlockChat = true };
        var coordinator = CreateCoordinator(
            log,
            new FakeSessionStore(log),
            new FakeMemoryService(log),
            worker,
            gate);
        using var cancellation = new CancellationTokenSource();
        var collect = CollectAsync(coordinator.HandleAsync(
            new ChatRequest("Xin chào", "session-1"),
            cancellation.Token));
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collect);
        using var nextTurn = gate.TryEnterTurn();
        Assert.NotNull(nextTurn);
        nextTurn.Dispose();
        await using var nextMemory = await gate.EnterMemoryAsync(CancellationToken.None);
        Assert.NotNull(nextMemory);
    }

    [Fact]
    public async Task Memory_persistence_failure_is_nonfatal_and_done_is_still_emitted()
    {
        var log = new List<string>();
        var memory = new FakeMemoryService(log) { RememberError = new IOException("private memory") };
        var sessions = new FakeSessionStore(log);
        var coordinator = CreateCoordinator(log, sessions, memory, SuccessfulWorker(log));

        var events = await CollectAsync(coordinator.HandleAsync(
            new ChatRequest("Xin chào", "session-1"),
            CancellationToken.None));

        Assert.Equal("done", events[^1].Type);
        Assert.Equal(2, sessions.Messages.Count);
    }

    private static ConversationCoordinator CreateCoordinator(
        List<string> log,
        FakeSessionStore sessions,
        FakeMemoryService memory,
        FakeWorkerClient worker,
        ConversationTurnGate? gate = null) => new(
            sessions,
            memory,
            new FakeSettingsStore(log),
            new FakeActivityLogStore(log),
            worker,
            new MetadataPolicy(),
            new StreamSanitizer(),
            gate ?? new ConversationTurnGate(TimeSpan.FromSeconds(1)));

    private static FakeWorkerClient SuccessfulWorker(List<string> log) => new(log)
    {
        Events =
        [
            Event(WorkerMessageTypes.ChatDone, 0, new JsonObject { ["raw_reply"] = "Em nghe đây." }),
        ],
    };

    private static WorkerEvent Event(string type, long sequence, JsonObject payload) =>
        new("worker-request", type, sequence, payload, ReadOnlyMemory<byte>.Empty);

    private static async Task<List<ChatEvent>> CollectAsync(IAsyncEnumerable<ChatEvent> events)
    {
        var result = new List<ChatEvent>();
        await foreach (var item in events)
        {
            result.Add(item);
        }

        return result;
    }

    private sealed class FakeSessionStore : ISessionStore
    {
        private readonly List<string> _log;

        public FakeSessionStore(List<string> log)
        {
            _log = log;
        }

        public bool ExistingSession { get; set; } = true;
        public List<SessionMessage> Messages { get; } = [];

        public Task<SessionDocument?> GetAsync(string sessionId, CancellationToken cancellationToken)
        {
            _log.Add("session.get");
            return Task.FromResult<SessionDocument?>(ExistingSession
                ? new SessionDocument(sessionId, "Test", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, Messages)
                : null);
        }

        public Task<string?> CreateAsync(CancellationToken cancellationToken)
        {
            _log.Add("session.create");
            ExistingSession = true;
            return Task.FromResult<string?>("created-session");
        }

        public Task<bool> AppendAsync(
            string sessionId,
            IReadOnlyList<SessionMessage> messages,
            CancellationToken cancellationToken)
        {
            foreach (var message in messages)
            {
                _log.Add($"session.append.{message.Role}");
                Messages.Add(message);
            }

            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<ConversationTurn>> GetContextTurnsAsync(
            string sessionId,
            int maxTurns,
            CancellationToken cancellationToken)
        {
            _log.Add("session.context");
            Assert.Equal(8, maxTurns);
            return Task.FromResult<IReadOnlyList<ConversationTurn>>(
                [new ConversationTurn("Cũ", "Đã nhớ")]);
        }

        public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SessionSummary>>([]);

        public Task<bool> DeleteAsync(string sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class FakeMemoryService : IMemoryService
    {
        private readonly List<string> _log;

        public FakeMemoryService(List<string> log)
        {
            _log = log;
        }

        public Exception? RememberError { get; set; }
        public string? RememberedPrompt { get; private set; }
        public string? RememberedResponse { get; private set; }

        public Task<IReadOnlyList<MemoryRecord>> RetrieveAsync(
            string query,
            CancellationToken cancellationToken)
        {
            _log.Add("memory.retrieve");
            return Task.FromResult<IReadOnlyList<MemoryRecord>>([Memory("preference", "trả lời ngắn")]);
        }

        public Task<IReadOnlyList<MemoryRecord>> RememberTurnAsync(
            string prompt,
            string response,
            CancellationToken cancellationToken)
        {
            _log.Add("memory.remember");
            RememberedPrompt = prompt;
            RememberedResponse = response;
            return RememberError is null
                ? Task.FromResult<IReadOnlyList<MemoryRecord>>([])
                : Task.FromException<IReadOnlyList<MemoryRecord>>(RememberError);
        }

        public void EnqueueLearning(string prompt, string response)
        {
            _log.Add("memory.enqueue");
        }

        public Task ClearAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private static MemoryRecord Memory(string kind, string content) => new(
            "memory-1",
            Enum.Parse<MemoryKind>(kind, ignoreCase: true),
            content,
            null,
            "fixture",
            0.9,
            0.8,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            null,
            null,
            "active",
            false);
    }

    private sealed class FakeSettingsStore : ISettingsStore
    {
        private readonly List<string> _log;

        public FakeSettingsStore(List<string> log)
        {
            _log = log;
        }

        public Task<SettingsSnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            _log.Add("settings.read");
            return Task.FromResult(new SettingsSnapshot(
                new JsonObject { ["api_key"] = "secret" },
                "llama.cpp",
                "local",
                "open/free",
                "kilo/free",
                "Default",
                256,
                0.7,
                50,
                true,
                true,
                true,
                false,
                string.Empty,
                "Ru (OmniVoice)",
                "original",
                1.0,
                false,
                true,
                false));
        }

        public Task SaveAsync(SettingsSnapshot candidate, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FakeActivityLogStore : IActivityLogStore
    {
        private readonly List<string> _log;

        public FakeActivityLogStore(List<string> log)
        {
            _log = log;
        }

        public Task AppendAsync(ActivityEvent activity, CancellationToken cancellationToken)
        {
            _log.Add($"activity.{activity.Data["role"]!.GetValue<string>()}");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ActivityEvent>> ReadAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ActivityEvent>>([]);

        public Task ClearAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeWorkerClient : IWorkerClient
    {
        private readonly List<string> _log;

        public FakeWorkerClient(List<string> log)
        {
            _log = log;
        }

        public IReadOnlyList<WorkerEvent> Events { get; init; } = [];
        public bool BlockChat { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WorkerChatRequest? LastChatRequest { get; private set; }
        public IReadOnlyList<string> TriggeredCodes { get; private set; } = [];

        public Task<WorkerHello> ConnectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new WorkerHello(1, new HashSet<string> { "chat", "vts" }));

        public async IAsyncEnumerable<WorkerEvent> GenerateChatAsync(
            WorkerChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _log.Add("worker.chat");
            LastChatRequest = request;
            Started.TrySetResult();
            if (BlockChat)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            foreach (var item in Events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
        }

        public Task<VtsCatalog?> GetVtsCatalogAsync(string requestId, CancellationToken cancellationToken)
        {
            _log.Add("worker.catalog");
            return Task.FromResult<VtsCatalog?>(new VtsCatalog(
                new HashSet<string> { "vts_01" },
                new HashSet<string> { "expr_01" }));
        }

        public Task TriggerVtsActionsAsync(
            string requestId,
            IReadOnlyList<string> codes,
            CancellationToken cancellationToken)
        {
            _log.Add("worker.action");
            TriggeredCodes = codes.ToArray();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkerFact>> ExtractFactsAsync(
            string requestId,
            string prompt,
            string response,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkerFact>>([]);

        public Task CancelAsync(string requestId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<WorkerHealth> GetHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new WorkerHealth(true, 1, new HashSet<string>()));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
