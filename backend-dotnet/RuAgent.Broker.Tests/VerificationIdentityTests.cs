using System.Reflection;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Broker.Tools.Terminal;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

/// <summary>
/// Terminal receipt identity fields the Harness still reads (ADR 0039). The Python repair
/// annotations are removed, so a receipt carries no repair identity, invalid-invocation flag or hint.
/// </summary>
public sealed class VerificationIdentityTests
{
    [Fact]
    public void Identity_is_stable_for_the_same_canonical_invocation_and_separates_arguments_and_cwd()
    {
        var first = Annotated(["-c", "import importlib; print('verified')"]);
        var same = Annotated(["-c", "import importlib; print('verified')"]);
        var otherArguments = Annotated(["-c", "from a b import value"]);
        var otherDirectory = Annotated(["-c", "import importlib; print('verified')"], cwd: "C:/other");

        Assert.Equal(first["verification_identity"]!.ToString(), same["verification_identity"]!.ToString());
        Assert.NotEqual(first["verification_identity"]!.ToString(), otherArguments["verification_identity"]!.ToString());
        Assert.NotEqual(first["verification_identity"]!.ToString(), otherDirectory["verification_identity"]!.ToString());
    }

    [Theory]
    [InlineData(RuAgent.Protocol.TerminalOperationClass.ReadOnly, "read_only")]
    [InlineData(RuAgent.Protocol.TerminalOperationClass.Unknown, "unknown")]
    public void Receipt_carries_display_cwd_and_terminal_effect(RuAgent.Protocol.TerminalOperationClass operation, string effect)
    {
        var output = Annotated(["script.py"], cwd: "C:/workspace", operation: operation);

        Assert.Equal("C:/workspace", output["verification_cwd"]!.GetValue<string>());
        Assert.Equal(effect, output["terminal_effect"]!.GetValue<string>());
    }

    [Fact]
    public void Inline_python_parse_failure_gets_no_repair_fields_or_hint()
    {
        var output = Output(exit: 1, stderrHead: "  File \"<string>\", line 1\n    from a b import value\nSyntaxError: invalid syntax\n");

        Annotate(output, ["-c", "from a b import value"]);

        AssertNoRepairFields(output);
        Assert.Equal(1, output["exit_code"]!.GetValue<int>());
    }

    [Fact]
    public void Empty_pytest_discovery_gets_no_repair_fields_or_hint()
    {
        var output = Output(exit: 5, stdoutHead: "no tests ran in 0.01s");

        Annotate(output, ["-m", "pytest", "-v"]);

        AssertNoRepairFields(output);
        Assert.Equal(5, output["exit_code"]!.GetValue<int>());
    }

    private static void AssertNoRepairFields(JsonObject output)
    {
        Assert.NotNull(output["verification_identity"]);
        Assert.Null(output["verification_repair_identity"]);
        Assert.Null(output["verification_invocation_invalid"]);
        Assert.Null(output["hint"]);
    }

    private static JsonObject Output(int exit, string stdoutHead = "", string stderrHead = "") => new()
    {
        ["exit_code"] = exit, ["stdout_head"] = stdoutHead, ["stdout_tail"] = string.Empty,
        ["stderr_head"] = stderrHead, ["stderr_tail"] = string.Empty,
        ["stdout_bytes"] = 0L, ["stderr_dropped_bytes"] = 0L, ["stdout_dropped_bytes"] = 0L,
    };

    private static JsonObject Annotated(string[] arguments, string cwd = "C:/workspace",
        RuAgent.Protocol.TerminalOperationClass operation = RuAgent.Protocol.TerminalOperationClass.Unknown)
    {
        var output = Output(exit: 0);
        Annotate(output, arguments, cwd, operation);
        return output;
    }

    private static void Annotate(JsonObject output, string[] arguments, string cwd = "C:/workspace",
        RuAgent.Protocol.TerminalOperationClass operation = RuAgent.Protocol.TerminalOperationClass.Unknown)
    {
        var intent = new CanonicalCommandIntent("C:/tools/python.exe", arguments, cwd, PathClassification.InsideWorkspace,
            false, 1000, operation, default, new string('a', 64));
        typeof(ToolExecutionService).GetMethod("AddVerificationIdentity", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [output, intent]);
    }
}
