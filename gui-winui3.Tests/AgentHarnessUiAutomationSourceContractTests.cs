using Xunit;

namespace RuApp.Tests;

public sealed class AgentHarnessUiAutomationSourceContractTests
{
    [Fact]
    public void Agent_mode_uses_the_flyout_radio_automation_contract()
    {
        string source = File.ReadAllText(FindHarnessSource("UiAutomationScenario.cs"));

        Assert.Contains("Invoke(mode);", source, StringComparison.Ordinal);
        Assert.Contains("AgentModeAgentOptionAutomationId", source, StringComparison.Ordinal);
        Assert.Contains("Invoke(agentOption);", source, StringComparison.Ordinal);
        Assert.Contains("SelectModeAsync(", source, StringComparison.Ordinal);
        Assert.Contains("AgentModeChatOptionAutomationId", source, StringComparison.Ordinal);
        Assert.Contains("SelectionItemPattern.Pattern", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Toggle(mode);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("togglePattern.Toggle();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureToggleOff(mode);", source, StringComparison.Ordinal);
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

        throw new DirectoryNotFoundException("gui-winui3.Harness source root was not found.");
    }
}
