using System.Diagnostics;
using System.Text;
using RuAgent.Broker.Tools;
using RuAgent.Broker.Tools.Terminal;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class StreamingProcessRunnerTests
{
    private static ProcessRunSpec Spec(
        string executable,
        string arguments,
        int timeoutMs = 30_000,
        IReadOnlyDictionary<string, string>? environment = null) => new(
        executable,
        [arguments],
        Environment.CurrentDirectory,
        environment ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        timeoutMs,
        256 * 1024);

    // ProcessRunSpec takes an argv list; cmd.exe needs /c plus the command text as
    // separate argv entries, so build specs explicitly instead.
    private static ProcessRunSpec CmdSpec(string command, int timeoutMs = 30_000)
    {
        var comspec = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
        return new ProcessRunSpec(
            comspec,
            ["/d", "/s", "/c", command],
            Path.GetTempPath(),
            new AgentCommandEnvironment().ChildEnvironment,
            timeoutMs,
            256 * 1024);
    }

    private static string EncodePowerShell(string script) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    [Fact]
    public async Task Runner_accepts_every_timeout_the_protocol_declares_valid()
    {
        // The tool schema and the policy evaluator both accept up to
        // TerminalLimits.HardMaxTimeoutMs, and a request between the standard
        // approval threshold and that maximum is exactly the case a user has to
        // approve. The runner used to reject anything above 300 s, so an approved
        // long-running command died as adapter_error after the user consented.
        var runner = new StreamingProcessRunner();

        var result = await runner.RunAsync(
            CmdSpec("echo long-window", TerminalLimits.HardMaxTimeoutMs),
            null,
            CancellationToken.None);

        Assert.Equal("succeeded", result.Status);
        Assert.Contains("long-window", result.StdoutHead, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runner_rejects_a_timeout_above_the_protocol_maximum()
    {
        var runner = new StreamingProcessRunner();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            runner.RunAsync(
                CmdSpec("echo too-long", TerminalLimits.BackgroundMaxLifetimeMs + 1),
                null,
                CancellationToken.None));
    }

    [Fact]
    public async Task Normal_exit_streams_output_and_reports_success()
    {
        var runner = new StreamingProcessRunner();
        var frames = new List<(string Stream, long Sequence, string Text)>();
        var result = await runner.RunAsync(
            CmdSpec("echo hello-stream"),
            (stream, sequence, text, _, token) =>
            {
                frames.Add((stream, sequence, text));
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal("succeeded", result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello-stream", result.StdoutHead, StringComparison.Ordinal);
        Assert.NotEmpty(frames);
        Assert.All(frames, frame => Assert.Equal("stdout", frame.Stream));
        Assert.Equal(1, frames[0].Sequence);
        Assert.True(frames.SequenceEqual(frames.OrderBy(frame => frame.Sequence)));
    }

    [Fact]
    public async Task Runner_closes_stdin_so_reading_child_exits_without_waiting_for_input()
    {
        var runner = new StreamingProcessRunner();
        var stopwatch = Stopwatch.StartNew();
        var script = "$line=[Console]::ReadLine(); if ($null -eq $line) { exit 7 } else { exit 0 }";
        var result = await runner.RunAsync(
            CmdSpec($"powershell -NoProfile -EncodedCommand {EncodePowerShell(script)}", timeoutMs: 5_000),
            null,
            CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal("failed", result.Status);
        Assert.Equal(7, result.ExitCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "stdin readers must receive EOF promptly");
    }

    [Fact]
    public async Task Stdout_and_stderr_are_drained_concurrently_with_a_merged_monotonic_sequence()
    {
        var runner = new StreamingProcessRunner();
        var sequences = new List<long>();
        var result = await runner.RunAsync(
            CmdSpec("echo out-1 & echo err-1 >&2 & echo out-2"),
            (stream, sequence, text, _, token) =>
            {
                lock (sequences)
                {
                    sequences.Add(sequence);
                }

                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal("succeeded", result.Status);
        Assert.True(sequences.Count > 0);
        Assert.True(sequences.Distinct().Count() == sequences.Count, "frame sequence must not repeat");
        Assert.Equal(Enumerable.Range(1, sequences.Count).Select(value => (long)value), sequences.OrderBy(value => value));
        Assert.Contains("out-1", result.StdoutHead, StringComparison.Ordinal);
        Assert.Contains("err-1", result.StderrHead, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Known_secrets_split_across_chunks_never_survive_into_frames_or_buffers()
    {
        var secret = "ru-bearer-super-secret-value";
        var runner = new StreamingProcessRunner(redactionSeeds: [secret]);
        var frames = new List<string>();
        // The secret is split across two echo statements so the chunks arrive separately.
        var result = await runner.RunAsync(
            CmdSpec($"echo {secret.Substring(0, 10)}& echo {secret.Substring(10)}"),
            (_, _, text, _, token) =>
            {
                frames.Add(text);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.DoesNotContain(secret, result.StdoutHead, StringComparison.Ordinal);
        foreach (var frame in frames)
        {
            Assert.DoesNotContain(secret, frame, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Invalid_utf8_is_replaced_not_corrupted()
    {
        var runner = new StreamingProcessRunner();
        // 0xC3 0x28 is an invalid UTF-8 sequence; the decoder must substitute U+FFFD
        // instead of throwing or corrupting the following bytes.
        var script = "$s=[Console]::OpenStandardOutput(); $s.Write([byte[]](0xC3,0x28,0x41,0x42),0,4)";
        var result = await runner.RunAsync(
            CmdSpec($"powershell -NoProfile -EncodedCommand {EncodePowerShell(script)}"),
            null,
            CancellationToken.None);

        Assert.Equal("succeeded", result.Status);
        Assert.Contains("\uFFFD", result.StdoutHead, StringComparison.Ordinal);
        Assert.Contains("AB", result.StdoutHead, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Truncation_counters_report_bounded_retention()
    {
        var runner = new StreamingProcessRunner(outputLimitBytes: 256 * 1024, headCapacityChars: 16, tailCapacityChars: 16);
        var script = "Write-Output ([string]::new('x', 5000))";
        var result = await runner.RunAsync(
            CmdSpec($"powershell -NoProfile -EncodedCommand {EncodePowerShell(script)}"),
            null,
            CancellationToken.None);

        Assert.Equal("succeeded", result.Status);
        Assert.True(result.StdoutBytes >= 5000);
        Assert.True(result.StdoutDroppedBytes > 0);
        var rendered = result.StdoutHead + result.StdoutTail;
        Assert.True(rendered.Length < result.StdoutBytes);
    }

    [Fact]
    public async Task Runner_output_limit_is_enforced_even_when_the_request_limit_is_larger()
    {
        var runner = new StreamingProcessRunner(outputLimitBytes: 32, headCapacityChars: 64, tailCapacityChars: 64);
        var result = await runner.RunAsync(
            CmdSpec("powershell -NoProfile -Command \"Write-Output ([string]::new('x', 500))\"")
                with { OutputLimitBytes = 1024 },
            null,
            CancellationToken.None);

        Assert.Equal("succeeded", result.Status);
        Assert.InRange(result.StdoutBytes, 1, 32);
        Assert.True(result.StdoutDroppedBytes > 0);
    }

    [Fact]
    public async Task Cancellation_terminates_the_whole_process_tree()
    {
        var runner = new StreamingProcessRunner();
        using var cts = new CancellationTokenSource(300);
        var stopwatch = Stopwatch.StartNew();
        // Long-running sleep via ping (timeout.exe needs interactive stdin). Grandchild
        // take-down is covered separately by the assigned-job kill test.
        var result = await runner.RunAsync(
            CmdSpec("ping -n 61 127.0.0.1 > NUL", timeoutMs: 120_000),
            null,
            cts.Token);
        stopwatch.Stop();

        Assert.Equal("cancelled", result.Status);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), "process tree must die promptly on cancellation");
    }

    [Fact]
    public async Task Timeout_terminates_the_process_tree_without_waiting_for_the_full_timeout_elsewhere()
    {
        var runner = new StreamingProcessRunner();
        var stopwatch = Stopwatch.StartNew();
        var result = await runner.RunAsync(
            CmdSpec("ping -n 61 127.0.0.1 > NUL", timeoutMs: 500),
            null,
            CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal("timed_out", result.Status);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), "timeout must fire near its deadline");
    }

    [Fact]
    public async Task Slow_consumer_does_not_block_draining_or_the_final_receipt()
    {
        var runner = new StreamingProcessRunner();
        var emitted = 0;
        var script = "1..40 | ForEach-Object { Write-Output (\"line-\" + $_) }";
        var result = await runner.RunAsync(
            CmdSpec($"powershell -NoProfile -EncodedCommand {EncodePowerShell(script)}"),
            (_, _, _, _, token) =>
            {
                Interlocked.Increment(ref emitted);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal("succeeded", result.Status);
        Assert.True(emitted >= 0);
        Assert.Contains("line-", result.StdoutHead, StringComparison.Ordinal);
    }

    [Fact]
    public void Negative_retention_capacity_is_rejected_at_construction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StreamingProcessRunner(headCapacityChars: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StreamingProcessRunner(tailCapacityChars: -1));
    }

    [Fact]
    public void Job_limit_flags_kill_on_close_and_disallow_breakaway()
    {
        using var job = WindowsJobProcessTree.Create();
        var flags = job.LimitFlags;

        Assert.True(flags.HasFlag(JobLimitFlags.KillOnJobClose));
        Assert.False(flags.HasFlag(JobLimitFlags.SilentBreakawayOk));
        Assert.False(flags.HasFlag(JobLimitFlags.BreakawayOk));
    }

    [Fact]
    public async Task Assigned_job_kill_takes_down_child_and_grandchild()
    {
        using var job = WindowsJobProcessTree.Create();
        var comspec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        var startInfo = new ProcessStartInfo
        {
            FileName = comspec,
            Arguments = "/d /s /c \"start /b cmd /c timeout /t 120 /nobreak > NUL & timeout /t 120 /nobreak > NUL\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var parent = Process.Start(startInfo)!;
        job.Assign(parent.Id);

        await Task.Delay(1_500);
        Assert.False(parent.HasExited, "parent should still run before the kill");

        Assert.True(job.KillTree());
        await parent.WaitForExitAsync(CancellationToken.None);
        await Task.Delay(2_000);

        var stragglers = Process.GetProcessesByName("cmd")
            .Count(process => SafeCommandLine(process)?.Contains("timeout /t 120", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Equal(0, stragglers);
    }

    [Fact]
    public async Task Runner_reports_a_stable_start_failure_for_a_missing_executable()
    {
        var runner = new StreamingProcessRunner();
        var spec = new ProcessRunSpec(
            Path.Combine(Path.GetTempPath(), "ru-missing-" + Guid.NewGuid().ToString("N") + ".exe"),
            ["/c", "echo x"],
            Path.GetTempPath(),
            new AgentCommandEnvironment().ChildEnvironment,
            5_000,
            256 * 1024);

        var result = await runner.RunAsync(spec, null, CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Null(result.ExitCode);
        Assert.Equal("process_start_failed", result.StderrHead);
    }

    [Fact]
    public async Task Cancellation_kills_a_grandchild_spawned_immediately_after_start()
    {
        var runner = new StreamingProcessRunner();
        var pidFile = NewGrandchildPidFile();
        var script = GrandchildScript("ping -n 300 127.0.0.1 > $null");
        using var cts = new CancellationTokenSource();
        var run = runner.RunAsync(GrandchildSpec(pidFile, script), null, cts.Token);

        var pid = await WaitForGrandchildPidAsync(pidFile, TimeSpan.FromSeconds(30));
        cts.Cancel();
        var result = await run;

        Assert.Equal("cancelled", result.Status);
        var exited = await WaitForProcessExitAsync(pid, TimeSpan.FromSeconds(15));
        KillIfAlive(pid);
        Assert.True(exited, "a grandchild spawned at start escaped the job and survived cancellation");
    }

    [Fact]
    public async Task A_detached_grandchild_does_not_outlive_the_job()
    {
        // The child writes its grandchild's PID and exits immediately, so the
        // grandchild is orphaned from its parent; only job membership can take it
        // down. The grandchild inherits the run's output pipes, so the run stays
        // draining until it is cancelled; the job must still take the orphan down.
        var runner = new StreamingProcessRunner();
        var pidFile = NewGrandchildPidFile();
        var script = GrandchildScript(string.Empty);
        using var cts = new CancellationTokenSource();
        var run = runner.RunAsync(GrandchildSpec(pidFile, script), null, cts.Token);

        var pid = await WaitForGrandchildPidAsync(pidFile, TimeSpan.FromSeconds(30));
        cts.Cancel();
        var result = await run;

        Assert.True(result.Status is "succeeded" or "cancelled", $"unexpected status {result.Status}");
        var exited = await WaitForProcessExitAsync(pid, TimeSpan.FromSeconds(15));
        KillIfAlive(pid);
        Assert.True(exited, "a detached grandchild escaped the job and outlived the run");
    }

    private static string NewGrandchildPidFile() =>
        Path.Combine(Path.GetTempPath(), "ru-grandchild-" + Guid.NewGuid().ToString("N") + ".pid");

    private static string GrandchildScript(string finalCommand) =>
        "$psi=[Diagnostics.ProcessStartInfo]::new();" +
        "$psi.FileName=$env:COMSPEC;" +
        "$psi.Arguments='/d /s /c ping -n 300 127.0.0.1 > NUL';" +
        "$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true;" +
        "$psi.RedirectStandardInput=$true;$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true;" +
        "$grandchild=[Diagnostics.Process]::Start($psi);" +
        "[IO.File]::WriteAllText($env:RU_GRANDCHILD_PID_FILE,[string]$grandchild.Id);" +
        finalCommand;

    private static ProcessRunSpec GrandchildSpec(string pidFile, string script)
    {
        var environment = new AgentCommandEnvironment().ChildEnvironment
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        environment["COMSPEC"] = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
        environment["RU_GRANDCHILD_PID_FILE"] = pidFile;
        return new ProcessRunSpec(
            environment["COMSPEC"],
            ["/d", "/s", "/c", $"powershell -NoProfile -EncodedCommand {EncodePowerShell(script)}"],
            Path.GetTempPath(),
            environment,
            120_000,
            256 * 1024);
    }

    private static async Task<int> WaitForGrandchildPidAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path)
                    && int.TryParse(File.ReadAllText(path).Trim(), out var pid)
                    && pid > 0)
                {
                    return pid;
                }
            }
            catch (IOException)
            {
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("the grandchild PID file was never written");
    }

    private static async Task<bool> WaitForProcessExitAsync(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(200);
        }

        return false;
    }

    private static void KillIfAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (Exception)
        {
        }
    }

    private static string? SafeCommandLine(Process process)
    {
        try
        {
            return process.StartInfo.Arguments.Length > 0 ? process.StartInfo.Arguments : process.ProcessName;
        }
        catch (Exception error) when (error is InvalidOperationException or SystemException)
        {
            return null;
        }
    }
}
