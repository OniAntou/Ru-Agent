using Xunit;

namespace RuApp.Tests;

public sealed class AgentHarnessUiAutomationSourceContractTests
{
    [Fact]
    public void Agent_scenarios_use_the_static_agent_indicator_without_mode_selection()
    {
        string source = File.ReadAllText(FindHarnessSource("UiAutomationScenario.cs"));

        Assert.Contains("AgentModeAutomationId", source, StringComparison.Ordinal);
        Assert.Contains("IsAgentModeActive(mode)", source, StringComparison.Ordinal);
        Assert.Contains("RunAgentScenarioAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentModeAgentOptionAutomationId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentModeChatOptionAutomationId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectModeAsync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke(mode);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectionItemPattern.Pattern", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Toggle(mode);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("togglePattern.Toggle();", source, StringComparison.Ordinal);
    }

    private static string FindHarnessSource(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string sourceRoot = Path.Combine(directory.FullName, "gui-winui3.Harness");
            if (Directory.Exists(sourceRoot))
            {
                return Path.Combine(new[] { sourceRoot }.Concat(parts).ToArray());
            }
        }

        throw new DirectoryNotFoundException("gui-winui3 harness source root was not found.");
    }
}
