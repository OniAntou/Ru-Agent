using RuBackend.Diagnostics;

namespace RuBackend.Tests.Fakes;

internal sealed class ControlledDiagnosticLogSink : IDiagnosticLogSink
{
    public static readonly AsyncLocal<bool> InExplicitFlush = new();
    private readonly bool _honorCancellation;
    private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _appendCount;
    private int _cancellationObserved;

    public ControlledDiagnosticLogSink(bool honorCancellation = true)
    {
        _honorCancellation = honorCancellation;
    }

    public Task Entered => _entered.Task;
    public Task Exited => _exited.Task;
    public int AppendCount => _appendCount;
    public bool WasExplicitFlush { get; private set; }
    public bool CancellationObserved => Volatile.Read(ref _cancellationObserved) != 0;

    public void Release() => _release.TrySetResult(true);

    public async Task RunExplicitFlushAsync(Func<Task> flush)
    {
        InExplicitFlush.Value = true;
        try { await flush().ConfigureAwait(false); }
        finally { InExplicitFlush.Value = false; }
    }

    public async ValueTask AppendAsync(string filePath, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        _entered.TrySetResult(true);
        Interlocked.Increment(ref _appendCount);
        WasExplicitFlush = InExplicitFlush.Value;
        try
        {
            if (_honorCancellation)
            {
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _release.Task.ConfigureAwait(false);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? ".");
            await File.AppendAllTextAsync(filePath, System.Text.Encoding.UTF8.GetString(bytes.Span), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Exchange(ref _cancellationObserved, 1);
            throw;
        }
        finally
        {
            _exited.TrySetResult(true);
        }
    }
}

internal sealed class FailingDiagnosticLogSink : IDiagnosticLogSink
{
    public ValueTask AppendAsync(string filePath, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
        throw new IOException("injected failure");
}
