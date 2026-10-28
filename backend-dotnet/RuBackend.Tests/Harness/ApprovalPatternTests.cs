using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Approval;
using RuBackend.Harness;
using RuBackend.Harness.Workspace;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// Focused coverage for the bounded patterns an allow_always decision
/// remembers: the terminal command prefix, the exact shell/flag-first shapes,
/// the one-time-only refusals, and the external folder pattern derivation.
/// </summary>
public sealed class ApprovalPatternTests
{
    [Theory]
    [InlineData("apply_patch", "patch")]
    [InlineData("file_restore", "restore")]
    [InlineData("write", "restore")]
    [InlineData("terminal_read", "read")]
    [InlineData("terminal_stop", "read")]
    [InlineData("bash", "read")]
    public void Opaque_mutation_intents_cannot_become_session_or_permanent_allows(string tool, string operation)
    {
        var binding = Binding(tool, operationClass: operation);
        Assert.False(AgentApprovalsService.CanRememberApproval(binding));
        Assert.Null(AgentV1Service.BuildAllowAlwaysMatcher(binding, "rule_asks", externalDirectoryRequested: false));
    }

    private static ToolApprovalBinding Binding(
        string toolId,
        string? canonicalPath = null,
        string? resolvedExecutable = null,
        IReadOnlyList<string>? argv = null,
        string? cwd = null,
        string operationClass = "terminal") =>
        new(
            new RunId("run_" + new string('a', 32)),
            new ToolCallId("call_" + new string('b', 32)),
            toolId,
            "1.0.0",
            canonicalPath,
            resolvedExecutable,
            argv ?? Array.Empty<string>(),
            cwd,
            operationClass,
            "none",
            1,
            new string('c', 64),
            DateTimeOffset.UtcNow.AddMinutes(30));

    [Fact]
    public void Terminal_run_remembers_the_subcommand_prefix_not_the_full_argv()
    {
        var matcher = AgentV1Service.BuildAllowAlwaysMatcher(
            Binding(
                "bash",
                resolvedExecutable: "git",
                argv: new[] { "status", "--porcelain" },
                cwd: @"C:\work\repo"),
            "terminal_timeout_requires_approval",
            externalDirectoryRequested: false);

        Assert.NotNull(matcher);
        Assert.Equal("bash", matcher!.ToolId);
        Assert.Equal("git", matcher.ResolvedExecutable);
        Assert.Equal(new[] { "status" }, matcher.ArgvPattern);
        Assert.Equal(@"C:\work\repo", matcher.CwdPattern);
        Assert.Equal("terminal", matcher.OperationClass);
    }

    [Theory]
    [InlineData("list_tools", null)]
    [InlineData("call", "create_issue")]
    public void An_mcp_rule_remembers_the_op_and_tool_exactly(string op, string? tool)
    {
        // An empty argv pattern would match every call on the server, so the remembered
        // rule must keep the op (and the tool) exactly as the approved call bound them.
        string[] argv = tool is null ? [op] : [op, tool];
        var matcher = AgentV1Service.BuildAllowAlwaysMatcher(
            Binding("mcp", resolvedExecutable: "mcp:github", argv: argv, operationClass: "mutation"),
            "mcp_call_requires_approval",
            externalDirectoryRequested: false);

        Assert.NotNull(matcher);
        Assert.Equal("mcp", matcher!.ToolId);
        Assert.Equal("mcp:github", matcher.ResolvedExecutable);
        Assert.Equal(argv, matcher.ArgvPattern);
    }

    [Fact]
    public void Terminal_run_keeps_at_most_two_leading_non_flag_tokens()
    {
        var matcher = AgentV1Service.BuildAllowAlwaysMatcher(
            Binding(
                "bash",
                resolvedExecutable: "npm",
                argv: new[] { "run", "build", "--watch" },
                cwd: @"C:\work\repo"),
            "terminal_external_cwd_requires_approval",
            externalDirectoryRequested: false);

        Assert.NotNull(matcher);
        Assert.Equal(new[] { "run", "build" }, matcher!.ArgvPattern);
    }

    [Fact]
    public void Modern_run_process_remembers_the_same_bounded_command_prefix()
    {
        var matcher = AgentV1Service.BuildAllowAlwaysMatcher(
            Binding(
                "run_process",
                resolvedExecutable: "git",
                argv: new[] { "status", "--porcelain", "--branch" },
                cwd: @"C:\work\repo"),
            "terminal_timeout_requires_approval",
            externalDirectoryRequested: false);

        Assert.NotNull(matcher);
        Assert.Equal("run_process", matcher!.ToolId);
        Assert.Equal("git", matcher.ResolvedExecutable);
        Assert.Equal(new[] { "status" }, matcher.ArgvPattern);
        Assert.Equal(@"C:\work\repo", matcher.CwdPattern);
    }

    [Fact]
    public void Modern_run_process_keeps_flag_first_invocations_exact()
    {
        var argv = new[] { "-e", "console.log('hi')" };
        var matcher = AgentV1Service.BuildAllowAlwaysMatcher(
            Binding("run_process", resolvedExecutable: "node", argv: argv),
            "terminal_timeout_requires_approval",
            externalDirectoryRequested: false);

        Assert.NotNull(matcher);
        Assert.Equal(argv, matcher!.ArgvPattern);
    }

    [Fact]
    public void Modern_run_process_keeps_windows_slash_flag_invocations_exact()
    {
        var argv = new[] { "/d", "/c", "echo", "fixture" };
        var matcher = AgentV1Service.BuildAllowAlwaysMatcher(
            Binding("run_process", resolvedExecutable: "cmd.exe", argv: argv),
            "terminal_timeout_requires_approval",
            externalDirectoryRequested: false);

        Assert.NotNull(matcher);
        Assert.Equal(argv, matcher!.ArgvPattern);
    }

    [Fact]
    public void Modern_run_process_keeps_powershell_script_wrappers_exact()
    {
        var argv = new[] { "-NoProfile", "-NonInteractive", "-Command", "Get-ChildItem | Select-String TODO" };
        var matcher = AgentV1Service.BuildAllowAlwaysMatcher(
            Binding("run_process", resolvedExecutable: "pwsh.exe", argv: argv),
            "terminal_timeout_requires_approval",
            externalDirectoryRequested: false);

        Assert.NotNull(matcher);
        Assert.Equal(argv, matcher!.ArgvPattern);
    }

    [Fact]
    public void Flag_first_invocations_stay_exact()
    {
        var matcher = AgentV1Service.BuildAllowAlwaysMatcher(
            Binding(
                "bash",
                resolvedExecutable: "node",
                argv: new[] { "-e", "console.log('hi')" },
                cwd: @"C:\work\repo"),
            "terminal_timeout_requires_approval",
            externalDirectoryRequested: false);

        Assert.NotNull(matcher);
        Assert.Equal(new[] { "-e", "console.log('hi')" }, matcher!.ArgvPattern);
    }

    [Fact]
    public void Shell_mode_invocations_stay_exact()
    {
        var script = "Get-ChildItem | Select-String TODO";
        var matcher = AgentV1Service.BuildAllowAlwaysMatcher(
            Binding(
                "bash",
                resolvedExecutable: "powershell",
                argv: new[] { "-NoProfile", "-NonInteractive", "-Command", script },
                cwd: @"C:\work\repo"),
            "terminal_timeout_requires_approval",
            externalDirectoryRequested: false);

        Assert.NotNull(matcher);
        Assert.Equal(
            new[] { "-NoProfile", "-NonInteractive", "-Command", script },
            matcher!.ArgvPattern);
    }

    [Fact]
    public void Protected_path_and_rule_asks_never_become_a_reusable_rule()
    {
        var binding = Binding(
            "bash",
            resolvedExecutable: "git",
            argv: new[] { "status" },
            cwd: @"C:\Users\user\.ssh");

        Assert.Null(AgentV1Service.BuildAllowAlwaysMatcher(
            binding,
            "terminal_protected_path_requires_approval",
            externalDirectoryRequested: false));
        Assert.Null(AgentV1Service.BuildAllowAlwaysMatcher(
            binding,
            "rule_asks",
            externalDirectoryRequested: false));
    }

    [Fact]
    public void External_ask_remembers_a_path_only_folder_pattern()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-pattern-" + Guid.NewGuid().ToString("N"));
        var sub = Path.Combine(root, "sub");
        Directory.CreateDirectory(sub);
        var file = Path.Combine(sub, "note.txt");
        File.WriteAllText(file, "x");
        try
        {
            var matcher = AgentV1Service.BuildAllowAlwaysMatcher(
                Binding("read", canonicalPath: file, operationClass: "read"),
                "external_directory_required",
                externalDirectoryRequested: true);

            var expectedDirectory = WindowsWorkspaceIdentityResolver.GetCanonicalRoot(sub)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Assert.NotNull(matcher);
            Assert.Null(matcher!.ToolId);
            Assert.Null(matcher.OperationClass);
            Assert.Equal(expectedDirectory + "\\**", matcher.CanonicalPathPattern);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void External_folder_target_and_missing_leaf_both_grant_the_nearest_existing_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-pattern-" + Guid.NewGuid().ToString("N"));
        var sub = Path.Combine(root, "sub");
        Directory.CreateDirectory(sub);
        try
        {
            var expectedDirectory = WindowsWorkspaceIdentityResolver.GetCanonicalRoot(sub)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            Assert.Equal(
                expectedDirectory + "\\**",
                AgentV1Service.BuildExternalDirectoryPattern(sub));
            Assert.Equal(
                expectedDirectory + "\\**",
                AgentV1Service.BuildExternalDirectoryPattern(Path.Combine(sub, "new-dir", "file.txt")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
