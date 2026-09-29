using RuAgent.Broker.Policy.Commands;
using RuAgent.Broker.Tools.Terminal;
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
        Assert.Equal("2", analysis.AnalyzerVersion);
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
    [InlineData("grep", "--open-files-in-pager=calc.exe")]
    [InlineData("grep", "-Ocalc.exe")]
    [InlineData("grep", "--paginate")]
    [InlineData("log", "--paginate")]
    [InlineData("diff", "--ext-diff")]
    [InlineData("diff", "--ext-diff=diff.cmd")]
    [InlineData("log", "--textconv")]
    [InlineData("show", "--textconv=cat.exe")]
    public void Options_that_make_git_execute_another_program_never_stay_read_only(
        string verb,
        string option)
    {
        // git grep -O spawns the named program, --ext-diff and --textconv run
        // repository-configured helpers, and --paginate forces the pager. None of
        // them may keep the auto-executable ReadOnly verdict, or a model could run
        // an arbitrary program with no approval.
        var analysis = _analyzer.Analyze(@"C:\tools\git.exe", [verb, option, "secret"]);

        Assert.NotEqual(TerminalOperationClass.ReadOnly, analysis.OperationClass);
        Assert.False(TerminalAutoPolicy.IsAutoExecutable(analysis.OperationClass));
    }

    [Fact]
    public void Submodule_status_is_the_only_deterministic_bounded_form()
    {
        var analysis = _analyzer.Analyze(@"C:\tools\git.exe", ["submodule", "status", "--recursive"]);

        Assert.Equal(TerminalOperationClass.ReadOnly, analysis.OperationClass);
        Assert.Equal("none", analysis.NetworkIntent);
    }

    [Fact]
    public void Other_submodule_forms_fail_closed_as_external_mutations()
    {
        string[][] forms =
        [
            ["submodule"],
            ["submodule", "foreach", "git status"],
            ["submodule", "foreach", "--exec", "calc.exe"],
            ["submodule", "update", "--remote"],
            ["submodule", "update", "--fetch"],
            ["submodule", "sync"],
            ["submodule", "set-url", "origin", @"C:\outside"],
            ["submodule", "foreach", "--output=export.txt", "git status"],
        ];

        foreach (var form in forms)
        {
            var analysis = _analyzer.Analyze(@"C:\tools\git.exe", form);
            Assert.Equal(TerminalOperationClass.ExternalMutation, analysis.OperationClass);
            Assert.Equal("git_submodule_requires_approval", analysis.ReasonCode);
            Assert.Equal("unknown", analysis.NetworkIntent);
        }
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
    }

    [Fact]
    public void Repository_local_config_writes_never_take_the_automatic_path()
    {
        // A repo-local write can install program-selecting keys (core.fsmonitor,
        // core.hooksPath, diff.external, aliases). It must ask even though the
        // file lives inside the workspace.
        string[][] writes =
        [
            ["config", "user.name", "x"],
            ["config", "core.hooksPath", @"C:\probe-hooks"],
            ["config", "core.fsmonitor", "true"],
            ["config", "--add", "alias.x", "!calc"],
            ["config", "--unset", "core.fsmonitor"],
            ["config", "--unset-all", "alias.x"],
            ["config", "--replace-all", "core.hooksPath", @"C:\probe-hooks"],
            ["config", "--rename-section", "a", "b"],
            ["config", "--remove-section", "a"],
        ];

        foreach (var form in writes)
        {
            var analysis = _analyzer.Analyze(@"C:\tools\git.exe", form);
            Assert.Equal(TerminalOperationClass.Unknown, analysis.OperationClass);
            Assert.Equal("git_config_mutation_requires_approval", analysis.ReasonCode);
            Assert.False(TerminalAutoPolicy.IsAutoExecutable(analysis.OperationClass));
        }
    }

    [Fact]
    public void Repository_local_config_reads_stay_read_only()
    {
        string[][] reads =
        [
            ["config", "user.name"],
            ["config", "--get", "user.name"],
            ["config", "--get-all", "user.name"],
            ["config", "--get-regexp", "user\\..*"],
            ["config", "--list"],
            ["config", "-l"],
            ["config", "--show-origin", "--get", "user.name"],
            ["config", "--type", "bool", "--get", "core.autocrlf"],
        ];

        foreach (var form in reads)
        {
            var analysis = _analyzer.Analyze(@"C:\tools\git.exe", form);
            Assert.Equal(TerminalOperationClass.ReadOnly, analysis.OperationClass);
            Assert.Equal("git_config_read", analysis.ReasonCode);
        }
    }
}
