using RuAgent.Broker.Tools;
using RuAgent.Broker.Tools.Terminal;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class BackgroundTaskDisplayTests
{
    [Fact]
    public void Command_labels_redact_credentials_before_bounding_and_remove_newlines()
    {
        var adapter = new TerminalAdapter(new WorkspacePathGuard(Path.GetTempPath()),
            commandEnvironment: new AgentCommandEnvironment(new Dictionary<string, string>
            {
                ["OPENAI_API_KEY"] = "synthetic-task-display-secret",
            }));
        var label = adapter.DisplayCommand("echo synthetic-task-display-secret\r\n" + new string('x', 600));
        Assert.Contains("[redacted]", label);
        Assert.DoesNotContain("synthetic-task-display-secret", label);
        Assert.DoesNotContain('\n', label);
        Assert.DoesNotContain('\r', label);
        Assert.Equal(512, label.Length);
    }

    [Fact]
    public void Retained_command_labels_follow_the_existing_session_boundary()
    {
        var registry = new BackgroundTaskRegistry();
        try
        {
            Assert.True(registry.TryStart("a", "task", 60_000, true, out _, "npm run dev"));
            Assert.Equal("npm run dev", Assert.Single(registry.List("a")).Command);
            Assert.True(registry.TryRead("a", "task", out var snapshot));
            Assert.Equal("npm run dev", snapshot.Command);
            Assert.Empty(registry.List("b"));
            Assert.False(registry.TryRead("b", "task", out _));
        }
        finally
        {
            registry.RemoveSession("a");
        }
    }
}
