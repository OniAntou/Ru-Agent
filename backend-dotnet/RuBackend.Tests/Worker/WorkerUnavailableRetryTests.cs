using System.Net.WebSockets;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuBackend.Harness.Worker;
using RuBackend.Worker;
using Xunit;

namespace RuBackend.Tests.Worker;

/// <summary>
/// A worker that is not listening yet is a timing problem, not a broken request.
/// </summary>
public sealed class WorkerUnavailableRetryTests
{
    [Fact]
    public async Task A_missing_worker_is_retried_inside_the_step_budget()
    {
        // The runner used to let WorkerUnavailableException escape the retry budget, so the
        // first refused socket ended the turn as "worker_unavailable" — which is how a slow or
        // restarting worker turned into a failed conversation for the reader.
        var sockets = 0;
        var runner = new LoopbackModelStepRunner(
            new AgentWorkerClientOptions(new Uri("ws://127.0.0.1:1/worker/v2"), new string('t', 32)),
            () =>
            {
                sockets++;
                return new ClientWebSocket();
            })
        {
            StartupConnectWindowOverride = TimeSpan.FromMilliseconds(1),
            StepRetryBackoffsOverride = [TimeSpan.Zero, TimeSpan.Zero],
        };

        await Assert.ThrowsAsync<WorkerUnavailableException>(
            () => runner.RunStepAsync(Request(), _ => default, CancellationToken.None));

        // Three attempts, each probing and then connecting: a single attempt would be two.
        Assert.True(sockets >= 4, $"expected the step to be retried, saw {sockets} sockets");
    }

    private static ModelStepRequest Request() => new(
        new RunId("run_" + new string('a', 32)),
        new ModelStepId("step_" + new string('b', 32)),
        new ModelContextSnapshot([], "[]"u8.ToArray()),
        AgentPermissionPreset.Trusted,
        policySnapshotVersion: 1,
        policySnapshotSha256: new string('a', 64));
}
