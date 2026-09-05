using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class HarnessSessionDescriptorTests
{
    [Theory]
    [InlineData("agent-tool-contract")]
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

    [Fact]
    public void Harness_descriptor_round_trips_the_isolated_workspace()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "ru-harness-descriptor-" + Guid.NewGuid().ToString("N"));
        string descriptorPath = Path.Combine(root, "session.json");
        string workspacePath = Path.Combine(root, "workspace");
        string eventsPath = Path.Combine(root, "gui", "events.jsonl");
        Directory.CreateDirectory(workspacePath);

        try
        {
            var descriptor = HarnessSessionDescriptor.Create(
                "http://127.0.0.1:8000",
                "secret",
                "agent-tool-contract",
                eventsPath,
                workspacePath: workspacePath);
            descriptor.WriteTo(descriptorPath);

            var loaded = HarnessSessionDescriptor.LoadFromArgument(
                $"--ru-harness-session \"{descriptorPath}\"");

            Assert.Equal(Path.GetFullPath(workspacePath), loaded.WorkspacePath);
            Assert.False(File.Exists(descriptorPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Harness_descriptor_rejects_a_workspace_outside_the_descriptor_root()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "ru-harness-descriptor-" + Guid.NewGuid().ToString("N"));
        string descriptorPath = Path.Combine(root, "session.json");
        string externalWorkspace = Path.GetTempPath();
        Directory.CreateDirectory(root);

        try
        {
            var descriptor = HarnessSessionDescriptor.Create(
                "http://127.0.0.1:8000",
                "secret",
                "agent-tool-contract",
                Path.Combine(root, "events.jsonl"),
                workspacePath: externalWorkspace);
            descriptor.WriteTo(descriptorPath);

            Assert.Throws<ArgumentException>(() => HarnessSessionDescriptor.LoadFromArgument(
                $"--ru-harness-session \"{descriptorPath}\""));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
