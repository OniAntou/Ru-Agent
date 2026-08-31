using System.Diagnostics;
using System.ComponentModel;
using System.Text;

namespace RuAgent.Broker.Tools;

public sealed record ProcessRunSpec(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    int TimeoutMs,
    int OutputLimitBytes);

public sealed record ProcessRunResult(
    string Status,
    int? ExitCode,
    int DurationMs,
    string Stdout,
    string Stderr,
    bool OutputTruncated,
    string WorkingDirectory);

public sealed class ProcessTreeRunner
{
    public async Task<ProcessRunResult> RunAsync(
        ProcessRunSpec spec,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.TimeoutMs is < 1 or > 300_000 || spec.OutputLimitBytes is < 1 or > 4 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(spec));
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var process = new Process
        {
            StartInfo = BuildStartInfo(spec),
            EnableRaisingEvents = true,
        };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("process_start_failed");
            }
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            return new ProcessRunResult(
                "failed",
                null,
                (int)stopwatch.Elapsed.TotalMilliseconds,
                string.Empty,
                "process_start_failed",
                false,
                spec.WorkingDirectory);
        }

        var stdoutTask = ReadBoundedAsync(process.StandardOutput, spec.OutputLimitBytes);
        var stderrTask = ReadBoundedAsync(process.StandardError, spec.OutputLimitBytes);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(spec.TimeoutMs);
        var status = "succeeded";
        // Interactive CLI detection: if the command is known to wait for stdin and produces no output quickly, abort early
        var interactiveCheck = IsInteractiveCommand(spec.Executable, spec.Arguments);
        try
        {
            if (interactiveCheck)
            {
                var early = Task.WhenAny(process.WaitForExitAsync(timeout.Token), Task.Delay(500, timeout.Token));
                var completed = await early.ConfigureAwait(false);
                if (completed is Task delayTask && delayTask == await Task.WhenAny(Task.Delay(500, timeout.Token)).ConfigureAwait(false))
                {
                    // After 500ms, if process still running and no output, treat as interactive
                    if (!process.HasExited && stdoutTask.IsCompleted && stderrTask.IsCompleted)
                    {
                        // Check if tasks completed with no output and process still alive -> likely waiting for input
                        var stdoutResult = stdoutTask.IsCompleted ? await stdoutTask.ConfigureAwait(false) : new BoundedOutput(string.Empty, false);
                        var stderrResult = stderrTask.IsCompleted ? await stderrTask.ConfigureAwait(false) : new BoundedOutput(string.Empty, false);
                        if (string.IsNullOrWhiteSpace(stdoutResult.Text) && string.IsNullOrWhiteSpace(stderrResult.Text))
                        {
                            status = "interactive_cli_detected";
                            Kill(process);
                            // Wait a bit for process to exit after kill
                            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                            goto afterWait;
                        }
                    }
                }
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            else
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            status = cancellationToken.IsCancellationRequested ? "cancelled" : status == "interactive_cli_detected" ? "interactive_cli_detected" : "timed_out";
            Kill(process);
        }
        afterWait:;

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (status == "succeeded" && process.ExitCode != 0)
        {
            status = "failed";
        }

        return new ProcessRunResult(
            status,
            process.HasExited ? process.ExitCode : null,
            (int)stopwatch.Elapsed.TotalMilliseconds,
            stdout.Text,
            stderr.Text,
            stdout.Truncated || stderr.Truncated,
            spec.WorkingDirectory);
    }

    private static ProcessStartInfo BuildStartInfo(ProcessRunSpec spec)
    {
        var info = new ProcessStartInfo
        {
            FileName = spec.Executable,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in spec.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment.Clear();
        foreach (var pair in spec.Environment)
        {
            info.Environment[pair.Key] = pair.Value;
        }

        return info;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static async Task<BoundedOutput> ReadBoundedAsync(StreamReader reader, int limit)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        var bytes = 0;
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            var text = new string(buffer, 0, count);
            var textBytes = Encoding.UTF8.GetByteCount(text);
            if (bytes + textBytes > limit)
            {
                var remaining = Math.Max(0, limit - bytes);
                var clipped = Encoding.UTF8.GetBytes(text);
                builder.Append(Encoding.UTF8.GetString(clipped, 0, Math.Min(remaining, clipped.Length)));
                truncated = true;
                while (await reader.ReadAsync(buffer).ConfigureAwait(false) > 0)
                {
                    // Drain the pipe after clipping so the child cannot block
                    // on a full redirected stream before timeout cleanup.
                }
                break;
            }

            builder.Append(text);
            bytes += textBytes;
        }

        return new BoundedOutput(builder.ToString(), truncated);
    }

    private sealed record BoundedOutput(string Text, bool Truncated);

    private static bool IsInteractiveCommand(string executable, IReadOnlyList<string> args)
    {
        var exe = Path.GetFileName(executable).ToLowerInvariant();
        // Known interactive shells and repls without a script/file argument
        if (exe is "python" or "python3" or "node" or "cmd" or "powershell" or "pwsh" or "bash" or "sh")
        {
            // If no arguments or only flags that imply interactive, treat as interactive
            if (args.Count == 0) return true;
            // Check for -i flag
            foreach (var a in args) if (a == "-i" || a == "--interactive") return true;
            // If the first arg is not a file (no .py, .js, .ps1, etc.), likely interactive
            if (args.Count > 0 && !args[0].Contains('.') && !args[0].StartsWith("-")) return false; // might be a script, not interactive
            // If executable is python/node with no script, it's interactive
            if (exe.StartsWith("python") && args.Count == 0) return true;
            if (exe == "node" && args.Count == 0) return true;
        }
        // Also check for commands that explicitly wait for stdin
        foreach (var a in args) if (a.Contains("interactive") || a == "-") return true;
        return false;
    }
}
