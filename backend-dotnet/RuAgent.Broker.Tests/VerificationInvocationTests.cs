using System.Reflection;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Broker.Tools.Terminal;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class VerificationInvocationTests
{
    [Fact]
    public void Inline_python_parse_failure_has_a_repair_family_without_changing_exact_identity()
    {
        var failed = Output("  File \"<string>\", line 1\n    from a b import value\n           ^\nSyntaxError: invalid syntax\n", 1);
        Annotate(failed, ["-c", "from a b import value"]);
        var passed = Output("", 0);
        Annotate(passed, ["-c", "import importlib; print('verified')"]);
        Assert.True(failed["verification_invocation_invalid"]?.GetValue<bool>());
        Assert.Equal(failed["verification_repair_identity"]!.ToString(), passed["verification_repair_identity"]!.ToString());
        Assert.NotEqual(failed["verification_identity"]!.ToString(), passed["verification_identity"]!.ToString());
        var elsewhere = Output("", 0);
        Annotate(elsewhere, ["-c", "print('verified')"], cwd: "C:/other");
        Assert.NotEqual(failed["verification_repair_identity"]!.ToString(), elsewhere["verification_repair_identity"]!.ToString());
    }

    [Theory]
    [InlineData("Traceback (most recent call last):\n  File \"<string>\", line 1\nSyntaxError: raised by running code", 0)]
    [InlineData("Traceback (most recent call last):\n  File \"<string>\", line 1\n  File \"module.py\", line 2\nSyntaxError: invalid syntax", 0)]
    [InlineData("  File \"module.py\", line 2\nSyntaxError: invalid syntax", 0)]
    [InlineData("  File \"<string>\", line 1\nSyntaxError: invalid syntax", 1)]
    public void Project_failures_runtime_exceptions_and_truncated_output_are_not_invalid_invocations(string stderr, int dropped)
    {
        var output = Output(stderr, 1);
        output["stderr_dropped_bytes"] = dropped;
        Annotate(output, ["-c", "import module"]);
        Assert.NotEqual(true, output["verification_invocation_invalid"]?.GetValue<bool>());
    }

    [Fact]
    public void File_execution_and_other_executables_do_not_receive_the_inline_repair_family()
    {
        var file = Output("  File \"<string>\", line 1\nSyntaxError: invalid syntax", 1);
        Annotate(file, ["script.py"]);
        Assert.Null(file["verification_repair_identity"]);
        var other = Output("  File \"<string>\", line 1\nSyntaxError: invalid syntax", 1);
        Annotate(other, ["-c", "invalid"], executable: "C:/tools/not-python.exe");
        Assert.Null(other["verification_repair_identity"]);
    }

    private static JsonObject Output(string stderr, int exit) => new()
    {
        ["exit_code"] = exit, ["stderr_head"] = stderr, ["stderr_tail"] = "",
        ["stdout_bytes"] = 0L, ["stderr_dropped_bytes"] = 0L,
    };

    private static void Annotate(JsonObject output, string[] arguments, string cwd = "C:/workspace", string executable = "C:/tools/python.exe")
    {
        var intent = new CanonicalCommandIntent(executable, arguments, cwd, PathClassification.InsideWorkspace,
            false, 1000, RuAgent.Protocol.TerminalOperationClass.Unknown, default, new string('a', 64));
        typeof(ToolExecutionService).GetMethod("AddVerificationIdentity", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [output, intent]);
    }
}
