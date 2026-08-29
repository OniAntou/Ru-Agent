using System.Reflection;
using RuBackend.Harness.Worker;
using RuBackend.Worker;
using Xunit;

namespace RuBackend.Tests.Worker;

public sealed class WorkerClientOptionsTests
{
    [Fact]
    public async Task Agent_options_require_the_v2_endpoint_and_keep_the_configured_uri()
    {
        var uri = new Uri("ws://127.0.0.1:8123/worker/v2?role=agent");
        var options = new AgentWorkerClientOptions(uri, "agent-token");

        options.Validate();
        Assert.Equal(AgentWorkerClientOptions.ProtocolVersion, 2);

        await using var runner = new LoopbackModelStepRunner(options);
        var field = typeof(LoopbackModelStepRunner).GetField(
            "_options",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var configured = Assert.IsType<AgentWorkerClientOptions>(field!.GetValue(runner));
        Assert.Equal(uri, configured.Uri);
        Assert.Equal("agent-token", configured.Token);
    }

    [Fact]
    public void Device_options_require_the_v1_endpoint()
    {
        var options = new DeviceWorkerClientOptions(
            new Uri("ws://127.0.0.1:8124/worker/v1"),
            "device-token");

        options.Validate();

        Assert.Throws<ArgumentException>(() => new DeviceWorkerClientOptions(
            new Uri("ws://127.0.0.1:8124/worker/v2"),
            "device-token").Validate());
    }

    [Fact]
    public void Agent_options_reject_a_v1_endpoint_instead_of_rewriting_it()
    {
        var options = new AgentWorkerClientOptions(
            new Uri("ws://127.0.0.1:8123/worker/v1"),
            "agent-token");

        Assert.Throws<ArgumentException>(() => options.Validate());
    }
}
