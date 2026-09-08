using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuBackend.Business.Activity;
using RuBackend.Business.Sessions;
using RuBackend.Business.Settings;
using RuBackend.Conversation;
using RuBackend.Harness;
using RuBackend.Memory;
using RuBackend.Worker;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentReplyPolicyTests
{
    private static readonly DateTimeOffset Epoch =
        new(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Policy_sanitizes_visible_text_and_accepts_only_catalog_codes()
    {
        var worker = new FakeWorker
        {
            Catalog = new VtsCatalog(
                new HashSet<string>(["smile"], StringComparer.Ordinal),
                new HashSet<string>(["expr"], StringComparer.Ordinal)),
        };
        var policy = new AgentReplyPolicy(new MetadataPolicy(), new WorkerAgentVtsClient(worker));

        var reply = await policy.PrepareAsync(
            Run(),
            "Hay quá [[EMO:joy]] [[VTS:smile,expr]] [[VOICE_PACE:fast]]",
            CancellationToken.None);

        Assert.Equal("Hay quá", reply.VisibleText);
        Assert.Equal(["smile", "expr"], reply.VtsCodes);
        Assert.Equal("joy", reply.Emotion);
    }

    [Fact]
    public async Task Policy_keeps_text_safe_when_vts_catalog_is_unavailable()
    {
        var policy = new AgentReplyPolicy(new MetadataPolicy(), new WorkerAgentVtsClient(new FakeWorker
        {
            CatalogError = new WorkerProtocolException("vts_unavailable"),
        }));

        var reply = await policy.PrepareAsync(
            Run(),
            "Em nghe đây [[VTS:smile]] [[EMO:joy]]",
            CancellationToken.None);

        Assert.Equal("Em nghe đây", reply.VisibleText);
        Assert.Empty(reply.VtsCodes);
        Assert.Equal("joy", reply.Emotion);
    }

    [Fact]
    public async Task Effect_sink_triggers_vts_and_deduplicates_the_same_model_step()
    {
        var worker = new FakeWorker();
        var activity = new FakeActivityLogStore();
        var sink = new AgentReplyEffectSink(new WorkerAgentVtsClient(worker), activity);
        var reply = new AgentReplyPresentation("visible", ["smile"], "joy");

        await sink.ApplyAsync(Run(), reply, new ModelStepId("step_11111111111111111111111111111111"), CancellationToken.None);
        await sink.ApplyAsync(Run(), reply, new ModelStepId("step_11111111111111111111111111111111"), CancellationToken.None);

        Assert.Equal(["smile"], worker.TriggeredCodes);
        var evt = Assert.Single(activity.Events);
        Assert.Equal("agent", evt.Type);
        Assert.Equal(
            "agent:run_11111111111111111111111111111111:step_11111111111111111111111111111111:assistant",
            evt.EventId);
        Assert.Equal("visible", evt.Data["content"]!.GetValue<string>());
        Assert.Equal("joy", evt.Data["emotion"]!.GetValue<string>());
    }

    [Fact]
    public async Task User_activity_projector_is_idempotent_and_uses_safe_agent_shape()
    {
        var activity = new FakeActivityLogStore();
        var projector = new AgentConversationActivityProjector(activity);
        var intent = new EventId("evt_11111111111111111111111111111111");
        var run = Run();

        await projector.ProjectUserAsync(
            intent,
            run.RunId,
            run.SessionId.Value,
            "Xin chào [[VTS:private]]",
            CancellationToken.None);
        await projector.ProjectUserAsync(
            intent,
            run.RunId,
            run.SessionId.Value,
            "different replay",
            CancellationToken.None);

        var evt = Assert.Single(activity.Events);
        Assert.Equal("agent", evt.Type);
        Assert.Equal("user", evt.Data["role"]!.GetValue<string>());
        Assert.Equal("Xin chào [[VTS:private]]", evt.Data["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Assistant_activity_projector_is_idempotent_and_uses_safe_agent_shape()
    {
        var activity = new FakeActivityLogStore();
        var projector = new AgentConversationActivityProjector(activity);
        var intent = new EventId("evt_22222222222222222222222222222222");
        var run = Run();
        await projector.ProjectAssistantAsync(intent, run.RunId, run.SessionId.Value, "Done", CancellationToken.None);
        await projector.ProjectAssistantAsync(intent, run.RunId, run.SessionId.Value, "different replay", CancellationToken.None);
        var evt = Assert.Single(activity.Events);
        Assert.Equal("agent", evt.Type);
        Assert.Equal("assistant", evt.Data["role"]!.GetValue<string>());
    }

    [Fact]
    public void Transient_redactor_holds_private_markers_and_redacts_split_secrets()
    {
        var redactor = new AgentReplyTransientRedactor(
            new RuAgent.Harness.Persistence.Privacy.KnownSecretRedactor(["split-secret"]),
            new StreamSanitizer());
        var session = redactor.CreateSession();

        var visible = session.Append("before split-");
        visible += session.Append("secret [[VT");
        visible += session.Append("S:smile]]");
        visible += session.Complete();

        Assert.Equal("before [REDACTED] ", visible);
        Assert.DoesNotContain("[[VTS:", visible, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("split-secret", visible, StringComparison.Ordinal);
    }

    [Fact]
    public void Transient_redactor_holds_marker_at_start_and_unterminated_marker()
    {
        var redactor = new AgentReplyTransientRedactor(
            RuAgent.Harness.Persistence.Privacy.KnownSecretRedactor.Empty,
            new StreamSanitizer());

        var markerAtStart = redactor.CreateSession();
        var first = markerAtStart.Append("[[VT");
        first += markerAtStart.Append("S:smile]]");
        first += markerAtStart.Complete();
        Assert.Equal(string.Empty, first);

        var unterminated = redactor.CreateSession();
        var second = unterminated.Append("Xin chào [[VTS:smile");
        second += unterminated.Complete();
        Assert.Equal("Xin chào ", second);
        Assert.DoesNotContain("[[VTS:", second, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Transient_redactor_preserves_utf8_and_prefix_only_append_invariant()
    {
        var redactor = new AgentReplyTransientRedactor(
            RuAgent.Harness.Persistence.Privacy.KnownSecretRedactor.Empty,
            new StreamSanitizer());
        var session = redactor.CreateSession();

        var text = session.Append("Xin chào");
        text += session.Append("Xin chào anh");
        text += session.Complete();

        Assert.Equal("Xin chào anh", text);
    }

    [Fact]
    public async Task Memory_context_provider_obeys_sharing_setting_and_renders_records()
    {
        var memory = new FakeMemoryService
        {
            Records = [Memory("preference", "trả lời ngắn")],
        };
        var provider = new AgentMemoryContextProvider(memory, new FakeSettingsStore
        {
            ShareMemoryWithCloud = true,
        });
        var session = new SessionDocument(
            "session-1",
            "Test",
            Epoch,
            Epoch,
            [new SessionMessage("user", "nhắc lại sở thích của anh")]);

        var context = await provider.BuildAsync(session, CancellationToken.None);

        Assert.Equal("Relevant memory:\npreference: trả lời ngắn", context);
        Assert.Equal("nhắc lại sở thích của anh", memory.LastQuery);
    }

    [Fact]
    public async Task Memory_context_provider_skips_retrieval_when_sharing_is_disabled()
    {
        var memory = new FakeMemoryService
        {
            Records = [Memory("preference", "không được dùng")],
        };
        var provider = new AgentMemoryContextProvider(memory, new FakeSettingsStore
        {
            ShareMemoryWithCloud = false,
            Backend = "openai",
        });
        var session = new SessionDocument(
            "session-1",
            "Test",
            Epoch,
            Epoch,
            [new SessionMessage("user", "hỏi lại")]);

        Assert.Null(await provider.BuildAsync(session, CancellationToken.None));
        Assert.Null(memory.LastQuery);
    }

    [Fact]
    public async Task Memory_projector_remembers_and_enqueues_each_intent_once()
    {
        var memory = new FakeMemoryService();
        var sessions = new FakeSessionStore(
            [new ConversationTurn("prompt", "response")]);
        var projector = new AgentConversationMemoryProjector(
            memory,
            new FakeSettingsStore { EnableSelfLearning = true },
            sessions);
        var intent = new EventId("evt_11111111111111111111111111111111");

        await projector.ProjectAsync(intent, "session-1", CancellationToken.None);
        await projector.ProjectAsync(intent, "session-1", CancellationToken.None);

        Assert.Equal(1, memory.RememberCalls);
        Assert.Equal(1, memory.EnqueueCalls);
        Assert.Equal(("prompt", "response"), Assert.Single(memory.Remembered));
    }

    [Fact]
    public async Task Memory_projector_skips_disabled_learning_and_tolerates_store_failure()
    {
        var disabledMemory = new FakeMemoryService();
        var disabledProjector = new AgentConversationMemoryProjector(
            disabledMemory,
            new FakeSettingsStore { EnableSelfLearning = false },
            new FakeSessionStore([new ConversationTurn("prompt", "response")]));
        await disabledProjector.ProjectAsync(
            new EventId("evt_22222222222222222222222222222222"),
            "session-1",
            CancellationToken.None);
        Assert.Equal(0, disabledMemory.RememberCalls);

        var failingMemory = new FakeMemoryService
        {
            RememberError = new InvalidOperationException("memory_down"),
        };
        var failingProjector = new AgentConversationMemoryProjector(
            failingMemory,
            new FakeSettingsStore(),
            new FakeSessionStore([new ConversationTurn("prompt", "response")]));
        await failingProjector.ProjectAsync(
            new EventId("evt_33333333333333333333333333333333"),
            "session-1",
            CancellationToken.None);
        Assert.Equal(1, failingMemory.RememberCalls);
        Assert.Equal(0, failingMemory.EnqueueCalls);
    }

    [Fact]
    public async Task Memory_projector_treats_missing_session_turn_as_empty_projection()
    {
        var memory = new FakeMemoryService();
        var projector = new AgentConversationMemoryProjector(
            memory,
            new FakeSettingsStore(),
            new FakeSessionStore([]));

        await projector.ProjectAsync(
            new EventId("evt_44444444444444444444444444444444"),
            "session-1",
            CancellationToken.None);

        Assert.Equal(0, memory.RememberCalls);
    }

    private static AgentRun Run() => new(
        new RunId("run_11111111111111111111111111111111"),
        new SessionId("11111111111111111111111111111111"),
        new WorkspaceId("ws_11111111111111111111111111111111"),
        new HarnessRunStatus(HarnessRunState.Running),
        1,
        1,
        1,
        0,
        0,
        AgentPermissionPreset.Standard,
        1,
        new string('a', 64),
        Epoch,
        Epoch);

    private static MemoryRecord Memory(string kind, string content) => new(
        "memory-1",
        Enum.Parse<MemoryKind>(kind, true),
        content,
        null,
        "fixture",
        0.9,
        0.8,
        Epoch,
        Epoch,
        null,
        null,
        "active",
        false);

    private sealed class FakeWorker : IVtsProtocolClient
    {
        public VtsCatalog? Catalog { get; init; }
        public Exception? CatalogError { get; init; }
        public IReadOnlyList<string> TriggeredCodes { get; private set; } = [];

        public Task<VtsCatalog?> GetVtsCatalogAsync(
            string requestId,
            CancellationToken cancellationToken) =>
            CatalogError is null
                ? Task.FromResult(Catalog)
                : Task.FromException<VtsCatalog?>(CatalogError);

        public Task TriggerVtsActionsAsync(
            string requestId,
            IReadOnlyList<string> codes,
            CancellationToken cancellationToken)
        {
            TriggeredCodes = codes.ToArray();
            return Task.CompletedTask;
        }

    }

    private sealed class FakeActivityLogStore : IActivityLogStore
    {
        public List<ActivityEvent> Events { get; } = [];

        public Task AppendAsync(ActivityEvent activity, CancellationToken cancellationToken)
        {
            Events.Add(activity);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ActivityEvent>> ReadAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ActivityEvent>>(Events);

        public Task ClearAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeMemoryService : IMemoryService
    {
        public IReadOnlyList<MemoryRecord> Records { get; init; } = [];
        public Exception? RetrieveError { get; init; }
        public Exception? RememberError { get; init; }
        public string? LastQuery { get; private set; }
        public int RememberCalls { get; private set; }
        public int EnqueueCalls { get; private set; }
        public List<(string Prompt, string Response)> Remembered { get; } = [];

        public Task<IReadOnlyList<MemoryRecord>> ListActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Records);

        public Task<IReadOnlyList<MemoryRecord>> ListActiveAsync(int limit, string? cursor, CancellationToken cancellationToken) =>
            Task.FromResult(Records);

        public Task<MemoryRecord?> GetAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(Records.FirstOrDefault(record => record.Id == id));

        public Task<IReadOnlyList<MemoryRecord>> RetrieveAsync(string query, CancellationToken cancellationToken)
        {
            LastQuery = query;
            return RetrieveError is null
                ? Task.FromResult(Records)
                : Task.FromException<IReadOnlyList<MemoryRecord>>(RetrieveError);
        }

        public Task<IReadOnlyList<MemoryRecord>> RememberTurnAsync(string prompt, string response, CancellationToken cancellationToken)
        {
            RememberCalls++;
            Remembered.Add((prompt, response));
            return RememberError is null
                ? Task.FromResult<IReadOnlyList<MemoryRecord>>([])
                : Task.FromException<IReadOnlyList<MemoryRecord>>(RememberError);
        }

        public void EnqueueLearning(string prompt, string response) => EnqueueCalls++;

        public Task ClearAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeSettingsStore : ISettingsStore
    {
        public bool EnableSelfLearning { get; init; } = true;
        public bool ShareMemoryWithCloud { get; init; } = true;
        public string Backend { get; init; } = "llama.cpp";

        public Task<SettingsSnapshot> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SettingsSnapshot(
                new JsonObject(),
                "OpenCode Zen",
                Backend,
                "open/free",
                "kilo/free",
                "Default",
                EnableSelfLearning,
                ShareMemoryWithCloud,
                false,
                "Ru (OmniVoice)",
                "original",
                1.0,
                false,
                true,
                false,
                "#FFFFFF",
                "medium",
                2.0,
                "auto",
                -1,
                -1,
                "qwen2.5:latest",
                "default",
                "default",
                "http://localhost:11434/v1",
                "http://localhost:1234/v1",
                "http://localhost:8000/v1",
                string.Empty,
                string.Empty));

        public Task SaveAsync(SettingsSnapshot candidate, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FakeSessionStore(IReadOnlyList<ConversationTurn> turns) : ISessionStore
    {
        public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SessionSummary>>([]);

        public Task<SessionDocument?> GetAsync(string sessionId, CancellationToken cancellationToken) =>
            Task.FromResult<SessionDocument?>(null);

        public Task<string?> CreateAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task<bool> DeleteAsync(string sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> AppendAsync(string sessionId, IReadOnlyList<SessionMessage> messages, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<SessionAppendOutcome> AppendIfAbsentAsync(string sessionId, SessionMessage message, CancellationToken cancellationToken) =>
            Task.FromResult(SessionAppendOutcome.Appended);

        public Task<IReadOnlyList<ConversationTurn>> GetContextTurnsAsync(string sessionId, int maxTurns, CancellationToken cancellationToken) =>
            Task.FromResult(turns);
    }
}
