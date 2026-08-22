using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentPrivacyContractTests
{
    [Fact]
    public void Harness_sink_rejects_agent_arguments_and_credentials()
    {
        var sink = new InMemoryHarnessEventSink();
        Assert.Throws<ArgumentException>(() => sink.Write("agent.activity", new { arguments = "secret" }));
        Assert.Throws<ArgumentException>(() => sink.Write("agent.activity", new { token = "secret" }));
        sink.Write("agent.activity", new { state = "running", tool_id = "files.list" });
        Assert.Contains("agent.activity", sink.Codes);
    }
}
