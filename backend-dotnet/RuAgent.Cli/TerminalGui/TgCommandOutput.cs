using System.Text;
using Terminal.Gui.App;

namespace RuAgent.Cli.TerminalGui;

/// <summary>Routes local command results independently of the run's transcript stream.</summary>
internal sealed class TgCommandOutput
{
    private readonly TgChatWindow _window;
    private readonly TextWriter _transcript;
    private readonly AsyncLocal<Capture?> _current = new();

    internal TgCommandOutput(TgChatWindow window, TextWriter transcript)
    {
        _window = window;
        _transcript = transcript;
        Output = new CommandWriter(this, isError: false);
        Error = new CommandWriter(this, isError: true);
    }

    internal TextWriter Output { get; }
    internal TextWriter Error { get; }

    internal async Task RunAsync(string title, bool report, Func<Task> action, CancellationToken ct,
        bool captureWelcome = true, bool captureOnChat = false)
    {
        // Nested session commands belong to the enclosing command's result. AsyncLocal
        // keeps an independent running turn outside this capture.
        if (_current.Value is { IsOpen: true })
        {
            await action().ConfigureAwait(false);
            return;
        }

        var welcome = false;
        if (!await OnUiAsync(() =>
        {
            welcome = _window.Landing.Visible;
            _window.Hints.ClearCommandFeedback();
        }, ct).ConfigureAwait(false)) return;

        if (!report && (!welcome || !captureWelcome) && !captureOnChat)
        {
            await action().ConfigureAwait(false);
            return;
        }

        var capture = new Capture();
        _current.Value = capture;
        try { await action().ConfigureAwait(false); }
        finally { capture.Close(); _current.Value = null; }

        ct.ThrowIfCancellationRequested();
        var (text, error) = capture.Read();
        var rows = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (rows.Length == 0) return;

        var document = report;
        if (!await OnUiAsync(() =>
        {
            if (_window.Landing.Visible)
            {
                var clean = TgAnsiPaint.Parse(rows[0]).Clean;
                if (rows.Length == 1 && clean.Length <= 200)
                {
                    document = false;
                    _window.Hints.ShowCommandFeedback(clean, error);
                }
                else document = true;
            }
            else if (!report)
            {
                _transcript.WriteLine(text.TrimEnd('\r', '\n'));
                _transcript.Flush();
            }
        }, ct).ConfigureAwait(false)) return;

        if (!document) return;
        await TgPanelRequests.RequestDocumentAsync(_window, title, rows, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (report)
            await OnUiAsync(() =>
            {
                if (_window.Landing.Visible) return;
                _transcript.WriteLine(text.TrimEnd('\r', '\n'));
                _transcript.Flush();
            }, ct).ConfigureAwait(false);
    }

    private async Task<bool> OnUiAsync(Action action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_window.App is not { } app) return false;
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            app.Invoke(() =>
            {
                if (ct.IsCancellationRequested) { done.TrySetCanceled(ct); return; }
                try { action(); done.TrySetResult(true); }
                catch (Exception error) { done.TrySetException(error); }
            });
        }
        catch (NotInitializedException) { return false; }
        return await done.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private void Write(string? value, bool error)
    {
        if (_current.Value?.TryAppend(value, error) != true) _transcript.Write(value);
    }

    private sealed class CommandWriter(TgCommandOutput owner, bool isError) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => owner.Write(value.ToString(), isError);
        public override void Write(string? value) => owner.Write(value, isError);
        public override void WriteLine(string? value) => owner.Write(value + "\n", isError);
        public override void WriteLine() => owner.Write("\n", isError);
        public override void Flush()
        {
            if (owner._current.Value is not { IsOpen: true }) owner._transcript.Flush();
        }
    }

    private sealed class Capture
    {
        private readonly StringBuilder _text = new();
        private bool _error;
        private bool _truncated;
        private volatile bool _closed;
        internal bool IsOpen => !_closed;

        internal bool TryAppend(string? value, bool error)
        {
            lock (_text)
            {
                if (_closed) return false;
                if (string.IsNullOrEmpty(value)) return true;
                _error |= error;
                var available = TgTranscriptView.MaxRetainedCharacters - _text.Length;
                _text.Append(value.AsSpan(0, Math.Min(value.Length, available)));
                _truncated |= value.Length > available;
                return true;
            }
        }

        internal void Close()
        {
            lock (_text) _closed = true;
        }

        internal (string Text, bool Error) Read()
        {
            lock (_text)
                return (_text.ToString() + (_truncated ? "\nCommand output truncated.\n" : ""), _error);
        }
    }
}
