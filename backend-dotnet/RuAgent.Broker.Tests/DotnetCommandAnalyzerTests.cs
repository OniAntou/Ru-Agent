using RuAgent.Broker.Policy.Commands;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class DotnetCommandAnalyzerTests
{
    private readonly DotnetCommandAnalyzer _analyzer = new();

    [Theory]
    [InlineData("test", TerminalOperationClass.Test)]
    [InlineData("build", TerminalOperationClass.Build)]
    [InlineData("vstest", TerminalOperationClass.Test)]
    public void Build_and_test_verbs_are_recognized(string verb, TerminalOperationClass expected)
    {
        var analysis = _analyzer.Analyze(@"C:\dotnet\dotnet.exe", [verb, "--nologo"]);

        Assert.Equal(expected, analysis.OperationClass);
        Assert.Equal("1", analysis.AnalyzerVersion);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("--info")]
    [InlineData("--list-sdks")]
    [InlineData("--list-runtimes")]
    public void Diagnostic_forms_are_read_only(string flag)
    {
        var analysis = _analyzer.Analyze(@"C:\dotnet\dotnet.exe", [flag]);

        Assert.Equal(TerminalOperationClass.ReadOnly, analysis.OperationClass);
    }

    [Fact]
    public void Restore_is_an_external_mutation_with_declared_network()
    {
        var analysis = _analyzer.Analyze(@"C:\dotnet\dotnet.exe", ["restore"]);

        Assert.Equal(TerminalOperationClass.ExternalMutation, analysis.OperationClass);
        Assert.Equal("declared", analysis.NetworkIntent);
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("pack")]
    [InlineData("tool")]
    [InlineData("new")]
    [InlineData("format")]
    public void Deploy_and_packaging_verbs_are_external_mutations(string verb)
    {
        var analysis = _analyzer.Analyze(@"C:\dotnet\dotnet.exe", [verb]);

        Assert.Equal(TerminalOperationClass.ExternalMutation, analysis.OperationClass);
    }

    [Fact]
    public void Run_executes_arbitrary_code_and_fails_closed()
    {
        var analysis = _analyzer.Analyze(@"C:\dotnet\dotnet.exe", ["run", "--project", "app"]);

        Assert.Equal(TerminalOperationClass.Unknown, analysis.OperationClass);
    }

    [Fact]
    public void Unknown_verbs_fail_closed()
    {
        var analysis = _analyzer.Analyze(@"C:\dotnet\dotnet.exe", ["frobnicate"]);

        Assert.Equal(TerminalOperationClass.Unknown, analysis.OperationClass);
    }
}
