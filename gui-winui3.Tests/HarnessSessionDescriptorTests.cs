using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class HarnessSessionDescriptorTests
{
    [Theory]
    [InlineData("agent-volume-contract")]
    [InlineData("agent-cancel")]
    [InlineData("agent-stale-result")]
    [InlineData("agent-private-metadata")]
    public void Harness_descriptor_accepts_every_published_agent_scenario(string scenario)
    {
        var descriptor = HarnessSessionDescriptor.Create(
            "http://127.0.0.1:8000",
            "secret",
            scenario,
            Path.Combine(Path.GetTempPath(), "ru-harness-events.jsonl"));

        Assert.Equal(scenario, descriptor.Scenario);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://192.168.1.10:8000")]
    public void Harness_descriptor_rejects_non_loopback_edge(string baseUrl)
    {
        Assert.Throws<ArgumentException>(() =>
            HarnessSessionDescriptor.Create(baseUrl, "secret", "turn", "events.jsonl"));
    }
}
