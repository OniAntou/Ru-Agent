using System.Text;
using RuAgent.Broker.Policy;
using RuAgent.Protocol;
using RuAgent.Protocol.Tools;
using Xunit;

namespace RuAgent.Broker.Tests;

/// <summary>
/// Golden for two things that decide policy: the advertised-tool to policy family map
/// (<see cref="CodingToolArguments.AdapterToolId"/>) and rule tool-id matching
/// (<see cref="PolicyRuleMatcher.MatchesMatcher(MatcherSpec, PolicyContext)"/> with a tool-id-only matcher).
///
/// The expectations are literal data captured from the code before the family column and the shared alias source
/// existed. They are not computed from the code under test, so a refactor that changes any mapping or match fails
/// here instead of silently passing.
///
/// Golden lines: "A call family" holds the AdapterToolId result ("-" for null). "M rule call bits" holds the matches
/// for one rule and one call; a pair with no matching context has no line. "*" means every context matches. Otherwise
/// bits has one character per context, with class as the outermost dimension (read, edit, write, patch), then ToolOp
/// (null, list, read, write, delete, restore, web_search, web_fetch, help), then BashMode (null, run, shell,
/// read_output, kill).
/// </summary>
public sealed class PolicyFamilyGoldenTests
{
    private static readonly string[] DescriptorIds =
    [
        "read", "list", "help", "todo_write", "grep", "glob", "web_search", "web_fetch", "skill", "ask_user",
        "mcp", "computer", "agent_spawn", "agent_send", "agent_wait", "agent_cancel", "agent_apply",
        "code_intelligence", "run_code", "apply_patch", "read_file", "write_file", "edit_file", "file_delete",
        "file_restore", "powershell", "run_process", "terminal_read", "terminal_stop",
    ];

    // Adapter-family names that are also used as rule and call ids, plus one id no row declares.
    private static readonly string[] FamilyIds = ["bash", "write", "edit"];
    private const string UnknownToolId = "unknown_tool";

    private static readonly string[] AliasRuleIds =
    [
        "files.list", "files.glob", "files.search", "files.read", "files.write", "files.edit", "files.delete",
        "files.restore", "terminal.run", "terminal.shell", "web.search", "web.fetch", "help.docs",
    ];

    private static readonly string?[] ToolOps = [null, "list", "read", "write", "delete", "restore", "web_search", "web_fetch", "help"];
    private static readonly string?[] BashModes = [null, "run", "shell", "read_output", "kill"];
    private static readonly string[] OperationClasses = ["read", "edit", "write", "patch"];

    private static readonly string[] CallIds = [.. DescriptorIds, .. FamilyIds, UnknownToolId];
    private static readonly string[] RuleIds = [.. DescriptorIds, .. FamilyIds, .. AliasRuleIds, "unknown_rule"];

    [Fact]
    public void Descriptor_ids_are_the_golden_ids()
    {
        Assert.Equal(DescriptorIds, AgentToolDescriptorTable.All.Select(row => row.Id).ToArray());
    }

    [Fact]
    public void Advertised_tool_policy_family_matches_golden_for_every_descriptor_id_and_unknown_ids()
    {
        var expected = GoldenLines("A ").ToDictionary(
            parts => parts[1],
            parts => parts[2] == "-" ? null : parts[2],
            StringComparer.Ordinal);

        Assert.Equal(CallIds.Length, expected.Count);
        foreach (var callId in CallIds)
        {
            Assert.True(expected.ContainsKey(callId), $"no golden line for '{callId}'");
            Assert.Equal(expected[callId], CodingToolArguments.AdapterToolId(callId));
        }
    }

    [Fact]
    public void Tool_id_matching_matches_golden_for_every_rule_and_call_pair()
    {
        var expected = GoldenLines("M ").ToDictionary(
            parts => (parts[1], parts[2]),
            parts => parts[3],
            new RuleCallComparer());

        var mismatches = new List<string>();
        foreach (var rule in RuleIds)
        {
            foreach (var call in CallIds)
            {
                var golden = expected.TryGetValue((rule, call), out var bits) ? bits : null;
                var actual = MatchToken(rule, call);
                if (!string.Equals(golden, actual, StringComparison.Ordinal))
                {
                    mismatches.Add($"{rule} {call}: golden={golden ?? "-"} actual={actual ?? "-"}");
                }
            }
        }

        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    private static string? MatchToken(string rule, string call)
    {
        var matcher = new MatcherSpec(rule, null, null, null, Array.Empty<string>(), null, null, null);
        var bits = new StringBuilder();
        var hits = 0;
        foreach (var operationClass in OperationClasses)
        {
            foreach (var toolOp in ToolOps)
            {
                foreach (var bashMode in BashModes)
                {
                    var context = new PolicyContext(
                        ToolId: call,
                        ToolVersion: "1",
                        CanonicalWorkspaceRoot: "C:\\ws",
                        Target: null,
                        RelativePathInsideWorkspace: null,
                        ResolvedExecutable: null,
                        Argv: PolicyContext.EmptyArgv,
                        CanonicalCwd: null,
                        OperationClass: operationClass,
                        NetworkIntent: "none",
                        NetworkDeclared: false,
                        IntentHash: "h",
                        ToolOp: toolOp,
                        BashMode: bashMode);
                    var hit = PolicyRuleMatcher.MatchesMatcher(matcher, context);
                    bits.Append(hit ? '1' : '0');
                    if (hit)
                    {
                        hits++;
                    }
                }
            }
        }

        if (hits == 0)
        {
            return null;
        }

        return hits == bits.Length ? "*" : bits.ToString();
    }

    private static string[][] GoldenLines(string prefix) =>
        Golden.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
            .Select(line => line.Split(' '))
            .ToArray();

    private sealed class RuleCallComparer : IEqualityComparer<(string Rule, string Call)>
    {
        public bool Equals((string Rule, string Call) x, (string Rule, string Call) y) =>
            string.Equals(x.Rule, y.Rule, StringComparison.Ordinal) && string.Equals(x.Call, y.Call, StringComparison.Ordinal);

        public int GetHashCode((string Rule, string Call) obj) => HashCode.Combine(obj.Rule, obj.Call);
    }

    private const string Golden = """
A read -
A list -
A help -
A todo_write -
A grep -
A glob -
A web_search -
A web_fetch -
A skill -
A ask_user -
A mcp -
A computer -
A agent_spawn -
A agent_send -
A agent_wait -
A agent_cancel -
A agent_apply -
A code_intelligence -
A run_code -
A apply_patch -
A read_file read
A write_file write
A edit_file edit
A file_delete write
A file_restore write
A powershell bash
A run_process bash
A terminal_read bash
A terminal_stop bash
A bash -
A write -
A edit -
A unknown_tool -
M read read *
M read read_file *
M list list *
M help help *
M todo_write todo_write *
M grep grep *
M glob glob *
M web_search web_search *
M web_fetch web_fetch *
M skill skill *
M ask_user ask_user *
M mcp mcp *
M computer computer *
M agent_spawn agent_spawn *
M agent_send agent_send *
M agent_wait agent_wait *
M agent_cancel agent_cancel *
M agent_apply agent_apply *
M code_intelligence code_intelligence *
M run_code run_code *
M apply_patch apply_patch *
M read_file read_file *
M write_file write_file *
M edit_file edit_file *
M file_delete file_delete *
M file_restore file_restore *
M powershell powershell *
M run_process run_process *
M terminal_read terminal_read *
M terminal_stop terminal_stop *
M bash powershell *
M bash run_process *
M bash terminal_read *
M bash terminal_stop *
M bash bash *
M write apply_patch 111111111111111111111111111111111111111111111000000000000000000000000000000000000000000000111111111111111111111111111111111111111111111000000000000000000000000000000000000000000000
M write write_file *
M write file_delete *
M write file_restore *
M write write *
M edit apply_patch 000000000000000000000000000000000000000000000111111111111111111111111111111111111111111111000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000
M edit edit_file *
M edit edit *
M files.list read 000001111100000000000000000000000000000000000000001111100000000000000000000000000000000000000001111100000000000000000000000000000000000000001111100000000000000000000000000000000000
M files.list read_file 000001111100000000000000000000000000000000000000001111100000000000000000000000000000000000000001111100000000000000000000000000000000000000001111100000000000000000000000000000000000
M files.glob read 000001111100000000000000000000000000000000000000001111100000000000000000000000000000000000000001111100000000000000000000000000000000000000001111100000000000000000000000000000000000
M files.glob glob *
M files.glob read_file 000001111100000000000000000000000000000000000000001111100000000000000000000000000000000000000001111100000000000000000000000000000000000000001111100000000000000000000000000000000000
M files.search read 000001111100000000000000000000000000000000000000001111100000000000000000000000000000000000000001111100000000000000000000000000000000000000001111100000000000000000000000000000000000
M files.search grep *
M files.search read_file 000001111100000000000000000000000000000000000000001111100000000000000000000000000000000000000001111100000000000000000000000000000000000000001111100000000000000000000000000000000000
M files.read read 000000000011111000000000000000000000000000000000000000011111000000000000000000000000000000000000000011111000000000000000000000000000000000000000011111000000000000000000000000000000
M files.read read_file 000000000011111000000000000000000000000000000000000000011111000000000000000000000000000000000000000011111000000000000000000000000000000000000000011111000000000000000000000000000000
M files.write apply_patch 000000000000000111110000000000000000000000000000000000000000000000000000000000000000000000000000000000000111110000000000000000000000000000000000000000000000000000000000000000000000
M files.write write_file 000000000000000111110000000000000000000000000000000000000000111110000000000000000000000000000000000000000111110000000000000000000000000000000000000000111110000000000000000000000000
M files.write file_delete 000000000000000111110000000000000000000000000000000000000000111110000000000000000000000000000000000000000111110000000000000000000000000000000000000000111110000000000000000000000000
M files.write file_restore 000000000000000111110000000000000000000000000000000000000000111110000000000000000000000000000000000000000111110000000000000000000000000000000000000000111110000000000000000000000000
M files.write write 000000000000000111110000000000000000000000000000000000000000111110000000000000000000000000000000000000000111110000000000000000000000000000000000000000111110000000000000000000000000
M files.edit apply_patch 000000000000000000000000000000000000000000000111111111111111111111111111111111111111111111000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000
M files.edit edit_file *
M files.edit edit *
M files.delete apply_patch 000000000000000000001111100000000000000000000000000000000000000000000000000000000000000000000000000000000000001111100000000000000000000000000000000000000000000000000000000000000000
M files.delete write_file 000000000000000000001111100000000000000000000000000000000000000001111100000000000000000000000000000000000000001111100000000000000000000000000000000000000001111100000000000000000000
M files.delete file_delete 000000000000000000001111100000000000000000000000000000000000000001111100000000000000000000000000000000000000001111100000000000000000000000000000000000000001111100000000000000000000
M files.delete file_restore 000000000000000000001111100000000000000000000000000000000000000001111100000000000000000000000000000000000000001111100000000000000000000000000000000000000001111100000000000000000000
M files.delete write 000000000000000000001111100000000000000000000000000000000000000001111100000000000000000000000000000000000000001111100000000000000000000000000000000000000001111100000000000000000000
M files.restore apply_patch 000000000000000000000000011111000000000000000000000000000000000000000000000000000000000000000000000000000000000000011111000000000000000000000000000000000000000000000000000000000000
M files.restore write_file 000000000000000000000000011111000000000000000000000000000000000000000011111000000000000000000000000000000000000000011111000000000000000000000000000000000000000011111000000000000000
M files.restore file_delete 000000000000000000000000011111000000000000000000000000000000000000000011111000000000000000000000000000000000000000011111000000000000000000000000000000000000000011111000000000000000
M files.restore file_restore 000000000000000000000000011111000000000000000000000000000000000000000011111000000000000000000000000000000000000000011111000000000000000000000000000000000000000011111000000000000000
M files.restore write 000000000000000000000000011111000000000000000000000000000000000000000011111000000000000000000000000000000000000000011111000000000000000000000000000000000000000011111000000000000000
M terminal.run powershell 010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000
M terminal.run run_process 010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000
M terminal.run terminal_read 010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000
M terminal.run terminal_stop 010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000
M terminal.run bash 010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000
M terminal.shell powershell 001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100
M terminal.shell run_process 001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100
M terminal.shell terminal_read 001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100
M terminal.shell terminal_stop 001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100
M terminal.shell bash 001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100001000010000100
M web.search read 000000000000000000000000000000111110000000000000000000000000000000000000000111110000000000000000000000000000000000000000111110000000000000000000000000000000000000000111110000000000
M web.search web_search *
M web.search read_file 000000000000000000000000000000111110000000000000000000000000000000000000000111110000000000000000000000000000000000000000111110000000000000000000000000000000000000000111110000000000
M web.fetch read 000000000000000000000000000000000001111100000000000000000000000000000000000000001111100000000000000000000000000000000000000001111100000000000000000000000000000000000000001111100000
M web.fetch web_fetch *
M web.fetch read_file 000000000000000000000000000000000001111100000000000000000000000000000000000000001111100000000000000000000000000000000000000001111100000000000000000000000000000000000000001111100000
M help.docs read 000000000000000000000000000000000000000011111000000000000000000000000000000000000000011111000000000000000000000000000000000000000011111000000000000000000000000000000000000000011111
M help.docs read_file 000000000000000000000000000000000000000011111000000000000000000000000000000000000000011111000000000000000000000000000000000000000011111000000000000000000000000000000000000000011111
""";
}
