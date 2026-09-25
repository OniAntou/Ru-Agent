using System.Diagnostics;
using System.Runtime.ExceptionServices;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools;
using RuAgent.Broker.Tools.Git;
using RuAgent.Broker.Tools.Terminal;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class TerminalAdapterTests
{
    [Fact]
    public async Task Terminal_write_set_metadata_rejects_read_only_calls_with_paths()
    {
        var root = CreateTemporaryRoot("ru-terminal-write-set-");
        try
        {
            var adapter = CreateAdapter(root);
            var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapter.RunAsync(
                MakeGrantWithScopes(ToolScope.TerminalExecute),
                Request("rg", ["--files"]) with { ReadOnly = true, WriteSet = ["src/app.cs"] },
                confineWorkspace: true,
                CancellationToken.None));
            Assert.Equal("invalid_terminal_write_set", error.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Adapter_rejects_unregistered_commands_and_unapproved_network_requests()
    {
        var root = CreateTemporaryRoot("ru-terminal-");
        try
        {
            var adapter = CreateAdapter(root);
            var allowlistError = Assert.Throws<UnauthorizedAccessException>(() =>
            {
                _ = adapter.RunAsync(
                    MakeGrantWithScopes(ToolScope.TerminalExecute),
                    new TerminalRunRequest("powershell", ".", "notepad.exe", [], 1_000),
                    confineWorkspace: true,
                    CancellationToken.None);
            });
            Assert.Equal("terminal_command_not_allowed", allowlistError.Message);

            var error = Assert.Throws<UnauthorizedAccessException>(() =>
            {
                _ = adapter.RunAsync(
                    MakeGrantWithScopes(ToolScope.TerminalExecute),
                    new TerminalRunRequest(
                        "powershell",
                        ".",
                        "git",
                        ["status"],
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
    public async Task Recognized_read_only_commands_execute_without_an_approval_token()
    {
        var root = CreateTemporaryRoot("ru-terminal-recognized-");
        try
        {
            File.WriteAllText(Path.Combine(root, "seen.txt"), "content");
            var adapter = CreateAdapter(root);
            var result = await adapter.RunAsync(
                MakeGrantWithScopes(ToolScope.TerminalExecute),
                Request("rg", ["--files"]),
                confineWorkspace: true,
                CancellationToken.None);
            Assert.Equal("succeeded", result.Status);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("seen.txt", result.StdoutHead);
            Assert.True(result.StdoutBytes > 0);
            Assert.Equal(Path.GetFullPath(root), result.WorkingDirectory);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Unrecognized_commands_require_an_intent_bound_one_time_token()
    {
        var root = CreateTemporaryRoot("ru-terminal-token-");
        try
        {
            var approvals = new ApprovalTokenService();
            var adapter = new TerminalAdapter(
                new WorkspacePathGuard(root),
                approvals);

            var unauthorized = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                adapter.RunAsync(
                    MakeGrantWithScopes(ToolScope.TerminalExecute),
                    Request("python", ["-c", "print(1)"]),
                    confineWorkspace: true,
                    CancellationToken.None));
            Assert.Equal("terminal_command_not_allowed", unauthorized.Message);

            // A token bound to a different intent hash never authorizes the call.
            var wrongBindingToken = approvals.Issue(
                "request-1",
                "session-1",
                "terminal.run",
                "1.0.0",
                "not-the-intent-hash",
                TimeSpan.FromSeconds(60));
            var stale = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                adapter.RunAsync(
                    MakeGrantWithScopes(ToolScope.TerminalExecute),
                    Request(
                        "python",
                        ["-c", "print(1)"],
                        approvalToken: wrongBindingToken,
                        requestId: "request-1",
                        sessionId: "session-1"),
                    confineWorkspace: true,
                    CancellationToken.None));
            Assert.Equal("terminal_approval_invalid", stale.Message);

            // A token bound to the exact canonical intent authorizes exactly one call.
            var intent = adapter.CanonicalizeRun(Request("python", ["-c", "print(1)"]));
            var token = approvals.Issue(
                "request-2",
                "session-1",
                "terminal.run",
                "1.0.0",
                intent.IntentHash,
                TimeSpan.FromSeconds(60));
            var result = await adapter.RunAsync(
                MakeGrantWithScopes(ToolScope.TerminalExecute),
                Request(
                    "python",
                    ["-c", "print(1)"],
                    approvalToken: token,
                    requestId: "request-2",
                    sessionId: "session-1"),
                confineWorkspace: true,
                CancellationToken.None);
            Assert.Equal("succeeded", result.Status);

            var replayed = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                adapter.RunAsync(
                    MakeGrantWithScopes(ToolScope.TerminalExecute),
                    Request(
                        "python",
                        ["-c", "print(1)"],
                        approvalToken: token,
                        requestId: "request-2",
                        sessionId: "session-1"),
                    confineWorkspace: true,
                    CancellationToken.None));
            Assert.Equal("terminal_approval_invalid", replayed.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Timeout_over_the_hard_maximum_is_denied_before_any_process_work()
    {
        var root = CreateTemporaryRoot("ru-terminal-hard-max-");
        try
        {
            var adapter = CreateAdapter(root);
            var error = Assert.Throws<UnauthorizedAccessException>(() =>
            {
                _ = adapter.CanonicalizeRun(Request("rg", ["--files"], timeoutMs: 1_800_001));
            });
            Assert.Equal("timeout_exceeds_hard_max", error.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("argument_nul_rejected", "bad\0arg")]
    [InlineData("interactive_flag_rejected", "--interactive")]
    [InlineData("background_daemon_rejected", "--daemon")]
    public void Structural_argument_violations_are_denied_before_execution(string expectedCode, string argument)
    {
        var root = CreateTemporaryRoot("ru-terminal-structural-");
        try
        {
            var adapter = CreateAdapter(root);
            var error = Assert.Throws<UnauthorizedAccessException>(() =>
            {
                _ = adapter.CanonicalizeRun(Request("rg", [argument]));
            });
            Assert.Equal(expectedCode, error.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Execution_time_reresolution_rejects_when_the_workspace_directory_disappears()
    {
        var parent = CreateTemporaryRoot("ru-terminal-reresolve-");
        var workspaceRoot = Path.Combine(parent, "workspace");
        Directory.CreateDirectory(workspaceRoot);
        try
        {
            var approvals = new ApprovalTokenService();
            var adapter = new TerminalAdapter(
                new WorkspacePathGuard(workspaceRoot),
                approvals);
            var request = Request("git", ["status"], workingDirectory: ".");
            var intent = adapter.CanonicalizeRun(request);
            var token = approvals.Issue(
                "request-reresolve",
                "session-1",
                "terminal.run",
                "1.0.0",
                intent.IntentHash,
                TimeSpan.FromSeconds(60));

            Directory.Delete(workspaceRoot, recursive: true);

            var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                adapter.RunAsync(
                    MakeGrantWithScopes(ToolScope.TerminalExecute),
                    Request(
                        "git",
                        ["status"],
                        approvalToken: token,
                        requestId: "request-reresolve",
                        sessionId: "session-1"),
                    confineWorkspace: true,
                    CancellationToken.None));
            Assert.Equal("working_directory_invalid", error.Message);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task NetworkCommandRequiresApprovalTokenWithoutNetworkScope()
    {
        var root = CreateTemporaryRoot("ru-terminal-network-granted-");
        try
        {
            var approvals = new ApprovalTokenService();
            var adapter = new TerminalAdapter(
                new WorkspacePathGuard(root),
                approvals);
            var intent = adapter.CanonicalizeRun(Request("dotnet", ["--version"], network: true));
            var token = approvals.Issue(
                "request-network-1",
                "session-network-1",
                "terminal.run",
                "1.0.0",
                intent.IntentHash,
                TimeSpan.FromSeconds(60));
            var result = await adapter.RunAsync(
                MakeGrantWithScopes(ToolScope.TerminalExecute),
                Request("dotnet", ["--version"], network: true, approvalToken: token, requestId: "request-network-1", sessionId: "session-network-1"),
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
    public async Task NetworkCommandStillRejectedWithoutApproval()
    {
        var root = CreateTemporaryRoot("ru-terminal-network-denied-");
        try
        {
            var adapter = CreateAdapter(root);
            var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                adapter.RunAsync(
                    MakeGrantWithScopes(ToolScope.TerminalExecute),
                    Request("dotnet", ["--version"], network: true),
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
        File.WriteAllText(Path.Combine(outsideDirectory, "outside-marker.txt"), "x");
        try
        {
            var adapter = CreateAdapter(workspaceRoot);
            var result = await adapter.RunAsync(
                MakeGrantWithScopes(ToolScope.TerminalExecute),
                Request(
                    "rg",
                    ["--files"],
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
    public async Task Unconfined_execution_keeps_hard_timeout()
    {
        var root = CreateTemporaryRoot("ru-terminal-timeout-");
        try
        {
            var adapter = CreateAdapter(root);
            var result = await adapter.RunAsync(
                MakeGrantWithScopes(ToolScope.TerminalExecute),
                new TerminalRunRequest(
                    "powershell",
                    ".",
                    "powershell",
                    ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 10"],
                    100,
                    ApprovalToken: string.Empty,
                    RequestId: "timeout-request",
                    SessionId: "timeout-session",
                    Approved: true),
                confineWorkspace: false,
                CancellationToken.None);
            Assert.Equal("timed_out", result.Status);
            Assert.True(result.DurationMs < 5_000, result.DurationMs.ToString());
        }
        finally
        {
            DeleteDirectoryEventually(root);
        }
    }

    [Fact]
    public async Task Unconfined_execution_keeps_output_cap()
    {
        var root = CreateTemporaryRoot("ru-terminal-output-cap-");
        try
        {
            var adapter = new TerminalAdapter(
                new WorkspacePathGuard(root),
                runner: new StreamingProcessRunner(outputLimitBytes: 32, headCapacityChars: 64, tailCapacityChars: 64));
            var result = await adapter.RunAsync(
                MakeGrantWithScopes(ToolScope.TerminalExecute),
                new TerminalRunRequest(
                    "powershell",
                    ".",
                    "powershell",
                    ["-NoProfile", "-NonInteractive", "-Command", "Write-Output ([string]::new('x', 5000))"],
                    30_000,
                    ApprovalToken: string.Empty,
                    RequestId: "output-request",
                    SessionId: "output-session",
                    Approved: true),
                confineWorkspace: false,
                CancellationToken.None);
            Assert.Equal("succeeded", result.Status);
            Assert.InRange(result.StdoutBytes, 1, 32);
            Assert.True(result.StdoutDroppedBytes > 0);
        }
        finally
        {
            DeleteDirectoryEventually(root);
        }
    }

    [Fact]
    public async Task Unconfined_execution_keeps_cancellation()
    {
        var root = CreateTemporaryRoot("ru-terminal-cancel-");
        try
        {
            var adapter = CreateAdapter(root);
            using var cancellation = new CancellationTokenSource(200);
            var result = await adapter.RunAsync(
                MakeGrantWithScopes(ToolScope.TerminalExecute),
                new TerminalRunRequest(
                    "powershell",
                    ".",
                    "powershell",
                    ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"],
                    120_000,
                    ApprovalToken: string.Empty,
                    RequestId: "cancel-request",
                    SessionId: "cancel-session",
                    Approved: true),
                confineWorkspace: false,
                cancellation.Token);
            Assert.Equal("cancelled", result.Status);
            Assert.True(result.DurationMs < 5_000, result.DurationMs.ToString());
        }
        finally
        {
            DeleteDirectoryEventually(root);
        }
    }

    [Fact]
    public async Task Unconfined_cancellation_kills_real_child_process_tree()
    {
        var root = CreateTemporaryRoot("ru-terminal-process-tree-");
        var childScript = Path.Combine(root, "child.ps1");
        var marker = Path.Combine(root, "child.pid");
        var escapedChild = childScript.Replace("'", "''", StringComparison.Ordinal);
        var escapedMarker = marker.Replace("'", "''", StringComparison.Ordinal);
        File.WriteAllText(
            childScript,
            $"[System.IO.File]::WriteAllText('{escapedMarker}', [string]$PID); Start-Sleep -Seconds 60");
        using var cancellation = new CancellationTokenSource();
        Task<StreamingProcessResult>? executionTask = null;
        var childProcessId = 0;
        try
        {
            var adapter = CreateAdapter(root);
            var parentScript =
                "$child = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-NonInteractive','-File','"
                + escapedChild
                + "') -PassThru; Wait-Process -Id $child.Id";
            executionTask = adapter.RunAsync(
                MakeGrantWithScopes(ToolScope.TerminalExecute),
                new TerminalRunRequest(
                    "powershell",
                    ".",
                    "powershell",
                    ["-NoProfile", "-NonInteractive", "-Command", parentScript],
                    120_000,
                    ApprovalToken: string.Empty,
                    RequestId: "process-tree-request",
                    SessionId: "process-tree-session",
                    Approved: true),
                confineWorkspace: false,
                cancellation.Token);
            childProcessId = await WaitForProcessIdAsync(marker, TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            var result = await executionTask;
            Assert.Equal("cancelled", result.Status);
            Assert.True(
                await WaitForProcessExitAsync(childProcessId, TimeSpan.FromSeconds(5)),
                $"child process {childProcessId} survived cancellation");
        }
        finally
        {
            cancellation.Cancel();
            Exception? cleanupError = null;
            if (executionTask is not null)
            {
                try
                {
                    await executionTask;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
                catch (Exception error)
                {
                    cleanupError = error;
                }
            }

            if (childProcessId > 0)
            {
                try
                {
                    await EnsureProcessStoppedAsync(childProcessId);
                }
                catch (Exception error)
                {
                    cleanupError ??= error;
                }
            }

            try
            {
                DeleteDirectoryEventually(root);
            }
            catch (Exception error)
            {
                cleanupError ??= error;
            }

            if (cleanupError is not null)
            {
                ExceptionDispatchInfo.Capture(cleanupError).Throw();
            }
        }
    }

    [Fact]
    public async Task Unrestricted_confinement_allows_protected_system_working_directory()
    {
        var root = CreateTemporaryRoot("ru-terminal-protected-cwd-");
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.False(string.IsNullOrWhiteSpace(windowsDirectory));
        try
        {
            var adapter = CreateAdapter(root);
            var result = await adapter.RunAsync(
                MakeGrantWithScopes(ToolScope.TerminalExecute),
                Request("dotnet", ["--version"], workingDirectory: windowsDirectory),
                confineWorkspace: false,
                CancellationToken.None);
            Assert.Equal("succeeded", result.Status);
            Assert.True(
                string.Equals(Path.GetFullPath(windowsDirectory), result.WorkingDirectory, StringComparison.OrdinalIgnoreCase),
                result.WorkingDirectory);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Unrestricted_confinement_allows_junctions_into_protected_directories()
    {
        var parent = CreateTemporaryRoot("ru-terminal-junction-");
        var workspaceRoot = Path.Combine(parent, "workspace");
        Directory.CreateDirectory(workspaceRoot);
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.False(string.IsNullOrWhiteSpace(windowsDirectory));
        var junction = Path.Combine(workspaceRoot, "windows-link");
        CreateJunction(junction, windowsDirectory);
        try
        {
            var adapter = CreateAdapter(workspaceRoot);
            var result = await adapter.RunAsync(
                MakeGrantWithScopes(ToolScope.TerminalExecute),
                Request("dotnet", ["--version"], workingDirectory: "windows-link"),
                confineWorkspace: false,
                CancellationToken.None);
            Assert.Equal("succeeded", result.Status);
        }
        finally
        {
            TryRemoveLink(junction);
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task Unrestricted_confinement_allows_extended_syntax_protected_directory()
    {
        var root = CreateTemporaryRoot("ru-terminal-extended-cwd-");
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.False(string.IsNullOrWhiteSpace(windowsDirectory));
        try
        {
            var adapter = CreateAdapter(root);
            var result = await adapter.RunAsync(
                MakeGrantWithScopes(ToolScope.TerminalExecute),
                Request(
                    "dotnet",
                    ["--version"],
                    workingDirectory: $@"\\?\{windowsDirectory}"),
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
    public async Task Confinement_flag_remains_independent_of_network_scope()
    {
        var root = CreateTemporaryRoot("ru-terminal-independence-");
        try
        {
            var adapter = CreateAdapter(root);
            var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                adapter.RunAsync(
                    MakeGrantWithScopes(ToolScope.TerminalExecute),
                    Request(
                        "rg",
                        ["--files"],
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
            var adapter = new TerminalAdapter(
                new WorkspacePathGuard(root),
                approvals);
            var scriptIntent = adapter.CanonicalizeShell(new TerminalShellRequest(
                "powershell",
                ".",
                "Get-Date",
                1_000,
                string.Empty));
            var staleIntent = adapter.CanonicalizeShell(new TerminalShellRequest(
                "powershell",
                ".",
                "Get-Location",
                1_000,
                string.Empty));
            var token = approvals.Issue(
                "request-1",
                "session-1",
                "terminal.shell",
                "1.0.0",
                scriptIntent.IntentHash,
                TimeSpan.FromSeconds(30));

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                adapter.RunApprovedShellAsync(
                    new TerminalShellRequest(
                        "powershell",
                        ".",
                        "Get-Location",
                        1_000,
                        token,
                        "request-1",                        "session-1"),
                    confineWorkspace: true,
                    CancellationToken.None));

            var result = await adapter.RunApprovedShellAsync(
                new TerminalShellRequest(
                    "powershell",
                    ".",
                    "Get-Date",
                    1_000,
                    token,
                    "request-1",                    "session-1"),
                confineWorkspace: true,
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
                        "request-1",                        "session-1"),
                    confineWorkspace: true,
                    CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Unsupported_shell_runtimes_are_rejected_before_execution()
    {
        var root = CreateTemporaryRoot("ru-terminal-shell-runtime-bad-");
        try
        {
            var adapter = CreateAdapter(root);

            foreach (var shell in new[] { "git-bash", "wsl", "cmd" })
            {
                var error = Assert.Throws<UnauthorizedAccessException>(() =>
                    adapter.CanonicalizeShell(new TerminalShellRequest(shell, ".", "Get-Date", 1_000, string.Empty)));
                Assert.Equal("unsupported_shell_runtime", error.Message);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Shell_approval_bound_to_one_runtime_does_not_authorize_another()
    {
        var root = CreateTemporaryRoot("ru-terminal-shell-runtime-bind-");
        try
        {
            var bin = Path.Combine(root, "bin");
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, "powershell.exe"), "stub");
            File.WriteAllText(Path.Combine(bin, "pwsh.exe"), "stub");
            var approvals = new ApprovalTokenService();
            var adapter = new TerminalAdapter(
                new WorkspacePathGuard(root),
                approvals,
                intentResolver: new CommandIntentResolver(root, [bin]));

            var powershellIntent = adapter.CanonicalizeShell(new TerminalShellRequest("powershell", ".", "Get-Date", 1_000, string.Empty));
            var pwshIntent = adapter.CanonicalizeShell(new TerminalShellRequest("pwsh", ".", "Get-Date", 1_000, string.Empty));
            Assert.NotEqual(powershellIntent.IntentHash, pwshIntent.IntentHash);

            var token = approvals.Issue(
                "request-1",
                "session-1",
                "terminal.shell",
                "1.0.0",
                powershellIntent.IntentHash,
                TimeSpan.FromSeconds(30));

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                adapter.RunApprovedShellAsync(
                    new TerminalShellRequest("pwsh", ".", "Get-Date", 1_000, token, "request-1", "session-1"),
                    confineWorkspace: true,
                    CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Unconfined_shell_still_requires_a_token_and_honors_cwd_denylist()
    {
        var parent = CreateTemporaryRoot("ru-terminal-shell-unconfined-");
        var workspaceRoot = Path.Combine(parent, "workspace");
        var outsideDirectory = Path.Combine(parent, "outside");
        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(outsideDirectory);
        try
        {
            var approvals = new ApprovalTokenService();
            var adapter = new TerminalAdapter(
                new WorkspacePathGuard(workspaceRoot),
                approvals);
            var request = new Func<string, string, string, TerminalShellRequest>(
                (workingDirectory, token, requestId) => new(
                    "powershell",
                    workingDirectory,
                    "Get-Date",
                    30_000,
                    token,
                    requestId,
                    "session-shell-unconfined"));

            var outsideIntent = adapter.CanonicalizeShell(new TerminalShellRequest(
                "powershell",
                Path.Combine("..", "outside"),
                "Get-Date",
                30_000,
                string.Empty));
            var outsideToken = approvals.Issue(
                "request-outside-1",
                "session-shell-unconfined",
                "terminal.shell",
                "1.0.0",
                outsideIntent.IntentHash,
                TimeSpan.FromSeconds(60));

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                adapter.RunApprovedShellAsync(
                    request(
                        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                        outsideToken,
                        "request-outside-1"),
                    confineWorkspace: false,
                    CancellationToken.None));

            var result = await adapter.RunApprovedShellAsync(
                request(
                    Path.Combine("..", "outside"),
                    outsideToken,
                    "request-outside-1"),
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

    private static TerminalAdapter CreateAdapter(string root) => new(
        new WorkspacePathGuard(root));

    private static PolicyGrant MakeGrantWithScopes(params string[] scopes) => new(
        "session-terminal-" + Guid.NewGuid().ToString("N"),
        new HashSet<string>(scopes, StringComparer.Ordinal),
        DateTimeOffset.UtcNow.AddMinutes(30));

    private static TerminalRunRequest Request(
        string executable,
        IReadOnlyList<string> arguments,
        bool network = false,
        string workingDirectory = ".",
        int timeoutMs = 30_000,
        string approvalToken = "",
        string requestId = "",
        string sessionId = "") => new(
        "powershell",
        workingDirectory,
        executable,
        arguments,
        timeoutMs,
        network,
        approvalToken,
        requestId,
        sessionId);

    private static string CreateTemporaryRoot(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void CreateJunction(string junction, string target)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = $"/c mklink /J \"{junction}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        Assert.True(process.WaitForExit(15_000));
        Assert.Equal(0, process.ExitCode);
    }

    private static void TryRemoveLink(string link)
    {
        try
        {
            new DirectoryInfo(link).Delete();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void ManagedCommitService_TryExtractCommitMessage_ParsesFormats()
    {
        Assert.Equal("feat: test", ManagedCommitService.TryExtractCommitMessage(new[] { "commit", "-m", "feat: test" }));
        Assert.Equal("fix: bug", ManagedCommitService.TryExtractCommitMessage(new[] { "commit", "-mfix: bug" }));
        Assert.Equal("docs: update", ManagedCommitService.TryExtractCommitMessage(new[] { "commit", "--message", "docs: update" }));
        Assert.Equal("chore: refactor", ManagedCommitService.TryExtractCommitMessage(new[] { "commit", "--message=chore: refactor" }));
        Assert.Null(ManagedCommitService.TryExtractCommitMessage(new[] { "commit" }));
        Assert.Null(ManagedCommitService.TryExtractCommitMessage(new[] { "status" }));
    }

    [Fact]
    public async Task TerminalAdapter_ManagedCommit_Refuses_MissingMessage()
    {
        var root = CreateTemporaryRoot("ru-terminal-git-");
        try
        {
            var approvals = new ApprovalTokenService();
            var adapter = new TerminalAdapter(
                new WorkspacePathGuard(root),
                approvals);

            var intent = adapter.CanonicalizeRun(new TerminalRunRequest(
                "powershell", ".", "git", new[] { "commit" }, 10_000, false, "token", "req-1", "sess-1", false));

            var token = approvals.Issue("req-1", "sess-1", "terminal.run", "1.0.0", intent.IntentHash, TimeSpan.FromSeconds(30));

            var result = await adapter.RunAsync(
                MakeGrantWithScopes(ToolScope.TerminalExecute),
                new TerminalRunRequest("powershell", ".", "git", new[] { "commit" }, 10_000, false, token, "req-1", "sess-1", false),
                confineWorkspace: true,
                CancellationToken.None);

            Assert.Equal("failed", result.Status);
            Assert.Contains("commit message must be provided", result.StderrTail);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TerminalAdapter_ManagedCommit_Refuses_WhenRealIndexHasStagedChanges()
    {
        var root = CreateTemporaryRoot("ru-terminal-staged-");
        try
        {
            var psiInit = new ProcessStartInfo("git", "init") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true };
            using var pInit = Process.Start(psiInit)!;
            pInit.WaitForExit();

            Process.Start(new ProcessStartInfo("git", "config user.name test") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true })!.WaitForExit();
            Process.Start(new ProcessStartInfo("git", "config user.email test@example.com") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true })!.WaitForExit();

            File.WriteAllText(Path.Combine(root, "init.txt"), "hello");
            Process.Start(new ProcessStartInfo("git", "add init.txt") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true })!.WaitForExit();
            Process.Start(new ProcessStartInfo("git", "commit -m \"init\"") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true })!.WaitForExit();

            File.WriteAllText(Path.Combine(root, "user_staged.txt"), "user work");
            Process.Start(new ProcessStartInfo("git", "add user_staged.txt") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true })!.WaitForExit();

            var approvals = new ApprovalTokenService();
            var adapter = new TerminalAdapter(
                new WorkspacePathGuard(root),
                approvals);

            var intent = adapter.CanonicalizeRun(new TerminalRunRequest(
                "powershell", ".", "git", new[] { "commit", "-m", "agent commit" }, 10_000, false, "token", "req-1", "sess-1", false));

            var token = approvals.Issue("req-1", "sess-1", "terminal.run", "1.0.0", intent.IntentHash, TimeSpan.FromSeconds(30));

            var result = await adapter.RunAsync(
                MakeGrantWithScopes(ToolScope.TerminalExecute),
                new TerminalRunRequest("powershell", ".", "git", new[] { "commit", "-m", "agent commit" }, 10_000, false, token, "req-1", "sess-1", false),
                confineWorkspace: true,
                CancellationToken.None);

            Assert.Equal("failed", result.Status);
            Assert.Contains("index_not_empty", result.StderrTail);
        }
        finally
        {
            ForceDeleteDirectory(root);
        }
    }

    private static async Task<int> WaitForProcessIdAsync(string markerPath, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        Exception? readError = null;
        while (stopwatch.Elapsed < timeout)
        {
            if (File.Exists(markerPath))
            {
                try
                {
                    if (int.TryParse(File.ReadAllText(markerPath).Trim(), out var processId))
                    {
                        return processId;
                    }
                }
                catch (IOException error)
                {
                    readError = error;
                }
            }

            await Task.Delay(100);
        }

        throw new IOException("child_process_pid_marker_timeout", readError);
    }

    private static async Task<bool> WaitForProcessExitAsync(int processId, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    private static async Task EnsureProcessStoppedAsync(int processId)
    {
        if (await WaitForProcessExitAsync(processId, TimeSpan.FromSeconds(5)))
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Assert.True(process.HasExited);
        }
        catch (ArgumentException)
        {
        }
    }

    private static void DeleteDirectoryEventually(string path)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 20 && Directory.Exists(path); attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                lastError = error;
                Thread.Sleep(100);
            }
        }

        if (Directory.Exists(path))
        {
            throw new IOException("temporary_directory_cleanup_failed", lastError);
        }
    }

    private static void ForceDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
