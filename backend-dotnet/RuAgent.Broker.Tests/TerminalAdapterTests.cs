using System.Diagnostics;
using System.Runtime.ExceptionServices;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools;
using RuAgent.Broker.Tools.Git;
using RuAgent.Broker.Tools.Terminal;
using Xunit;
using RuAgent.Protocol;

namespace RuAgent.Broker.Tests;

[Collection(nameof(WindowsProcessCollection))]
public sealed class TerminalAdapterTests
{
    [Fact]
    public void Command_analysis_is_metadata_and_does_not_grant_workspace_authority()
    {
        var root = CreateTemporaryRoot("ru-python-auto-");
        try
        {
            var executable = Path.Combine(root, "python.exe");
            File.WriteAllText(executable, "fixture");
            File.WriteAllText(Path.Combine(root, "edit.py"), "pass");
            var adapter = CreateAdapter(root);
            var request = Request(executable, ["edit.py"]);
            Assert.Equal(TerminalOperationClass.WorkspaceScript, adapter.CanonicalizeRun(request).OperationClass);
            File.Delete(Path.Combine(root, "edit.py"));
            Assert.Equal(TerminalOperationClass.WorkspaceScript, adapter.CanonicalizeRun(request).OperationClass);
            Assert.Equal(TerminalOperationClass.WorkspaceScript,
                adapter.CanonicalizeRun(Request(executable, [Path.Combine(Path.GetTempPath(), "outside.py")])).OperationClass);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

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
    public async Task Authorized_read_only_commands_execute_in_the_workspace()
    {
        var root = CreateTemporaryRoot("ru-terminal-recognized-");
        try
        {
            File.WriteAllText(Path.Combine(root, "seen.txt"), "content");
            var adapter = CreateAdapter(root);
            var result = await adapter.RunAsync(
                MakeGrantWithScopes(ToolScope.TerminalExecute),
                Request("rg", ["--files"]),
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
    public async Task Plain_git_diff_does_not_run_the_repository_configured_external_helper()
    {
        var root = CreateTemporaryRoot("ru-git-configured-diff-");
        try
        {
            if (!await IsGitAvailableAsync())
            {
                return;
            }

            var marker = await PrepareRepositoryWithConfiguredHelperAsync(root, "diff.external");

            var result = await CreateAdapter(root).RunAsync(
                MakeGrantWithScopes(ToolScope.TerminalExecute),
                Request("git", ["diff"]),
                CancellationToken.None);

            Assert.True(
                result.Status == "succeeded",
                $"git diff failed with exit={result.ExitCode}: {result.StderrHead}{result.StderrTail}");
            Assert.False(File.Exists(marker));
        }
        finally
        {
            DeleteReadOnlyTree(root);
        }
    }

    [Fact]
    public async Task Plain_git_status_does_not_run_the_repository_configured_fsmonitor_helper()
    {
        var root = CreateTemporaryRoot("ru-git-configured-fsmonitor-");
        try
        {
            if (!await IsGitAvailableAsync())
            {
                return;
            }

            var marker = await PrepareRepositoryWithConfiguredHelperAsync(root, "core.fsmonitor");
            var result = await CreateAdapter(root).RunAsync(
                MakeGrantWithScopes(ToolScope.TerminalExecute),
                Request("git", ["status", "--short"]),
                CancellationToken.None);

            Assert.True(
                result.Status == "succeeded",
                $"git status failed with exit={result.ExitCode}: {result.StderrHead}{result.StderrTail}");
            Assert.False(File.Exists(marker));
        }
        finally
        {
            DeleteReadOnlyTree(root);
        }
    }

    /// <summary>
    /// The adapter executes only Broker-authorized intents. Recognized safe
    /// classes no longer run on their own: allow/ask is decided by the run's
    /// policy snapshot before dispatch, so an unauthorized request is refused
    /// whatever its operation class.
    /// </summary>
    [Fact]
    public async Task Unauthorized_commands_are_refused_whatever_their_class()
    {
        var root = CreateTemporaryRoot("ru-terminal-unauthorized-");
        try
        {
            var adapter = CreateAdapter(root);
            Assert.Equal(TerminalOperationClass.ReadOnly, adapter.CanonicalizeRun(Request("rg", ["--files"])).OperationClass);

            var readOnly = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                adapter.RunAsync(
                    MakeGrantWithScopes(ToolScope.TerminalExecute),
                    Request("rg", ["--files"], approved: false),
                    CancellationToken.None));
            Assert.Equal("terminal_command_not_allowed", readOnly.Message);

            var unrecognized = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                adapter.RunAsync(
                    MakeGrantWithScopes(ToolScope.TerminalExecute),
                    Request("python", ["-c", "print(1)"], approved: false),
                    CancellationToken.None));
            Assert.Equal("terminal_command_not_allowed", unrecognized.Message);

            var result = await adapter.RunAsync(
                MakeGrantWithScopes(ToolScope.TerminalExecute),
                Request("python", ["-c", "print(1)"]),
                CancellationToken.None);
            Assert.Equal("succeeded", result.Status);
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
            var adapter = new TerminalAdapter(new WorkspacePathGuard(workspaceRoot));
            _ = adapter.CanonicalizeRun(Request("git", ["status"], workingDirectory: "."));

            Directory.Delete(workspaceRoot, recursive: true);

            var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                adapter.RunAsync(
                    MakeGrantWithScopes(ToolScope.TerminalExecute),
                    Request("git", ["status"]),
                    CancellationToken.None));
            Assert.Equal("working_directory_invalid", error.Message);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task Authorized_network_command_runs_without_network_scope()
    {
        var root = CreateTemporaryRoot("ru-terminal-network-granted-");
        try
        {
            var adapter = CreateAdapter(root);
            var result = await adapter.RunAsync(
                MakeGrantWithScopes(ToolScope.TerminalExecute),
                Request("dotnet", ["--version"], network: true),
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
                    Request("dotnet", ["--version"], network: true, approved: false),
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
                    Approved: true),
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
                    Approved: true),
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
                    Approved: true),
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
                    Approved: true),
                cancellation.Token);
            // This is startup of two real PowerShell processes, not cancellation latency.
            childProcessId = await WaitForProcessIdAsync(marker, TimeSpan.FromSeconds(20));
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
                CancellationToken.None);
            Assert.Equal("succeeded", result.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Raw_shell_runs_only_when_authorized()
    {
        var root = CreateTemporaryRoot("ru-terminal-shell-");
        try
        {
            var adapter = CreateAdapter(root);

            var unauthorized = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                adapter.RunApprovedShellAsync(
                    new TerminalShellRequest("powershell", ".", "Get-Date", 10_000),
                    CancellationToken.None));
            Assert.Equal("terminal_shell_approval_invalid", unauthorized.Message);

            var result = await adapter.RunApprovedShellAsync(
                new TerminalShellRequest("powershell", ".", "Get-Date", 10_000, Approved: true),
                CancellationToken.None);
            Assert.Equal("succeeded", result.Status);
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
                    adapter.CanonicalizeShell(new TerminalShellRequest(shell, ".", "Get-Date", 1_000)));
                Assert.Equal("unsupported_shell_runtime", error.Message);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The shell runtime is part of the canonical intent, so an approval bound
    /// to one runtime's intent hash never matches another runtime's call.
    /// </summary>
    [Fact]
    public void Shell_runtime_is_part_of_the_intent_identity()
    {
        var root = CreateTemporaryRoot("ru-terminal-shell-runtime-bind-");
        try
        {
            var bin = Path.Combine(root, "bin");
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, "powershell.exe"), "stub");
            File.WriteAllText(Path.Combine(bin, "pwsh.exe"), "stub");
            var adapter = new TerminalAdapter(
                new WorkspacePathGuard(root),
                intentResolver: new CommandIntentResolver(root, [bin]));

            var powershellIntent = adapter.CanonicalizeShell(new TerminalShellRequest("powershell", ".", "Get-Date", 1_000));
            var pwshIntent = adapter.CanonicalizeShell(new TerminalShellRequest("pwsh", ".", "Get-Date", 1_000));
            Assert.NotEqual(powershellIntent.IntentHash, pwshIntent.IntentHash);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Unconfined_authorized_shell_runs_in_an_outside_working_directory()
    {
        var parent = CreateTemporaryRoot("ru-terminal-shell-unconfined-");
        var workspaceRoot = Path.Combine(parent, "workspace");
        var outsideDirectory = Path.Combine(parent, "outside");
        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(outsideDirectory);
        try
        {
            var adapter = CreateAdapter(workspaceRoot);
            var result = await adapter.RunApprovedShellAsync(
                new TerminalShellRequest("powershell", Path.Combine("..", "outside"), "Get-Date", 30_000, Approved: true),
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
        new WorkspacePathGuard(root),
        intentResolver: new CommandIntentResolver(
            root));

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
        bool approved = true) => new(
        "powershell",
        workingDirectory,
        executable,
        arguments,
        timeoutMs,
        network,
        Approved: approved);

    private static string CreateTemporaryRoot(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<bool> IsGitAvailableAsync()
    {
        try
        {
            await RunGitFixtureAsync(Environment.CurrentDirectory, "--version");
            return true;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task RunGitFixtureAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("git_fixture_start_failed");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git_fixture_failed: {output} {error}");
        }
    }

    private static async Task<string> PrepareRepositoryWithConfiguredHelperAsync(string root, string configKey)
    {
        await RunGitFixtureAsync(root, "init", "--quiet");
        await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "initial\n");
        await RunGitFixtureAsync(root, "add", "tracked.txt");
        await RunGitFixtureAsync(root, "-c", "user.name=Ru Test", "-c", "user.email=ru@example.invalid", "commit", "--quiet", "-m", "fixture");

        var helper = Path.Combine(root, "marker-helper.sh");
        var marker = helper + ".marker";
        await File.WriteAllTextAsync(
            helper,
            "#!/bin/sh\nprintf 'helper executed\\n' > \"$0.marker\"\nexit 0\n",
            new System.Text.UTF8Encoding(false));
        await RunGitFixtureAsync(root, "config", configKey, helper.Replace('\\', '/'));
        await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "changed\n");
        return marker;
    }

    private static void DeleteReadOnlyTree(string root)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Directory.Delete(root, recursive: true);
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
    public async Task Git_commit_runs_as_a_plain_terminal_command_and_keeps_only_the_staged_changes()
    {
        var root = CreateTemporaryRoot("ru-terminal-plain-commit-");
        try
        {
            if (!await IsGitAvailableAsync())
            {
                return;
            }

            await RunGitFixtureAsync(root, "init", "--quiet");
            await RunGitFixtureAsync(root, "config", "user.name", "Ru Test");
            await RunGitFixtureAsync(root, "config", "user.email", "ru@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "initial\n");
            await RunGitFixtureAsync(root, "add", "tracked.txt");
            await RunGitFixtureAsync(root, "commit", "--quiet", "-m", "fixture");
            await File.WriteAllTextAsync(Path.Combine(root, "staged.txt"), "staged\n");
            await RunGitFixtureAsync(root, "add", "staged.txt");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "unstaged\n");

            var adapter = CreateAdapter(root);
            var grant = MakeGrantWithScopes(ToolScope.TerminalExecute);
            var commit = await adapter.RunAsync(
                grant, Request("git", ["commit", "-m", "agent commit"]), CancellationToken.None);
            var show = await adapter.RunAsync(
                grant, Request("git", ["show", "--name-only", "--format=", "HEAD"]), CancellationToken.None);

            Assert.Equal("succeeded", commit.Status);
            Assert.DoesNotContain("managed commit", commit.StdoutHead, StringComparison.Ordinal);
            Assert.Contains("staged.txt", show.StdoutHead, StringComparison.Ordinal);
            Assert.DoesNotContain("tracked.txt", show.StdoutHead, StringComparison.Ordinal);
        }
        finally
        {
            DeleteReadOnlyTree(root);
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
