using Xunit;

namespace RuApp.Tests;

public sealed class AgentHarnessUiAutomationSourceContractTests
{
    [Fact]
    public void Agent_mode_uses_the_toggle_automation_pattern()
    {
        string source = File.ReadAllText(FindHarnessSource("UiAutomationScenario.cs"));

        Assert.Contains("Toggle(mode);", source, StringComparison.Ordinal);
        Assert.Contains("togglePattern.Toggle();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke(mode);", source, StringComparison.Ordinal);
        Assert.Contains("EnsureToggleOff(mode);", source, StringComparison.Ordinal);
        Assert.Contains("if (IsToggleOn(element))", source, StringComparison.Ordinal);
        Assert.Equal(2, source.Split("EnsureToggleOff(mode);", StringSplitOptions.None).Length - 1);
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
