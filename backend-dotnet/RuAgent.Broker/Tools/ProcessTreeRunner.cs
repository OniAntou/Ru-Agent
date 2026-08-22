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
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            status = cancellationToken.IsCancellationRequested ? "cancelled" : "timed_out";
            Kill(process);
        }

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
}
