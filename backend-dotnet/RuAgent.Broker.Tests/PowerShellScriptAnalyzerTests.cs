using RuAgent.Broker.Policy.Commands;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class PowerShellScriptAnalyzerTests
{
    private readonly PowerShellScriptAnalyzer _analyzer = new(new CommandAnalyzerRegistry());

    [Theory]
    [InlineData("Remove-Item ./a.txt")]
    [InlineData("del ./a.txt")]
    [InlineData("erase ./a.txt")]
    [InlineData("rm ./a.txt")]
    [InlineData("rmdir ./folder")]
    public void Delete_commands_have_a_dedicated_risk(string script)
    {
        var result = _analyzer.Analyze(script);

        Assert.Equal(TerminalOperationClass.DirectDelete, result.OperationClass);
        Assert.Equal("terminal_direct_delete", result.ReasonCode);
    }

    [Theory]
    [InlineData("Invoke-Expression $command")]
    [InlineData("iex $command")]
    [InlineData("& $executable --version")]
    [InlineData("frobnicate.exe --version")]
    [InlineData("Get-Date; & 'unknown-tool.exe'")]
    [InlineData("Get-Date |")]
    public void Dynamic_unknown_or_invalid_scripts_fail_closed(string script)
    {
        var result = _analyzer.Analyze(script);

        Assert.Equal(TerminalOperationClass.Unknown, result.OperationClass);
    }

    [Theory]
    [InlineData("Get-Date")]
    [InlineData("Write-Output 'hello'")]
    [InlineData("git status")]
    [InlineData("git rm --cached old.txt")]
    [InlineData("python -m pip install requests")]
    [InlineData("dotnet restore")]
    public void Static_recognized_non_delete_commands_are_automatic(string script)
    {
        var result = _analyzer.Analyze(script);

        Assert.NotEqual(TerminalOperationClass.Unknown, result.OperationClass);
        Assert.NotEqual(TerminalOperationClass.DirectDelete, result.OperationClass);
    }

    [Fact]
    public void Compound_script_is_atomic_when_a_later_statement_deletes()
    {
        var result = _analyzer.Analyze("Get-Date; git status; Remove-Item ./a.txt");

        Assert.Equal(TerminalOperationClass.DirectDelete, result.OperationClass);
    }
}
