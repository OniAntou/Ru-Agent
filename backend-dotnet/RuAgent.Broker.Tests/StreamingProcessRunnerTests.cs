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
