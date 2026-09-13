using Microsoft.Extensions.Logging.Abstractions;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuBackend.Harness.Reconciliation;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class HarnessRecoveryStartupServiceTests
{
    [Fact]
    public async Task Transient_scan_fault_retries_and_still_frees_the_workspace()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var request = await fixture.SeedRunAsync(new HarnessRunStatus(HarnessRunState.WaitingApproval));
            var runId = request.Run.RunId;
            var store = new FlakyStore(fixture.EventStore);
            var service = new HarnessRecoveryStartupService(
                store,
                fixture.Options,
                NullLogger<HarnessRecoveryStartupService>.Instance);

            await service.StartAsync(CancellationToken.None);
            try
            {
                var recovered = await WaitForTerminalAsync(
                    fixture.EventStore,
                    runId,
                    TimeSpan.FromSeconds(15));

                Assert.True(recovered, "the parked run must still be recovered after a transient scan fault");
                var after = await fixture.EventStore.GetRunAsync(runId, CancellationToken.None);
                Assert.Equal(HarnessRunState.Failed, after!.State);
                Assert.Equal("approval_expired", after.TerminalErrorCode);
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task<bool> WaitForTerminalAsync(
        IHarnessEventStore store,
        RunId runId,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var run = await store.GetRunAsync(runId, CancellationToken.None);
            if (run is null || run.State.IsTerminal())
            {
                return true;
            }

            await Task.Delay(200);
        }

        return false;
    }

    /// <summary>
    /// One run read fails as it would when the Broker holds the database while the
    /// runtime starts: the sweep must report the parked run so the startup service
    /// retries instead of leaving the workspace locked until the next restart.
    /// </summary>
    private sealed class FlakyStore(IHarnessEventStore inner) : IHarnessEventStore
    {
        private int _failures;

        public Task<CreateRunResult> TryCreateRunAsync(
            NewAgentRun request,
            CancellationToken cancellationToken) =>
            inner.TryCreateRunAsync(request, cancellationToken);

        public Task<AgentRun?> GetRunAsync(RunId runId, CancellationToken cancellationToken)
        {
            if (++_failures == 1)
            {
                return Task.FromException<AgentRun?>(
                    new InvalidOperationException("database is locked"));
            }

            return inner.GetRunAsync(runId, cancellationToken);
        }

        public Task<HarnessCommitResult> CommitAsync(
            HarnessCommit commit,
            CancellationToken cancellationToken) =>
            inner.CommitAsync(commit, cancellationToken);

        public IAsyncEnumerable<HarnessEventEnvelope> ReadEventsAsync(
            RunId runId,
            long afterSequence,
            CancellationToken cancellationToken) =>
            inner.ReadEventsAsync(runId, afterSequence, cancellationToken);
    }
}
