using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The GUI harness drives the shipped app through one bounded allowlist of agent
/// scenarios. Media surfaces and the retired Agent-mode / workspace-trust approval
/// toggles were removed, so the allowlist carries agent scenarios only and the
/// driver must not still reach for those controls.
///
/// The allowlist is asserted as data — every entry must be an agent scenario — and
/// the retired control names are asserted absent from the whole harness project,
/// which is the property that actually matters.
/// </summary>
public sealed class AgentHarnessUiAutomationSourceContractTests
{
    private static readonly string[] RetiredControlNames =
    [
        "AgentModeAutomationId",
        "IsAgentModeActive",
        "AgentTrustApproveAutomationId",
        "ApproveWorkspaceTrustAsync",
        "AgentModeAgentOptionAutomationId",
        "AgentModeChatOptionAutomationId",
        "SelectModeAsync",
        "SelectionItemPattern.Pattern",
        "togglePattern.Toggle()",
    ];

    [Fact]
    public void Every_declared_scenario_is_an_agent_scenario()
    {
        var declared = DeclaredScenarios();

        Assert.NotEmpty(declared);
        Assert.All(declared, scenario =>
            Assert.StartsWith("agent-", scenario, StringComparison.Ordinal));
    }

    [Fact]
    public void The_allowlist_carries_no_media_or_mode_scenario()
    {
        var declared = DeclaredScenarios();

        // Voice, VTube Studio, observation and the Agent-mode selector are gone.
        foreach (var retiredPrefix in new[] { "chat-", "voice", "vts", "observation", "mode-" })
        {
            Assert.DoesNotContain(
                declared,
                scenario => scenario.StartsWith(retiredPrefix, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void The_harness_never_reaches_for_a_retired_control()
    {
        var offenders = new List<string>();
        foreach (var file in HarnessSources())
        {
            var text = File.ReadAllText(file);
            foreach (var retired in RetiredControlNames)
            {
                if (text.Contains(retired, StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {retired}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_driver_runs_scenarios_through_the_agent_entry_point()
    {
        var scenario = File.ReadAllText(HarnessPath("UiAutomationScenario.cs"));

        // One entry point for agent work; a second scenario dispatcher would mean a
        // parallel path nobody maintains.
        Assert.Contains("RunAgentScenarioAsync", scenario, StringComparison.Ordinal);
    }

    private static List<string> DeclaredScenarios()
    {
        var program = File.ReadAllText(HarnessPath("Program.cs"));
        var declaration = program.IndexOf("private static readonly string[] Scenarios", StringComparison.Ordinal);
        Assert.True(declaration >= 0, "The harness scenario allowlist was not found.");

        // Start after the type's own '[]' and the assignment, or the first bracket
        // found belongs to "string[]" and the slice comes back empty.
        var assignment = program.IndexOf('=', declaration);
        Assert.True(assignment > declaration, "The harness scenario allowlist has no initializer.");

        var open = program.IndexOf('[', assignment);
        var close = program.IndexOf(']', open);
        Assert.True(open > assignment && close > open, "The harness scenario allowlist is malformed.");

        return program[(open + 1)..close]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.Trim('"'))
            .Where(entry => entry.Length > 0)
            .ToList();
    }

    private static IEnumerable<string> HarnessSources() =>
        Directory.EnumerateFiles(HarnessRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(path => path
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .All(segment => !string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase)));

    private static string HarnessPath(params string[] parts) =>
        Path.Combine(new[] { HarnessRoot() }.Concat(parts).ToArray());

    private static string HarnessRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string root = Path.Combine(directory.FullName, "gui-winui3.Harness");
            if (Directory.Exists(root))
            {
                return root;
            }
        }

        throw new DirectoryNotFoundException("gui-winui3.Harness source root was not found.");
    }
}
