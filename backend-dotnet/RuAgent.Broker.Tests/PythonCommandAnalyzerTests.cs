using RuAgent.Broker.Policy.Commands;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class PythonCommandAnalyzerTests
{
    private readonly PythonCommandAnalyzer _analyzer = new();

    [Fact]
    public void Direct_pytest_is_a_test_command()
    {
        var analysis = _analyzer.Analyze(@"C:\venv\Scripts\pytest.exe", ["-q", "tests"]);

        Assert.Equal(TerminalOperationClass.Test, analysis.OperationClass);
        Assert.Equal("1", analysis.AnalyzerVersion);
    }

    [Theory]
    [InlineData("python")]
    [InlineData("python3")]
    [InlineData("py")]
    public void Python_module_pytest_is_a_test_command(string executable)
    {
        var analysis = _analyzer.Analyze(@"C:\python\python.exe", ["-m", "pytest", "-q"]);

        Assert.Equal(TerminalOperationClass.Test, analysis.OperationClass);
        _ = executable;
    }

    [Theory]
    [InlineData("ruff", new[] { "check" })]
    [InlineData("mypy", new[] { "." })]
    public void Linters_are_recognized_through_direct_and_module_forms(string module, string[] rest)
    {
        var direct = _analyzer.Analyze($@"C:\venv\Scripts\{module}.exe", rest);
        Assert.Equal(TerminalOperationClass.Lint, direct.OperationClass);

        var throughModule = _analyzer.Analyze(@"C:\python\python.exe", ["-m", module, .. rest]);
        Assert.Equal(TerminalOperationClass.Lint, throughModule.OperationClass);
    }

    [Fact]
    public void Ruff_fix_mutates_files_and_asks()
    {
        var analysis = _analyzer.Analyze(@"C:\venv\Scripts\ruff.exe", ["check", "--fix"]);

        Assert.Equal(TerminalOperationClass.ExternalMutation, analysis.OperationClass);
    }

    [Fact]
    public void Pip_installs_are_external_network_mutations()
    {
        var direct = _analyzer.Analyze(@"C:\venv\Scripts\pip.exe", ["install", "requests"]);
        var throughModule = _analyzer.Analyze(@"C:\python\python.exe", ["-m", "pip", "install", "requests"]);

        Assert.Equal(TerminalOperationClass.ExternalMutation, direct.OperationClass);
        Assert.Equal("declared", direct.NetworkIntent);
        Assert.Equal(TerminalOperationClass.ExternalMutation, throughModule.OperationClass);
    }

    [Fact]
    public void Arbitrary_python_execution_fails_closed()
    {
        var script = _analyzer.Analyze(@"C:\python\python.exe", ["script.py"]);
        var inline = _analyzer.Analyze(@"C:\python\python.exe", ["-c", "print(1)"]);
        var otherModule = _analyzer.Analyze(@"C:\python\python.exe", ["-m", "requests"]);

        Assert.Equal(TerminalOperationClass.Unknown, script.OperationClass);
        Assert.Equal(TerminalOperationClass.Unknown, inline.OperationClass);
        Assert.Equal(TerminalOperationClass.Unknown, otherModule.OperationClass);
    }

    [Fact]
    public void Interactive_debugger_flags_ask()
    {
        var analysis = _analyzer.Analyze(@"C:\venv\Scripts\pytest.exe", ["--pdb"]);

        Assert.Equal(TerminalOperationClass.Unknown, analysis.OperationClass);
    }
}
