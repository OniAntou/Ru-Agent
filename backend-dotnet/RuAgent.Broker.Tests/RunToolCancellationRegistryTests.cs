using RuAgent.Broker.Runtime;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class RunToolCancellationRegistryTests
{
    [Fact]
    public void Registration_started_from_a_cancellation_callback_cannot_escape_the_cancelled_run()
    {
        var registry = new RunToolCancellationRegistry();
        var lifetime = new BrokerRunLifetime();
        registry.BindRun("run-callback-race", lifetime);
        using var request = new CancellationTokenSource();
        using var initial = registry.Register("run-callback-race", lifetime, request.Token);
        RunToolCancellationRegistry.Registration? late = null;
        using var callback = initial.Token.Register(
            () => late = registry.Register("run-callback-race", lifetime, CancellationToken.None));

        registry.CancelRun("run-callback-race");

        Assert.NotNull(late);
        Assert.True(late.Token.IsCancellationRequested);
        late.Dispose();
    }
}
