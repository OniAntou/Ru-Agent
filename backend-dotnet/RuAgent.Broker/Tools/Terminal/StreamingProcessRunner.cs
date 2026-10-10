using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using RuAgent.Protocol;

namespace RuAgent.Broker.Tools.Terminal;

public delegate ValueTask TerminalOutputDeltaHandler(
    string stream,
    long frameSequence,
    string text,
    bool redacted,
    CancellationToken cancellationToken);

/// <summary>
/// The final receipt of one streamed terminal process. Head/tail text is already
/// redacted; byte and drop counters are safe metadata.
/// </summary>
public sealed record StreamingProcessResult(
    string Status,
    int? ExitCode,
    int DurationMs,
    string StdoutHead,
    string StdoutTail,
    string StderrHead,
    string StderrTail,
    long StdoutBytes,
    long StderrBytes,
    long StdoutDroppedBytes,
    long StderrDroppedBytes,
    long FramesEmitted,
    long FramesDropped,
    string WorkingDirectory);

/// <summary>
/// Streams one bounded terminal process: stdout and stderr are drained concurrently
/// into a merged monotonic frame sequence, every frame is redacted against the
/// captured known-secret seeds (including secrets split across chunk boundaries),
/// output retention stays head/tail bounded, and the whole process tree lives in a
/// dedicated Job Object that dies on cancel, timeout, or dispose.
/// </summary>
public sealed class StreamingProcessRunner
{
    private const int DecoderChunkChars = 4096;
    private readonly IReadOnlyList<string> _redactionSeeds;
    private readonly int _outputLimitBytes;
    private readonly int _headCapacityChars;
    private readonly int _tailCapacityChars;

    public StreamingProcessRunner(
        IReadOnlyList<string>? redactionSeeds = null,
        int outputLimitBytes = 256 * 1024,
        int headCapacityChars = 8_000,
        int tailCapacityChars = 8_000)
    {
        _redactionSeeds = (redactionSeeds ?? Array.Empty<string>())
            .Where(seed => !string.IsNullOrEmpty(seed))
            .ToArray();
        _outputLimitBytes = Math.Clamp(outputLimitBytes, 1, RuAgent.Protocol.TerminalLimits.MaxSpoolFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(headCapacityChars);
        ArgumentOutOfRangeException.ThrowIfNegative(tailCapacityChars);
        _headCapacityChars = headCapacityChars;
        _tailCapacityChars = tailCapacityChars;
    }

    public async Task<StreamingProcessResult> RunAsync(
        ProcessRunSpec spec,
        TerminalOutputDeltaHandler? onDelta,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        // The protocol schema accepts up to the background lifetime ceiling, so a
        // runner with a tighter private cap would reject an approved request; the
        // evaluator enforces the tighter foreground hard maximum.
        if (spec.TimeoutMs is < 1 or > RuAgent.Protocol.TerminalLimits.BackgroundMaxLifetimeMs
            || spec.OutputLimitBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(spec));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var outputLimitBytes = Math.Min(spec.OutputLimitBytes, _outputLimitBytes);
        using var job = WindowsJobProcessTree.Create();
        var startInfo = BuildStartInfo(spec);
        var stopwatch = Stopwatch.StartNew();
        SuspendedProcess.StartedProcess? started = null;
        try
        {
            // CREATE_SUSPENDED: the child runs no user code until it is assigned
            // to the job, so a descendant spawned immediately after start cannot
            // escape the job and outlive cancel or timeout.
            started = SuspendedProcess.StartSuspended(startInfo);
            job.Assign(started.Process.Id);
            if (!SuspendedProcess.Resume(started.ThreadHandle))
            {
                throw new InvalidOperationException("process_start_failed");
            }

            started.ThreadHandle.Dispose();
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            // The child never ran, or could not be contained: leave nothing
            // behind and return a stable start failure to the caller.
            DisposeStarted(started);
            return StartFailure(spec, stopwatch.ElapsedMilliseconds, "process_start_failed");
        }

        using var registered = started.Process;
        using var stdoutReader = started.StandardOutput;
        using var stderrReader = started.StandardError;
        var stdoutBuffer = new HeadTailOutputBuffer(_headCapacityChars, _tailCapacityChars);
        var stderrBuffer = new HeadTailOutputBuffer(_headCapacityChars, _tailCapacityChars);
        long framesEmitted = 0;
        long framesDropped = 0;
        long nextSequence = 1;
        object sequenceLock = new();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(spec.TimeoutMs));
        var drainToken = timeoutCts.Token;

        var stdoutTask = DrainAsync(
            stdoutReader,
            "stdout",
            stdoutBuffer,
            outputLimitBytes,
            EmitAsync,
            drainToken);
        var stderrTask = DrainAsync(
            stderrReader,
            "stderr",
            stderrBuffer,
            outputLimitBytes,
            EmitAsync,
            drainToken);

        // Keep draining cancellable after a timeout/cancel so a slow output
        // consumer cannot hold the run open after the process tree is killed.
        var status = "running";
        try
        {
            await registered.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            status = "succeeded";
            if (registered.ExitCode != 0)
            {
                status = "failed";
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            status = cancellationToken.IsCancellationRequested ? "cancelled" : "timed_out";
            // The child was assigned to the job before it could run, so every
            // descendant is in the job and the job kill is the authority. The
            // parentage kill stays as a bounded fallback for the direct child.
            try
            {
                if (!registered.HasExited)
                {
                    registered.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
            }

            job.KillTree();
            try
            {
                await registered.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }
        }

        var droppedByLimit = await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

        stopwatch.Stop();
        return new StreamingProcessResult(
            Status: status,
            ExitCode: registered.HasExited ? registered.ExitCode : null,
            DurationMs: (int)stopwatch.Elapsed.TotalMilliseconds,
            StdoutHead: stdoutBuffer.Head,
            StdoutTail: stdoutBuffer.Tail,
            StderrHead: stderrBuffer.Head,
            StderrTail: stderrBuffer.Tail,
            StdoutBytes: stdoutBuffer.TotalBytes,
            StderrBytes: stderrBuffer.TotalBytes,
            StdoutDroppedBytes: stdoutBuffer.DroppedBytes + droppedByLimit[0],
            StderrDroppedBytes: stderrBuffer.DroppedBytes + droppedByLimit[1],
            FramesEmitted: Interlocked.Read(ref framesEmitted),
            FramesDropped: Interlocked.Read(ref framesDropped),
            WorkingDirectory: spec.WorkingDirectory);

        async ValueTask EmitAsync(string stream, string text, bool redacted, CancellationToken emitToken)
        {
            if (onDelta is null || text.Length == 0)
            {
                return;
            }

            long sequence;
            lock (sequenceLock)
            {
                sequence = nextSequence++;
            }

            if (emitToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref framesDropped);
                return;
            }

            try
            {
                await onDelta(stream, sequence, text, redacted, emitToken).ConfigureAwait(false);
                Interlocked.Increment(ref framesEmitted);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref framesDropped);
            }
            catch (InvalidOperationException)
            {
                Interlocked.Increment(ref framesDropped);
            }
        }
    }

    private static StreamingProcessResult StartFailure(ProcessRunSpec spec, long elapsedMs, string code) => new(
        Status: "failed",
        ExitCode: null,
        DurationMs: (int)elapsedMs,
        StdoutHead: string.Empty,
        StdoutTail: string.Empty,
        StderrHead: code,
        StderrTail: string.Empty,
        StdoutBytes: 0,
        StderrBytes: 0,
        StdoutDroppedBytes: 0,
        StderrDroppedBytes: 0,
        FramesEmitted: 0,
        FramesDropped: 0,
        WorkingDirectory: spec.WorkingDirectory);

    private static void DisposeStarted(SuspendedProcess.StartedProcess? started)
    {
        if (started is null)
        {
            return;
        }

        try
        {
            if (!started.Process.HasExited)
            {
                started.Process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
        }

        started.ThreadHandle.Dispose();
        started.StandardOutput.Dispose();
        started.StandardError.Dispose();
        started.Process.Dispose();
    }

    private static ProcessStartInfo BuildStartInfo(ProcessRunSpec spec)
    {
        var info = new ProcessStartInfo
        {
            FileName = spec.Executable,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
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

    // No git config hardening on this path, deliberately. This runner also
    // carries the Broker's own managed-commit calls, and those compare a
    // `git diff --cached` hash against the preview before committing -- an extra
    // -c that changes diff behaviour breaks that check instead of hardening it.
    // GitCli already pins core.fsmonitor for the reads the Broker performs
    // itself. The terminal path is left alone and the gap is recorded in
    // docs/architecture.md as an accepted limitation.

    private async Task<long> DrainAsync(
        StreamReader reader,
        string streamName,
        HeadTailOutputBuffer buffer,
        int limitBytes,
        Func<string, string, bool, CancellationToken, ValueTask> emit,
        CancellationToken drainToken)
    {
        var redactor = new ChunkRedactor(_redactionSeeds);
        var charBuffer = new char[DecoderChunkChars];
        long acceptedBytes = 0;
        long droppedBytes = 0;

        async ValueTask AppendBoundedAsync(string text, bool redacted)
        {
            if (text.Length == 0)
            {
                return;
            }

            var encodedByteCount = Encoding.UTF8.GetByteCount(text);
            var remaining = Math.Max(0L, limitBytes - acceptedBytes);
            if (remaining == 0)
            {
                droppedBytes += encodedByteCount;
                return;
            }

            if (encodedByteCount <= remaining)
            {
                buffer.Append(text);
                acceptedBytes += encodedByteCount;
                await emit(streamName, text, redacted, drainToken).ConfigureAwait(false);
                return;
            }

            var clipped = TakeUtf8Prefix(text, checked((int)remaining), out var clippedBytes);
            if (clipped.Length > 0)
            {
                buffer.Append(clipped);
                acceptedBytes += clippedBytes;
                await emit(streamName, clipped, redacted, drainToken).ConfigureAwait(false);
            }

            droppedBytes += encodedByteCount - clippedBytes;
        }

        try
        {
            while (true)
            {
                var count = await reader.ReadAsync(charBuffer.AsMemory(), drainToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                var text = new string(charBuffer, 0, count);
                var safeText = redactor.Push(text);
                await AppendBoundedAsync(safeText, safeText != text || redactor.RedactedAnything).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (drainToken.IsCancellationRequested)
        {
            // The process was killed for cancellation/timeout. Keep the output
            // already received and flush any redactor-held suffix below.
        }
        catch (IOException) when (drainToken.IsCancellationRequested)
        {
            // A killed child can close a redirected pipe concurrently with the
            // cancellation token. Treat that as a bounded terminal drain.
        }

        var flush = redactor.Flush();
        await AppendBoundedAsync(flush, redactor.RedactedAnything).ConfigureAwait(false);
        return droppedBytes;
    }

    private static string TakeUtf8Prefix(string text, int maxBytes, out int usedBytes)
    {
        if (maxBytes <= 0 || text.Length == 0)
        {
            usedBytes = 0;
            return string.Empty;
        }

        var chars = 0;
        usedBytes = 0;
        while (chars < text.Length)
        {
            var next = chars + 1;
            if (char.IsHighSurrogate(text[chars])
                && next < text.Length
                && char.IsLowSurrogate(text[next]))
            {
                next++;
            }

            var candidateBytes = Encoding.UTF8.GetByteCount(text.AsSpan(chars, next - chars));
            if (usedBytes + candidateBytes > maxBytes)
            {
                break;
            }

            usedBytes += candidateBytes;
            chars = next;
        }

        return text[..chars];
    }

}
