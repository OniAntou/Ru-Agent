using RuAgent.Broker.Policy.Commands;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class RipgrepCommandAnalyzerTests
{
    private readonly RipgrepCommandAnalyzer _analyzer = new();

    [Fact]
    public void Search_forms_are_read_only()
    {
        var search = _analyzer.Analyze(@"C:\tools\rg.exe", ["pattern", "."]);
        var filesWithMatches = _analyzer.Analyze(@"C:\tools\rg.exe", ["-l", "pattern", "src"]);
        var listFiles = _analyzer.Analyze(@"C:\tools\rg.exe", ["--files"]);

        Assert.Equal(TerminalOperationClass.ReadOnly, search.OperationClass);
        Assert.Equal(TerminalOperationClass.ReadOnly, filesWithMatches.OperationClass);
        Assert.Equal(TerminalOperationClass.ReadOnly, listFiles.OperationClass);
        Assert.Equal("1", search.AnalyzerVersion);
    }

    [Fact]
    public void Replacement_forms_ask()
    {
        var shortForm = _analyzer.Analyze(@"C:\tools\rg.exe", ["-r", "replacement", "pattern"]);
        var longForm = _analyzer.Analyze(@"C:\tools\rg.exe", ["--replace", "replacement", "pattern"]);

        Assert.Equal(TerminalOperationClass.Unknown, shortForm.OperationClass);
        Assert.Equal(TerminalOperationClass.Unknown, longForm.OperationClass);
    }

    [Fact]
    public void Preprocessor_forms_ask_because_they_execute_programs()
    {
        var pre = _analyzer.Analyze(@"C:\tools\rg.exe", ["--pre", "decode.sh", "pattern"]);
        var preGlob = _analyzer.Analyze(@"C:\tools\rg.exe", ["--pre-glob", "*.bin", "pattern"]);

        Assert.Equal(TerminalOperationClass.Unknown, pre.OperationClass);
        Assert.Equal(TerminalOperationClass.Unknown, preGlob.OperationClass);
    }
}
