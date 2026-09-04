using RuAgent.Harness.Persistence;
using RuBackend.Business.Sessions;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class HarnessSessionDeletionCoordinatorTests
{
    [Fact]
    public async Task Harness_artifacts_are_closed_and_removed_before_the_session_file()
    {
        var trace = new List<string>();
        var artifacts = new RecordingArtifactStore(trace);
        var sessions = new RecordingSessionStore(trace, deleteResult: true);
        var coordinator = new HarnessSessionDeletionCoordinator(
            artifacts,
            sessions,
            TimeProvider.System);

        var outcome = await coordinator.DeleteAsync(
            "11111111111111111111111111111111",
            CancellationToken.None);

        Assert.Equal(HarnessSessionDeletionOutcome.Deleted, outcome);
        Assert.Equal(["harness", "session"], trace);
    }

    [Fact]
    public async Task Broker_grants_are_revoked_before_the_session_file()
    {
        var trace = new List<string>();
        var artifacts = new RecordingArtifactStore(trace);
        var sessions = new RecordingSessionStore(trace, deleteResult: true);
        var gateways = new StubGatewayFactory(trace, revokeResult: true);
        var coordinator = new HarnessSessionDeletionCoordinator(
            artifacts,
            sessions,
            TimeProvider.System,
            gateways);

        var outcome = await coordinator.DeleteAsync(
            "11111111111111111111111111111111",
            CancellationToken.None);

        Assert.Equal(HarnessSessionDeletionOutcome.Deleted, outcome);
        Assert.Equal(["revoke:11111111111111111111111111111111", "harness", "session"], trace);
    }

    [Fact]
    public async Task Broker_revoke_rejection_fails_the_delete_closed()
    {
        var trace = new List<string>();
        var artifacts = new RecordingArtifactStore(trace);
        var sessions = new RecordingSessionStore(trace, deleteResult: true);
        var gateways = new StubGatewayFactory(trace, revokeResult: false);
        var coordinator = new HarnessSessionDeletionCoordinator(
            artifacts,
            sessions,
            TimeProvider.System,
            gateways);

        await Assert.ThrowsAsync<HarnessStoreException>(() => coordinator.DeleteAsync(
            "11111111111111111111111111111111",
            CancellationToken.None));
        Assert.DoesNotContain("session", trace);
    }

    [Fact]
    public async Task Harness_cleanup_failure_still_deletes_the_session_file()
    {
        var trace = new List<string>();
        var artifacts = new RecordingArtifactStore(trace)
        {
            Error = new HarnessStoreException("harness_store_unavailable"),
        };
        var sessions = new RecordingSessionStore(trace, deleteResult: true);
        var coordinator = new HarnessSessionDeletionCoordinator(
            artifacts,
            sessions,
            TimeProvider.System);

        var outcome = await coordinator.DeleteAsync(
            "11111111111111111111111111111111",
            CancellationToken.None);

        Assert.Equal(HarnessSessionDeletionOutcome.Deleted, outcome);
        Assert.Equal(["harness", "session"], trace);
    }

    private sealed class StubGatewayFactory(List<string> trace, bool revokeResult)
        : RuBackend.Harness.Broker.BrokerToolGatewayFactory(null)
    {
        public override Task<bool> RevokeSessionGrantsAsync(string sessionId, CancellationToken cancellationToken)
        {
            trace.Add("revoke:" + sessionId);
            return Task.FromResult(revokeResult);
        }
    }

    private sealed class RecordingArtifactStore(List<string> trace)
        : IHarnessSessionArtifactStore
    {
        public Exception? Error { get; init; }

        public Task<HarnessSessionArtifactDeleteResult> CloseAndDeleteAsync(
            string sessionId,
            DateTimeOffset deletedAt,
            CancellationToken cancellationToken)
        {
            trace.Add("harness");
            return Error is null
                ? Task.FromResult(new HarnessSessionArtifactDeleteResult(1, 1))
                : Task.FromException<HarnessSessionArtifactDeleteResult>(Error);
        }
    }

    private sealed class RecordingSessionStore(List<string> trace, bool deleteResult)
        : ISessionStore
    {
        public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SessionSummary>>([]);

        public Task<SessionDocument?> GetAsync(
            string sessionId,
            CancellationToken cancellationToken) =>
            Task.FromResult<SessionDocument?>(null);

        public Task<string?> CreateAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task<bool> DeleteAsync(
            string sessionId,
            CancellationToken cancellationToken)
        {
            trace.Add("session");
            return Task.FromResult(deleteResult);
        }

        public Task<bool> AppendAsync(
            string sessionId,
            IReadOnlyList<SessionMessage> messages,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<SessionAppendOutcome> AppendIfAbsentAsync(
            string sessionId,
            SessionMessage message,
            CancellationToken cancellationToken) =>
            Task.FromResult(SessionAppendOutcome.SessionNotFound);

        public Task<IReadOnlyList<ConversationTurn>> GetContextTurnsAsync(
            string sessionId,
            int maxTurns,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ConversationTurn>>([]);
    }
}
