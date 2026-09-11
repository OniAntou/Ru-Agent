using RuAgent.Broker.Policy.Commands;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class GitCommandAnalyzerTests
{
    private readonly GitCommandAnalyzer _analyzer = new();

    [Theory]
    [InlineData("status")]
    [InlineData("diff")]
    [InlineData("log")]
    [InlineData("show")]
    [InlineData("rev-parse")]
    [InlineData("ls-files")]
    public void Read_only_git_verbs_classify_as_read_only(string verb)
    {
        var analysis = _analyzer.Analyze(@"C:\Program Files\Git\bin\git.exe", [verb]);

        Assert.Equal(TerminalOperationClass.ReadOnly, analysis.OperationClass);
        Assert.Equal("none", analysis.NetworkIntent);
        Assert.Equal("1", analysis.AnalyzerVersion);
    }

    [Fact]
    public void Branch_show_current_is_read_only_but_other_branch_forms_are_mutations()
    {
        Assert.Equal(
            TerminalOperationClass.ReadOnly,
            _analyzer.Analyze(@"C:\tools\git.exe", ["branch", "--show-current"]).OperationClass);
        Assert.Equal(
            TerminalOperationClass.GitMutation,
            _analyzer.Analyze(@"C:\tools\git.exe", ["branch", "-D", "feature"]).OperationClass);
    }

    [Fact]
    public void The_no_optional_locks_global_flag_is_understood_before_the_verb()
    {
        var analysis = _analyzer.Analyze(@"C:\tools\git.exe", ["--no-optional-locks", "status"]);

        Assert.Equal(TerminalOperationClass.ReadOnly, analysis.OperationClass);
    }

    [Fact]
    public void Output_redirect_flags_make_reads_mutating()
    {
        var analysis = _analyzer.Analyze(@"C:\tools\git.exe", ["log", "--output=export.txt"]);

        Assert.Equal(TerminalOperationClass.GitMutation, analysis.OperationClass);
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("add")]
    [InlineData("reset")]
    [InlineData("checkout")]
    [InlineData("switch")]
    [InlineData("merge")]
    [InlineData("rebase")]
    [InlineData("clean")]
    [InlineData("rm")]
    [InlineData("tag")]
    public void Local_git_mutations_are_recognized(string verb)
    {
        var analysis = _analyzer.Analyze(@"C:\tools\git.exe", [verb]);

        Assert.Equal(TerminalOperationClass.GitMutation, analysis.OperationClass);
        Assert.Equal("none", analysis.NetworkIntent);
    }

    [Theory]
    [InlineData("push")]
    [InlineData("fetch")]
    [InlineData("pull")]
    [InlineData("clone")]
    [InlineData("ls-remote")]
    [InlineData("remote")]
    public void Network_git_verbs_are_external_and_network_bound(string verb)
    {
        var analysis = _analyzer.Analyze(@"C:\tools\git.exe", [verb]);

        Assert.Equal(TerminalOperationClass.ExternalMutation, analysis.OperationClass);
        Assert.Equal("declared", analysis.NetworkIntent);
    }

    [Fact]
    public void Unknown_git_verbs_fail_closed_to_unknown()
    {
        var analysis = _analyzer.Analyze(@"C:\tools\git.exe", ["frobnicate"]);

        Assert.Equal(TerminalOperationClass.Unknown, analysis.OperationClass);
    }

    [Fact]
    public void Missing_verb_fails_closed()
    {
        Assert.Equal(TerminalOperationClass.Unknown, _analyzer.Analyze(@"C:\tools\git.exe", []).OperationClass);
    }

    [Fact]
    public void Commit_forms_stay_git_mutations_regardless_of_flags_or_position()
    {
        Assert.Equal(
            TerminalOperationClass.GitMutation,
            _analyzer.Analyze(@"C:\tools\git.exe", ["commit", "-m", "message"]).OperationClass);
        Assert.Equal(
            TerminalOperationClass.GitMutation,
            _analyzer.Analyze(@"C:\tools\git.exe", ["--no-optional-locks", "commit"]).OperationClass);
        Assert.Equal(
            TerminalOperationClass.GitMutation,
            _analyzer.Analyze(@"C:\tools\git.exe", ["-c", "user.name=x", "commit"]).OperationClass);
        Assert.Equal("none", _analyzer.Analyze(@"C:\tools\git.exe", ["commit", "-m", "m"]).NetworkIntent);
    }

    [Fact]
    public void Directory_overrides_require_approval()
    {
        Assert.Equal(
            TerminalOperationClass.Unknown,
            _analyzer.Analyze(@"C:\tools\git.exe", ["-C", @"C:\other-repo", "log"]).OperationClass);
        Assert.Equal(
            TerminalOperationClass.Unknown,
            _analyzer.Analyze(@"C:\tools\git.exe", ["-C", ".", "status"]).OperationClass);
        Assert.Equal(
            "git_directory_override_requires_approval",
            _analyzer.Analyze(@"C:\tools\git.exe", ["-C", ".", "status"]).ReasonCode);
    }

    [Theory]
    [InlineData("diff.external=C:\\probe-evil.exe")]
    [InlineData("core.hooksPath=C:\\probe-hooks")]
    [InlineData("core.fsmonitor=true")]
    [InlineData("include.path=C:\\outside.gitconfig")]
    [InlineData("alias.x=!calc")]
    public void Config_overrides_that_can_select_programs_require_approval(string assignment)
    {
        var analysis = _analyzer.Analyze(@"C:\tools\git.exe", ["-c", assignment, "log"]);

        Assert.Equal(TerminalOperationClass.Unknown, analysis.OperationClass);
        Assert.Equal("git_config_override_requires_approval", analysis.ReasonCode);
    }

    [Fact]
    public void Inert_config_overrides_keep_the_automatic_path()
    {
        Assert.Equal(
            TerminalOperationClass.ReadOnly,
            _analyzer.Analyze(@"C:\tools\git.exe", ["-c", "core.quotepath=false", "log"]).OperationClass);
    }

    [Fact]
    public void External_config_mutations_require_approval()
    {
        Assert.Equal(
            TerminalOperationClass.ExternalMutation,
            _analyzer.Analyze(@"C:\tools\git.exe", ["config", "--global", "core.hooksPath", @"C:\probe-hooks"]).OperationClass);
        Assert.Equal(
            TerminalOperationClass.ExternalMutation,
            _analyzer.Analyze(@"C:\tools\git.exe", ["config", "--system", "core.fsmonitor", "true"]).OperationClass);
        Assert.Equal(
            TerminalOperationClass.ExternalMutation,
            _analyzer.Analyze(@"C:\tools\git.exe", ["config", "--edit"]).OperationClass);
        Assert.Equal(
            TerminalOperationClass.GitMutation,
            _analyzer.Analyze(@"C:\tools\git.exe", ["config", "user.name", "x"]).OperationClass);
    }
}
