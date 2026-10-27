using Terminal.Gui.App;

namespace RuAgent.Cli.TerminalGui;

/// <summary>Present text and its raster overlay together, after the driver's flush.</summary>
internal sealed class TgSynchronizedFrame : IDisposable
{
    internal const string BeginSequence = "\x1b[?2026h";
    internal const string EndSequence = "\x1b[?2026l";
    private readonly IApplication _app;
    private readonly Action<string> _write;
    private bool _disposed;
    internal bool IsOpen { get; private set; }

    internal TgSynchronizedFrame(IApplication app, Action<string>? write = null)
    {
        _app = app;
        _write = write ?? (sequence => app.Driver?.WriteRaw(sequence));
        // Terminal.Gui 2.5 raises this only after Driver.Refresh has emitted
        // dirty cells and raster data. A draw event ends too early.
        app.LayoutAndDrawComplete += OnFrameComplete;
    }

    internal void Begin()
    {
        if (_disposed || IsOpen) return;
        _write(BeginSequence);
        IsOpen = true;
    }

    private void OnFrameComplete(object? sender, EventArgs e) => End();

    private void End()
    {
        if (!IsOpen) return;
        IsOpen = false;
        _write(EndSequence);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _app.LayoutAndDrawComplete -= OnFrameComplete;
        // An aborted draw may never reach LayoutAndDrawComplete. Always release
        // synchronized output so the user's terminal is not left frozen.
        End();
    }
}
