using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace RuApp.Services;

/// <summary>COM preview calls run on a pumping STA, never on the WinUI dispatcher.</summary>
internal sealed class PreviewStaWorker : IDisposable
{
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly TaskCompletionSource<uint> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;
    private int _pending;

    public PreviewStaWorker()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "Ru document preview STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public async Task InvokeAsync(Action action, bool cleanup = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        uint thread = await _ready.Task.ConfigureAwait(false);
        if (Interlocked.Increment(ref _pending) > 32 && !cleanup)
        {
            Interlocked.Decrement(ref _pending);
            throw new IOException("preview_renderer_busy: Wait for the document renderer to finish.");
        }
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(() =>
        {
            try { action(); completed.SetResult(); }
            catch (Exception e) { completed.SetException(e); }
            finally { Interlocked.Decrement(ref _pending); }
        });
        if (!PostThreadMessage(thread, 0x8001, 0, 0)) throw new InvalidOperationException("preview_dispatch_unavailable");
        await completed.Task.ConfigureAwait(false);
    }

    private void Run()
    {
        int initialized = CoInitializeEx(0, 2);
        if (initialized < 0) { _ready.SetException(Marshal.GetExceptionForHR(initialized)!); return; }
        try
        {
            PeekMessage(out _, 0, 0, 0, 0);
            _ready.SetResult(GetCurrentThreadId());
            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                if (message.Id == 0x8001) { while (_queue.TryDequeue(out var work)) work(); }
                else { TranslateMessage(ref message); DispatchMessage(ref message); }
            }
        }
        finally { CoUninitialize(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Queue cleanup before quitting; no UI thread waits for a possibly stalled handler.
        _ = StopAsync();
    }

    private async Task StopAsync()
    {
        try { PostThreadMessage(await _ready.Task.ConfigureAwait(false), 0x0012, 0, 0); }
        catch (Exception e) { GuiDiagnosticWriter.AppendTraceLine($"[Preview] STA shutdown: {e.GetType().Name}"); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Hwnd; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostThreadMessage(uint thread, uint message, nuint w, nint l);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
}
