using System.Text.Json;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Broker.Tools.Terminal;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class CommandIntentResolverTests
{
    private static readonly string[] EmptySearchDirectories = [];

    private static string CreateTemporaryRoot(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static CommandIntentResolver CreateResolver(string workspaceRoot, IReadOnlyList<string>? searchDirectories = null) =>
        new(workspaceRoot, searchDirectories);

    [Fact]
    public void Resolves_bare_executable_through_sanitized_search_directories()
    {
        var workspace = CreateTemporaryRoot("ru-intent-ws-");
        var toolsDirectory = CreateTemporaryRoot("ru-intent-tools-");
        var fakeTool = Path.Combine(toolsDirectory, "ru-fake-tool.cmd");
        File.WriteAllText(fakeTool, "@echo ok\r\n");
        try
        {
            var resolver = CreateResolver(workspace, [toolsDirectory]);
            var intent = resolver.ResolveRun(new CanonicalCommandRequest("ru-fake-tool"));

            Assert.Equal(Path.GetFullPath(fakeTool), intent.ResolvedExecutable);
            Assert.True(File.Exists(intent.ResolvedExecutable));
            Assert.Equal(PathClassification.InsideWorkspace, intent.WorkingDirectoryClass);
            Assert.Equal(TerminalOperationClass.Unknown, intent.OperationClass);
            Assert.Equal(TerminalLimits.DefaultTimeoutMs, intent.TimeoutMs);
            Assert.Equal(TerminalTimeoutClass.StandardWindow, intent.TimeoutClass);
            Assert.False(intent.NetworkIntent);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
            Directory.Delete(toolsDirectory, recursive: true);
        }
    }

    [Fact]
    public void Preserves_argument_vector_without_shell_joining()
    {
        var workspace = CreateTemporaryRoot("ru-intent-argv-");
        try
        {
            var resolver = CreateResolver(workspace, EmptySearchDirectories);
            var arguments = new[] { "-la", "a b", "--flag=value", "\"quoted\"" };
            var intent = resolver.ResolveRun(
                new CanonicalCommandRequest(Path.Combine(Environment.SystemDirectory, "cmd.exe"), arguments));

            Assert.Equal(arguments, intent.Arguments.ToArray());
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void Defaults_anchor_to_workspace_root()
    {
        var workspace = CreateTemporaryRoot("ru-intent-defaults-");
        try
        {
            var resolver = CreateResolver(workspace, EmptySearchDirectories);
            var executable = Path.Combine(Environment.SystemDirectory, "where.exe");
            var intent = resolver.ResolveRun(new CanonicalCommandRequest(executable));

            Assert.Equal(resolver.CanonicalWorkspaceRoot, intent.CanonicalWorkingDirectory);
            Assert.Equal(TerminalLimits.DefaultTimeoutMs, intent.TimeoutMs);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void Classifies_canonical_working_directory()
    {
        var parent = CreateTemporaryRoot("ru-intent-classify-");
        var workspace = Path.Combine(parent, "workspace");
        Directory.CreateDirectory(workspace);
        var outside = Path.Combine(parent, "outside");
        Directory.CreateDirectory(outside);
        var protectedDirectory = Path.Combine(workspace, ".git");
        Directory.CreateDirectory(protectedDirectory);
        var subDirectory = Path.Combine(workspace, "sub", "dir");
        Directory.CreateDirectory(subDirectory);
        try
        {
            var resolver = CreateResolver(workspace, EmptySearchDirectories);
            var executable = Path.Combine(Environment.SystemDirectory, "where.exe");

            var inside = resolver.ResolveRun(new CanonicalCommandRequest(executable, WorkingDirectory: "sub/dir"));
            Assert.Equal(subDirectory, inside.CanonicalWorkingDirectory);
            Assert.Equal(PathClassification.InsideWorkspace, inside.WorkingDirectoryClass);

            var outsideIntent = resolver.ResolveRun(new CanonicalCommandRequest(executable, WorkingDirectory: outside));
            Assert.Equal(PathClassification.OutsideWorkspace, outsideIntent.WorkingDirectoryClass);

            var protectedIntent = resolver.ResolveRun(new CanonicalCommandRequest(executable, WorkingDirectory: ".git"));
            Assert.Equal(PathClassification.Protected, protectedIntent.WorkingDirectoryClass);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Theory]
    [InlineData(1_200_000, TerminalTimeoutClass.ApprovalRequired)]
    [InlineData(900_000, TerminalTimeoutClass.StandardWindow)]
    [InlineData(900_001, TerminalTimeoutClass.ApprovalRequired)]
    [InlineData(1_800_000, TerminalTimeoutClass.ApprovalRequired)]
    public void Timeout_class_follows_the_standard_window_boundary(int timeoutMs, TerminalTimeoutClass expected)
    {
        var workspace = CreateTemporaryRoot("ru-intent-timeout-");
        try
        {
            var resolver = CreateResolver(workspace, EmptySearchDirectories);
            var executable = Path.Combine(Environment.SystemDirectory, "where.exe");
            var intent = resolver.ResolveRun(new CanonicalCommandRequest(executable, TimeoutMs: timeoutMs));

            Assert.Equal(timeoutMs, intent.TimeoutMs);
            Assert.Equal(expected, intent.TimeoutClass);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void Rejects_timeouts_over_the_absolute_thirty_minute_cap()
    {
        var workspace = CreateTemporaryRoot("ru-intent-cap-");
        try
        {
            var resolver = CreateResolver(workspace, EmptySearchDirectories);
            var executable = Path.Combine(Environment.SystemDirectory, "where.exe");
            var error = Assert.Throws<CommandIntentException>(() =>
                resolver.ResolveRun(new CanonicalCommandRequest(executable, TimeoutMs: 1_800_001)));
            Assert.Equal("timeout_exceeds_hard_max", error.Code);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void Rejects_invalid_executable_and_unbounded_argument_vectors()
    {
        var workspace = CreateTemporaryRoot("ru-intent-bounds-");
        try
        {
            var resolver = CreateResolver(workspace, EmptySearchDirectories);

            Assert.Equal("executable_invalid", Assert.Throws<CommandIntentException>(
                () => resolver.ResolveRun(new CanonicalCommandRequest(""))).Code);
            Assert.Equal("executable_invalid", Assert.Throws<CommandIntentException>(
                () => resolver.ResolveRun(new CanonicalCommandRequest("   "))).Code);
            Assert.Equal("argument_nul_rejected", Assert.Throws<CommandIntentException>(
                () => resolver.ResolveRun(new CanonicalCommandRequest(
                    Path.Combine(Environment.SystemDirectory, "where.exe"), ["ok\0bad"]))).Code);
            Assert.Equal("argv_too_long", Assert.Throws<CommandIntentException>(
                () => resolver.ResolveRun(new CanonicalCommandRequest(
                    Path.Combine(Environment.SystemDirectory, "where.exe"),
                    Enumerable.Range(0, TerminalLimits.MaxArguments + 1).Select(i => $"a{i}").ToArray()))).Code);
            Assert.Equal("argument_too_long", Assert.Throws<CommandIntentException>(
                () => resolver.ResolveRun(new CanonicalCommandRequest(
                    Path.Combine(Environment.SystemDirectory, "where.exe"),
                    [new string('a', TerminalLimits.MaxArgumentChars + 1)]))).Code);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void Rejects_interactive_password_prompt_and_background_daemon_forms()
    {
        var workspace = CreateTemporaryRoot("ru-intent-deny-");
        try
        {
            var resolver = CreateResolver(workspace, ["C:\\Windows\\System32"]);
            var executable = Path.Combine(Environment.SystemDirectory, "where.exe");

            Assert.Equal("interactive_flag_rejected", Assert.Throws<CommandIntentException>(
                () => resolver.ResolveRun(new CanonicalCommandRequest(executable, ["--tty"]))).Code);
            Assert.Equal("password_prompt_rejected", Assert.Throws<CommandIntentException>(
                () => resolver.ResolveRun(new CanonicalCommandRequest(executable, ["sudo"]))).Code);
            Assert.Equal("background_daemon_rejected", Assert.Throws<CommandIntentException>(
                () => resolver.ResolveRun(new CanonicalCommandRequest(executable, ["--daemon"]))).Code);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void Shell_requests_require_explicit_powershell_runtimes()
    {
        var workspace = CreateTemporaryRoot("ru-intent-shell-");
        try
        {
            var resolver = CreateResolver(workspace, ["C:\\Windows\\System32"]);
            var powershellIntent = resolver.ResolveShell(new CanonicalShellRequest("Get-Date"));
            Assert.Contains("powershell", Path.GetFileName(powershellIntent.ResolvedExecutable), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(["Get-Date"], powershellIntent.Arguments.ToArray());

            Assert.Equal("unsupported_shell_runtime", Assert.Throws<CommandIntentException>(
                () => resolver.ResolveShell(new CanonicalShellRequest("dir", ShellRuntime: "cmd"))).Code);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void Invalid_or_non_directory_working_directories_fail_closed()
    {
        var workspace = CreateTemporaryRoot("ru-intent-cwd-invalid-");
        try
        {
            var resolver = CreateTemporaryRootResolver(workspace);
            var executable = Path.Combine(Environment.SystemDirectory, "where.exe");
            var filePath = Path.Combine(workspace, "not-a-dir.txt");
            File.WriteAllText(filePath, "x");

            Assert.Equal("working_directory_invalid", Assert.Throws<CommandIntentException>(
                () => resolver.ResolveRun(new CanonicalCommandRequest(executable, WorkingDirectory: "not-a-dir.txt"))).Code);
            Assert.Equal("working_directory_invalid", Assert.Throws<CommandIntentException>(
                () => resolver.ResolveRun(new CanonicalCommandRequest(executable, WorkingDirectory: "missing-dir/deeper"))).Code);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void Absolute_executables_resolve_directly_and_missing_paths_fail_closed()
    {
        var workspace = CreateTemporaryRoot("ru-intent-abs-");
        var toolsDirectory = CreateTemporaryRoot("ru-intent-abs-tools-");
        try
        {
            var resolver = CreateResolver(workspace, EmptySearchDirectories);
            var fakeTool = Path.Combine(toolsDirectory, "abs-tool.cmd");
            File.WriteAllText(fakeTool, "@echo ok\r\n");

            var direct = resolver.ResolveRun(new CanonicalCommandRequest(fakeTool));
            Assert.Equal(Path.GetFullPath(fakeTool), direct.ResolvedExecutable);

            var missing = Path.Combine(toolsDirectory, "missing-tool.exe");
            Assert.Equal("executable_not_found", Assert.Throws<CommandIntentException>(
                () => resolver.ResolveRun(new CanonicalCommandRequest(missing))).Code);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
            Directory.Delete(toolsDirectory, recursive: true);
        }
    }

    [Fact]
    public void Intent_hash_is_stable_and_binds_every_execution_identity_field()
    {
        var parent = CreateTemporaryRoot("ru-intent-hash-");
        var workspace = Path.Combine(parent, "workspace");
        Directory.CreateDirectory(workspace);
        var other = Path.Combine(parent, "other");
        Directory.CreateDirectory(other);
        var toolsDirectory = CreateTemporaryRoot("ru-intent-hash-tools-");
        try
        {
            var resolver = CreateResolver(workspace, [toolsDirectory]);
            var executable = Path.Combine(Environment.SystemDirectory, "where.exe");
            var baseline = resolver.ResolveRun(new CanonicalCommandRequest(executable, ["-q"], NetworkIntent: true));

            var same = resolver.ResolveRun(new CanonicalCommandRequest(executable, ["-q"], NetworkIntent: true));
            Assert.Equal(baseline.IntentHash, same.IntentHash);

            var differentArguments = resolver.ResolveRun(new CanonicalCommandRequest(executable, ["-r"], NetworkIntent: true));
            Assert.NotEqual(baseline.IntentHash, differentArguments.IntentHash);

            var differentTimeout = resolver.ResolveRun(new CanonicalCommandRequest(executable, ["-q"], TimeoutMs: 60_000));
            Assert.NotEqual(baseline.IntentHash, differentTimeout.IntentHash);

            var differentNetwork = resolver.ResolveRun(new CanonicalCommandRequest(executable, ["-q"]));
            Assert.NotEqual(baseline.IntentHash, differentNetwork.IntentHash);

            var differentExecutable = resolver.ResolveRun(
                new CanonicalCommandRequest(Path.Combine(Environment.SystemDirectory, "cmd.exe")));
            Assert.NotEqual(baseline.IntentHash, differentExecutable.IntentHash);

            var differentWorkingDirectory = resolver.ResolveRun(
                new CanonicalCommandRequest(executable, ["-q"], WorkingDirectory: other));
            Assert.NotEqual(baseline.IntentHash, differentWorkingDirectory.IntentHash);

            Assert.Equal(64, baseline.IntentHash.Length);
            Assert.True(baseline.IntentHash.All(char.IsAsciiHexDigit));

            var classified = baseline.Classified(TerminalOperationClass.ReadOnly);
            Assert.Equal(TerminalOperationClass.ReadOnly, classified.OperationClass);
            Assert.NotEqual(baseline.IntentHash, classified.IntentHash);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
            Directory.Delete(toolsDirectory, recursive: true);
        }
    }

    private static CommandIntentResolver CreateTemporaryRootResolver(string workspace) =>
        new(workspace, ["C:\\Windows\\System32"]);
}
