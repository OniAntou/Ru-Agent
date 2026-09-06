using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence;
using RuBackend.Business;
using RuBackend.Business.Sessions;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentOutboxDeliveryTests
{
    [Fact]
    public async Task Transient_session_failure_retries_then_delivers_once()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        await SeedOutboxAsync(fixture, ConversationOutboxRole.User, "visible prompt");
        var (innerSessions, _) = await CreateSessionStoreAsync(fixture, createSession: true);
        var sessions = new SequencedSessionStore(innerSessions, transientFailures: 1);
        var clock = new MutableTimeProvider(BackendHarnessTestFixture.Timestamp.AddMinutes(1));
        var sink = new RecordingTransitionSink();
        var dispatcher = Dispatcher(fixture, sessions, sink, clock);

        var retry = await dispatcher.DispatchOnceAsync(CancellationToken.None);
        clock.Set(retry.NextAttemptAt!.Value);
        var delivered = await dispatcher.DispatchOnceAsync(CancellationToken.None);
        var document = await innerSessions.GetAsync(SessionIdValue, CancellationToken.None);

        Assert.Equal(AgentOutboxDispatchOutcome.RetryScheduled, retry.Outcome);
        Assert.Equal(AgentOutboxDispatchOutcome.Delivered, delivered.Outcome);
        Assert.Equal("visible prompt", Assert.Single(document!.Messages).Content);
        Assert.Equal(HistoryWaitKind.UserBeforeFirstStep, Assert.Single(sink.Acknowledgements).Kind);
    }

    [Fact]
    public async Task Retry_after_append_before_outbox_ack_does_not_duplicate_visible_history()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var run = await SeedOutboxAsync(
            fixture,
            ConversationOutboxRole.User,
            "crash-safe prompt");
        var (sessions, _) = await CreateSessionStoreAsync(fixture, createSession: true);
        var outbox = new HarnessOutboxStore(
            fixture.Options,
            leaseDuration: TimeSpan.FromSeconds(5));
        var now = BackendHarnessTestFixture.Timestamp.AddMinutes(1);
        var abandoned = await outbox.ClaimNextAsync(now, CancellationToken.None);
        await sessions.AppendIfAbsentAsync(
            SessionIdValue,
            new SessionMessage(
                "user",
                "crash-safe prompt",
                abandoned!.IntentId.Value,
                run.RunId.Value),
            CancellationToken.None);
        var clock = new MutableTimeProvider(now.AddSeconds(6));
        var sink = new RecordingTransitionSink();
        var dispatcher = Dispatcher(fixture, sessions, sink, clock, outbox);

        var result = await dispatcher.DispatchOnceAsync(CancellationToken.None);
        var document = await sessions.GetAsync(SessionIdValue, CancellationToken.None);

        Assert.Equal(AgentOutboxDispatchOutcome.Delivered, result.Outcome);
        Assert.Single(document!.Messages);
        Assert.Equal(2, result.AttemptCount);
        Assert.Single(sink.Acknowledgements);
    }

    [Fact]
    public async Task Deleted_session_durably_fails_before_history_acknowledgement()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        await SeedOutboxAsync(fixture, ConversationOutboxRole.User, "orphan prompt");
        var (sessions, _) = await CreateSessionStoreAsync(fixture, createSession: false);
        var sink = new RecordingTransitionSink();
        var dispatcher = Dispatcher(
            fixture,
            sessions,
            sink,
            new MutableTimeProvider(BackendHarnessTestFixture.Timestamp.AddMinutes(1)));

        var result = await dispatcher.DispatchOnceAsync(CancellationToken.None);

        Assert.Equal(AgentOutboxDispatchOutcome.RunFailed, result.Outcome);
        Assert.Empty(sink.Acknowledgements);
        Assert.Equal("session_not_found", Assert.Single(sink.Failures).ErrorCode);
        Assert.Null(await new HarnessOutboxStore(fixture.Options).ClaimNextAsync(
            BackendHarnessTestFixture.Timestamp.AddHours(1),
            CancellationToken.None));
    }

    [Fact]
    public async Task Final_answer_remains_pending_while_session_delivery_is_transient()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        await SeedOutboxAsync(fixture, ConversationOutboxRole.Assistant, "final answer");
        var (innerSessions, _) = await CreateSessionStoreAsync(fixture, createSession: true);
        var sessions = new SequencedSessionStore(innerSessions, transientFailures: 1);
        var sink = new RecordingTransitionSink();
        var clock = new MutableTimeProvider(BackendHarnessTestFixture.Timestamp.AddMinutes(1));
        var dispatcher = Dispatcher(fixture, sessions, sink, clock);

        var retry = await dispatcher.DispatchOnceAsync(CancellationToken.None);
        var stillWaiting = await fixture.EventStore.GetRunAsync(
            RunIdValue,
            CancellationToken.None);

        Assert.Equal(AgentOutboxDispatchOutcome.RetryScheduled, retry.Outcome);
        Assert.Empty(sink.Acknowledgements);
        Assert.Empty((await innerSessions.GetAsync(SessionIdValue, CancellationToken.None))!.Messages);
        Assert.Equal(HarnessRunState.WaitingHistory, stillWaiting!.State);
        Assert.Equal(HistoryWaitKind.FinalBeforeCompletion, stillWaiting.HistoryWaitKind);
    }

    [Fact]
    public async Task Acknowledged_outbox_recovers_a_crash_before_run_transition()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        await SeedOutboxAsync(fixture, ConversationOutboxRole.User, "recover transition");
        var (sessions, _) = await CreateSessionStoreAsync(fixture, createSession: true);
        var sink = new RecordingTransitionSink { ThrowNextAcknowledgement = true };
        var dispatcher = Dispatcher(
            fixture,
            sessions,
            sink,
            new MutableTimeProvider(BackendHarnessTestFixture.Timestamp.AddMinutes(1)));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispatcher.DispatchOnceAsync(CancellationToken.None));
        var recovered = await dispatcher.DispatchOnceAsync(CancellationToken.None);
        var document = await sessions.GetAsync(SessionIdValue, CancellationToken.None);

        Assert.Equal(AgentOutboxDispatchOutcome.TransitionRecovered, recovered.Outcome);
        Assert.Single(document!.Messages);
        Assert.Equal(2, sink.Acknowledgements.Count);
    }

    private static AgentConversationOutboxDispatcher Dispatcher(
        BackendHarnessTestFixture fixture,
        ISessionStore sessions,
        IAgentHistoryTransitionSink sink,
        TimeProvider clock,
        HarnessOutboxStore? outbox = null) =>
        new(
            outbox ?? new HarnessOutboxStore(fixture.Options),
            fixture.PayloadStore,
            sessions,
            new HarnessModelContextBootstrapper(
                sessions,
                fixture.PayloadStore,
                new CryptographicIdGenerator(),
                clock),
            sink,
            clock);

    private static async Task<AgentRun> SeedOutboxAsync(
        BackendHarnessTestFixture fixture,
        ConversationOutboxRole role,
        string content)
    {
        var payloadId = new PayloadId("payload_22222222222222222222222222222222");
        var bytes = Encoding.UTF8.GetBytes(content);
        var payload = new HarnessPayloadIntent(
            payloadId,
            role == ConversationOutboxRole.User
                ? HarnessPayloadKind.UserPrompt
                : HarnessPayloadKind.AssistantText,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            Math.Max(1, bytes.Length),
            bytes);
        var waitKind = role == ConversationOutboxRole.User
            ? HistoryWaitKind.UserBeforeFirstStep
            : HistoryWaitKind.FinalBeforeCompletion;
        var outbox = new ConversationOutboxIntent(
            new RuAgent.Harness.Core.EventId(
                "evt_22222222222222222222222222222222"),
            new SessionId(SessionIdValue),
            payloadId,
            role,
            waitKind);
        if (role == ConversationOutboxRole.User)
        {
            var request = await fixture.SeedRunAsync(
                new HarnessRunStatus(HarnessRunState.WaitingHistory, waitKind),
                [payload],
                [outbox]);
            return request.Run;
        }

        var initial = await fixture.SeedRunAsync(
            new HarnessRunStatus(HarnessRunState.Running));
        var next = new AgentRun(
            initial.Run.RunId,
            initial.Run.SessionId,
            initial.Run.WorkspaceId,
            new HarnessRunStatus(HarnessRunState.WaitingHistory, waitKind),
            version: 1,
            lastSequence: 2,
            initial.Run.BudgetWindow,
            initial.Run.ModelStepsInWindow,
            initial.Run.ToolCallsInWindow,
            initial.Run.Preset,
            initial.Run.PolicySnapshotVersion,
            initial.Run.PolicySnapshotSha256,
            initial.Run.CreatedAt,
            initial.Run.UpdatedAt.AddSeconds(1));
        var committed = await fixture.EventStore.CommitAsync(
            new HarnessCommit(
                initial.Run.RunId,
                expectedVersion: 0,
                next,
                [new HarnessEventIntent(
                    new RuAgent.Harness.Core.EventId(
                        "evt_33333333333333333333333333333333"),
                    HarnessEventKind.ModelStepCompleted,
                    payloadId: payloadId)],
                [payload],
                [outbox]),
            CancellationToken.None);
        Assert.Equal(HarnessCommitOutcome.Committed, committed.Outcome);
        return next;
    }

    private static async Task<(SessionStore Store, DataPaths Paths)> CreateSessionStoreAsync(
        BackendHarnessTestFixture fixture,
        bool createSession)
    {
        var paths = new DataPaths(
            Path.Combine(fixture.RootPath, "memory.db"),
            Path.Combine(fixture.RootPath, "settings.json"),
            Path.Combine(fixture.RootPath, "sessions"),
            Path.Combine(fixture.RootPath, "activity.jsonl"));
        var store = new SessionStore(paths, new MutableTimeProvider(
            BackendHarnessTestFixture.Timestamp));
        if (createSession)
        {
            Directory.CreateDirectory(paths.SessionDirectory);
            var document = new SessionDocument(
                SessionIdValue,
                "Agent",
                BackendHarnessTestFixture.Timestamp,
                BackendHarnessTestFixture.Timestamp,
                []);
            await File.WriteAllTextAsync(
                Path.Combine(paths.SessionDirectory, $"{SessionIdValue}.json"),
                JsonSerializer.Serialize(document));
        }

        return (store, paths);
    }

    private static readonly RunId RunIdValue =
        new("run_11111111111111111111111111111111");

    private const string SessionIdValue = "11111111111111111111111111111111";

    private sealed class MutableTimeProvider(DateTimeOffset current) : TimeProvider
    {
        public DateTimeOffset Current { get; private set; } = current;

        public override DateTimeOffset GetUtcNow() => Current;

        public void Set(DateTimeOffset current) => Current = current;
    }

    private sealed class RecordingTransitionSink : IAgentHistoryTransitionSink
    {
        public List<(RunId RunId, HistoryWaitKind Kind)> Acknowledgements { get; } = [];

        public List<(RunId RunId, string ErrorCode)> Failures { get; } = [];

        public bool ThrowNextAcknowledgement { get; set; }

        public Task AcknowledgeAsync(
            RunId runId,
            HistoryWaitKind historyWaitKind,
            CancellationToken cancellationToken)
        {
            Acknowledgements.Add((runId, historyWaitKind));
            if (ThrowNextAcknowledgement)
            {
                ThrowNextAcknowledgement = false;
                throw new InvalidOperationException("simulated_crash");
            }

            return Task.CompletedTask;
        }

        public Task FailAsync(
            RunId runId,
            string errorCode,
            CancellationToken cancellationToken)
        {
            Failures.Add((runId, errorCode));
            return Task.CompletedTask;
        }
    }

    private sealed class SequencedSessionStore(
        ISessionStore inner,
        int transientFailures) : ISessionStore
    {
        private int _transientFailures = transientFailures;

        public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken cancellationToken) =>
            inner.ListAsync(cancellationToken);

        public Task<SessionDocument?> GetAsync(
            string sessionId,
            CancellationToken cancellationToken) =>
            inner.GetAsync(sessionId, cancellationToken);

        public Task<string?> CreateAsync(CancellationToken cancellationToken) =>
            inner.CreateAsync(cancellationToken);

        public Task<bool> DeleteAsync(
            string sessionId,
            CancellationToken cancellationToken) =>
            inner.DeleteAsync(sessionId, cancellationToken);

        public Task<bool> AppendAsync(
            string sessionId,
            IReadOnlyList<SessionMessage> messages,
            CancellationToken cancellationToken) =>
            inner.AppendAsync(sessionId, messages, cancellationToken);

        public Task<SessionAppendOutcome> AppendIfAbsentAsync(
            string sessionId,
            SessionMessage message,
            CancellationToken cancellationToken)
        {
            if (_transientFailures-- > 0)
            {
                return Task.FromResult(SessionAppendOutcome.TransientFailure);
            }

            return inner.AppendIfAbsentAsync(sessionId, message, cancellationToken);
        }

        public Task<IReadOnlyList<ConversationTurn>> GetContextTurnsAsync(
            string sessionId,
            int maxTurns,
            CancellationToken cancellationToken) =>
            inner.GetContextTurnsAsync(sessionId, maxTurns, cancellationToken);
    }
}
