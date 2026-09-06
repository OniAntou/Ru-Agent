using Xunit;

namespace RuApp.Tests;

public sealed class AgentHarnessUiAutomationSourceContractTests
{
    [Fact]
    public void Agent_scenarios_do_not_depend_on_retired_mode_or_root_approval_controls()
    {
        string source = File.ReadAllText(FindHarnessSource("UiAutomationScenario.cs"));

        Assert.Contains("RunAgentScenarioAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentModeAutomationId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IsAgentModeActive", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentTrustApproveAutomationId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ApproveWorkspaceTrustAsync", source, StringComparison.Ordinal);
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
