using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuBackend.Business.Activity;
using RuBackend.Business.Sessions;
using RuBackend.Business.Settings;
using RuBackend.Conversation;
using RuBackend.Harness;
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
}
