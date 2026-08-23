using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools;
using RuAgent.Broker.Tools.Terminal;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class TerminalAdapterTests
{
    [Fact]
    public void Adapter_rejects_unregistered_commands_and_unscoped_network_requests()
    {
        var root = CreateTemporaryRoot("ru-terminal-");
        try
        {
            var adapter = CreateAdapter(root);
            Assert.Throws<UnauthorizedAccessException>(() =>
            {
                _ = adapter.RunAsync(
                    MakeGrantWithScopes(ToolScope.TerminalExecute, ToolScope.TerminalNetwork),
                    new TerminalRunRequest("powershell", ".", "format.exe", [], 1_000),
                    confineWorkspace: true,
                    CancellationToken.None);
            });

            var error = Assert.Throws<UnauthorizedAccessException>(() =>
            {
                _ = adapter.RunAsync(
                    MakeGrantWithScopes(ToolScope.TerminalExecute),
                    new TerminalRunRequest(
                        "powershell",
                        ".",
                        "python",
                        ["-c", "print(1)"],
                        1_000,
                        Network: true),
                    confineWorkspace: true,
                    CancellationToken.None);
            });
            Assert.Equal("terminal_network_requires_approval", error.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NetworkCommandAllowedWhenGrantIncludesNetworkScope()
    {
        var root = CreateTemporaryRoot("ru-terminal-network-granted-");
        try
        {
            var adapter = CreateAdapter(root);
            var result = await adapter.RunAsync(
                MakeGrantWithScopes(ToolScope.TerminalExecute, ToolScope.TerminalNetwork),
                Request("python", ["-c", "print(1)"], network: true),
                confineWorkspace: false,
                CancellationToken.None);
            Assert.Equal("succeeded", result.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NetworkCommandStillRejectedWithoutNetworkScope()
    {
        var root = CreateTemporaryRoot("ru-terminal-network-denied-");
        try
        {
            var adapter = CreateAdapter(root);
            var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                adapter.RunAsync(
                    MakeGrantWithScopes(ToolScope.TerminalExecute),
                    Request("python", ["-c", "print(1)"], network: true),
                    confineWorkspace: true,
                    CancellationToken.None));
            Assert.Equal("terminal_network_requires_approval", error.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Unrestricted_confinement_allows_working_directories_outside_the_workspace()
    {
        var parent = CreateTemporaryRoot("ru-terminal-unconfined-");
        var workspaceRoot = Path.Combine(parent, "workspace");
        var outsideDirectory = Path.Combine(parent, "outside");
        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(outsideDirectory);
        try
        {
            var adapter = CreateAdapter(workspaceRoot);
            var result = await adapter.RunAsync(
                MakeGrantWithScopes(ToolScope.TerminalExecute, ToolScope.TerminalNetwork),
                Request(
                    "python",
                    ["-c", "print(1)"],
                    network: true,
                    workingDirectory: Path.Combine("..", "outside")),
                confineWorkspace: false,
                CancellationToken.None);
            Assert.Equal("succeeded", result.Status);
            Assert.Equal(Path.GetFullPath(outsideDirectory), result.WorkingDirectory);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task Unrestricted_confinement_still_rejects_protected_system_working_directories()
    {
        var root = CreateTemporaryRoot("ru-terminal-protected-cwd-");
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.False(string.IsNullOrWhiteSpace(windowsDirectory));
        try
        {
            var adapter = CreateAdapter(root);
            var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                adapter.RunAsync(
                    MakeGrantWithScopes(ToolScope.TerminalExecute, ToolScope.TerminalNetwork),
                    Request("python", ["-c", "print(1)"], workingDirectory: windowsDirectory),
                    confineWorkspace: false,
                    CancellationToken.None));
            Assert.Equal("terminal_cwd_not_allowed", error.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Confinement_flag_remains_independent_of_network_scope()
    {
        var root = CreateTemporaryRoot("ru-terminal-independence-");
        try
        {
            var adapter = CreateAdapter(root);
            var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                adapter.RunAsync(
                    MakeGrantWithScopes(ToolScope.TerminalExecute, ToolScope.TerminalNetwork),
                    Request(
                        "python",
                        ["-c", "print(1)"],
                        network: true,
                        workingDirectory: ".."),
                    confineWorkspace: true,
                    CancellationToken.None));
            Assert.Equal("workspace_path_outside_root", error.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Raw_shell_requires_a_bound_one_time_approval_token()
    {
        var root = CreateTemporaryRoot("ru-terminal-shell-");
        try
        {
            var approvals = new ApprovalTokenService();
            var argumentsHash = "script-hash";
            var token = approvals.Issue(
                "request-1",
                "session-1",
                "terminal.shell",
                "1.0.0",
                argumentsHash,
                TimeSpan.FromSeconds(30));
            var adapter = new TerminalAdapter(
                new WorkspacePathGuard(root),
                new ProcessTreeRunner(),
                new ShellRuntimeResolver(),
                approvals);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                adapter.RunApprovedShellAsync(
                    new TerminalShellRequest(
                        "powershell",
                        ".",
                        "Get-Date",
                        1_000,
                        token,
                        "request-1",
                        "session-1",
                        "different-hash"),
                    CancellationToken.None));

            var result = await adapter.RunApprovedShellAsync(
                new TerminalShellRequest(
                    "powershell",
                    ".",
                    "Get-Date",
                    1_000,
                    token,
                    "request-1",
                    "session-1",
                    argumentsHash),
                CancellationToken.None);
            Assert.Equal("succeeded", result.Status);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                adapter.RunApprovedShellAsync(
                    new TerminalShellRequest(
                        "powershell",
                        ".",
                        "Get-Date",
                        1_000,
                        token,
                        "request-1",
                        "session-1",
                        argumentsHash),
                    CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static TerminalAdapter CreateAdapter(string root) => new(
        new WorkspacePathGuard(root),
        new ProcessTreeRunner(),
        new ShellRuntimeResolver());

    private static PolicyGrant MakeGrantWithScopes(params string[] scopes) => new(
        "session-terminal-" + Guid.NewGuid().ToString("N"),
        new HashSet<string>(scopes, StringComparer.Ordinal),
        DateTimeOffset.UtcNow.AddMinutes(30));

    private static TerminalRunRequest Request(
        string executable,
        IReadOnlyList<string> arguments,
        bool network = false,
        string workingDirectory = ".") => new(
        "powershell",
        workingDirectory,
        executable,
        arguments,
        30_000,
        network);

    private static string CreateTemporaryRoot(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
