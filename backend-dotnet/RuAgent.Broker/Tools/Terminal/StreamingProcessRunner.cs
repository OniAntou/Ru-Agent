using System.ComponentModel;
using System.Diagnostics;
using System.Text;

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
        _outputLimitBytes = Math.Clamp(outputLimitBytes, 1, 8 * 1024 * 1024);
        _headCapacityChars = headCapacityChars;
        _tailCapacityChars = tailCapacityChars;
    }

    public async Task<StreamingProcessResult> RunAsync(
        ProcessRunSpec spec,
        TerminalOutputDeltaHandler? onDelta,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        using var job = WindowsJobProcessTree.Create();
        var startInfo = BuildStartInfo(spec);
        var stopwatch = Stopwatch.StartNew();
        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException("process_start_failed");
            }

            job.Assign(process.Id);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            return new StreamingProcessResult(
                Status: "failed",
                ExitCode: null,
                DurationMs: (int)stopwatch.Elapsed.TotalMilliseconds,
                StdoutHead: string.Empty,
                StdoutTail: string.Empty,
                StderrHead: "process_start_failed",
                StderrTail: string.Empty,
                StdoutBytes: 0,
                StderrBytes: 0,
                StdoutDroppedBytes: 0,
                StderrDroppedBytes: 0,
                FramesEmitted: 0,
                FramesDropped: 0,
                WorkingDirectory: spec.WorkingDirectory);
        }

        using var registered = process;
        var stdoutBuffer = new HeadTailOutputBuffer(_headCapacityChars, _tailCapacityChars);
        var stderrBuffer = new HeadTailOutputBuffer(_headCapacityChars, _tailCapacityChars);
        long framesEmitted = 0;
        long framesDropped = 0;
        long nextSequence = 1;
        object sequenceLock = new();

        var stdoutTask = DrainAsync(
            process.StandardOutput,
            "stdout",
            stdoutBuffer,
            spec.OutputLimitBytes,
            EmitAsync,
            CancellationToken.None);
        var stderrTask = DrainAsync(
            process.StandardError,
            "stderr",
            stderrBuffer,
            spec.OutputLimitBytes,
            EmitAsync,
            CancellationToken.None);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(spec.TimeoutMs));
        var status = "running";
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            status = "succeeded";
            if (process.ExitCode != 0)
            {
                status = "failed";
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            status = cancellationToken.IsCancellationRequested ? "cancelled" : "timed_out";
            job.KillTree();
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }
        }

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

        stopwatch.Stop();
        return new StreamingProcessResult(
            Status: status,
            ExitCode: process.HasExited ? process.ExitCode : null,
            DurationMs: (int)stopwatch.Elapsed.TotalMilliseconds,
            StdoutHead: stdoutBuffer.Head,
            StdoutTail: stdoutBuffer.Tail,
            StderrHead: stderrBuffer.Head,
            StderrTail: stderrBuffer.Tail,
            StdoutBytes: stdoutBuffer.TotalBytes,
            StderrBytes: stderrBuffer.TotalBytes,
            StdoutDroppedBytes: stdoutBuffer.DroppedBytes,
            StderrDroppedBytes: stderrBuffer.DroppedBytes,
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
                await onDelta(stream, sequence, text, redacted, CancellationToken.None).ConfigureAwait(false);
                Interlocked.Increment(ref framesEmitted);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref framesDropped);
            }
        }
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

    private async Task DrainAsync(
        StreamReader reader,
        string streamName,
        HeadTailOutputBuffer buffer,
        int limitBytes,
        Func<string, string, bool, CancellationToken, ValueTask> emit,
        CancellationToken drainToken)
    {
        var redactor = new ChunkRedactor(_redactionSeeds);
        var charBuffer = new char[DecoderChunkChars];
        while (true)
        {
            var count = await reader.ReadAsync(charBuffer).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            var text = new string(charBuffer, 0, count);
            var safeText = redactor.Push(text);
            if (safeText.Length > 0)
            {
                buffer.Append(safeText);
                await emit(streamName, safeText, safeText != text || redactor.RedactedAnything, drainToken)
                    .ConfigureAwait(false);
            }
        }

        var flush = redactor.Flush();
        if (flush.Length > 0)
        {
            buffer.Append(flush);
            await emit(streamName, flush, redactor.RedactedAnything, drainToken).ConfigureAwait(false);
        }
    }

    private sealed class ChunkRedactor(IReadOnlyList<string> seeds)
    {
        private readonly string[] _seeds = seeds.OrderByDescending(seed => seed.Length).ToArray();
        private readonly int _holdBack = seeds.Count == 0 ? 0 : seeds.Max(seed => seed.Length) - 1;
        private string _pending = string.Empty;

        public bool RedactedAnything { get; private set; }

        public string Push(string chunk)
        {
            if (_seeds.Length == 0)
            {
                return chunk;
            }

            _pending += chunk;
            var safeLength = Math.Max(0, _pending.Length - _holdBack);
            var safePart = _pending[..safeLength];
            _pending = _pending[safeLength..];
            return Redact(safePart);
        }

        public string Flush()
        {
            var remaining = _pending;
            _pending = string.Empty;
            return Redact(remaining);
        }

        private string Redact(string input)
        {
            if (_seeds.Length == 0 || input.Length == 0)
            {
                return input;
            }

            foreach (var seed in _seeds)
            {
                if (input.Contains(seed, StringComparison.Ordinal))
                {
                    input = input.Replace(seed, "[redacted]", StringComparison.Ordinal);
                    RedactedAnything = true;
                }
            }

            return input;
        }
    }
}
