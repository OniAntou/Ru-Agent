using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;

namespace RuAgent.Cli.TerminalGui;

public sealed partial class TgTranscriptWriter
{
    private static TgTranscriptRow NativeRow(TranscriptLine row)
    {
        var spans = row.Runs.Select(run =>
        {
            var rgb = TuiPalette.Rgb(run.Token);
            var foreground = run.Dim ? TgPalette.ToColor((rgb.R / 2, rgb.G / 2, rgb.B / 2)) : TgPalette.ToColor(rgb);
            var attr = new Terminal.Gui.Drawing.Attribute(foreground, TgPalette.ToColor((0, 0, 0)),
                run.Bold ? TextStyle.Bold : TextStyle.None);
            return new TgSpan(TuiText.Strip(run.Text), attr);
        }).ToArray();
        var style = row.Role switch
        {
            TranscriptRole.Meta => TranscriptStyle.Meta,
            TranscriptRole.Tool => TranscriptStyle.Tool,
            TranscriptRole.User => TranscriptStyle.User,
            _ => TranscriptStyle.Prose,
        };
        var text = string.Concat(spans.Select(span => span.Text));
        // Unstyled rows retain their native role's default paint; plain prose must
        // not acquire a synthetic span merely because it used the typed transport.
        return new(text, style,
            row.Runs.All(run => run.Token == TuiToken.Ink && !run.Bold && !run.Dim) ? null : spans);
    }

    public void PreviewPresentation(IReadOnlyList<TranscriptLine> rows) => ProjectPresentation(rows, false);
    public void CommitPresentation(IReadOnlyList<TranscriptLine> rows) => ProjectPresentation(rows, true);

    private void ProjectPresentation(IReadOnlyList<TranscriptLine> rows, bool complete)
    {
        var native = rows.Select(NativeRow).ToArray();
        try
        {
            app.Invoke(() =>
            {
                if (_projectedRow)
                    transcript.ReplaceTail(native.Length == 0 ? null : native[0].Text,
                        native.Length == 0 ? TranscriptStyle.Prose : native[0].Style,
                        native.Length == 0 ? null : native[0].Spans);
                else if (native.Length > 0)
                    transcript.Append(native[0].Text, native[0].Style, native[0].Spans);
                foreach (var row in native.Skip(1)) transcript.Append(row.Text, row.Style, row.Spans);
                _projectedRow = !complete && native.Length > 0;
            });
        }
        catch (NotInitializedException) { }
    }

    public void AppendPresentation(TranscriptLine row)
    {
        var native = NativeRow(row);
        try { app.Invoke(() =>
        {
            if (row.Role == TranscriptRole.Tool) transcript.ClearLive();
            transcript.Append(native.Text, native.Style, native.Spans);
        }); }
        catch (NotInitializedException) { }
    }

    public void AppendPresentationChunk(TranscriptLine row)
    {
        var native = NativeRow(row);
        var append = _rowOpen;
        _rowOpen = true;
        try { app.Invoke(() =>
        {
            if (append) transcript.AppendChunk(native.Text, native.Spans);
            else transcript.Append(native.Text, native.Style, native.Spans);
        }); }
        catch (NotInitializedException) { }
    }

    public void AppendActivityPresentation(string activityId, TranscriptLine row, IReadOnlyList<string>? details = null)
    {
        var native = NativeRow(row);
        var hidden = details?.Select(TuiText.Strip).ToArray();
        try
        {
            app.Invoke(() =>
            {
                transcript.ClearLive();
                if (hidden is { Length: > 0 }) transcript.AppendCollapsed(native.Text, hidden, native.Style);
                else transcript.Append(native.Text, native.Style, native.Spans);
                _toolRows[activityId] = (transcript.CommittedRowCount - 1, transcript.RetentionVersion);
                while (_toolRows.Count > 64) _toolRows.Remove(_toolRows.Keys.First());
            });
        }
        catch (NotInitializedException) { }
    }

    public void UpdateActivityPresentation(string activityId, TranscriptLine row)
    {
        var native = NativeRow(row);
        try { app.Invoke(() =>
        {
            if (_toolRows.TryGetValue(activityId, out var mark) && mark.Version == transcript.RetentionVersion)
                transcript.ReplaceToolRow(mark.Row, native.Text);
        }); }
        catch (NotInitializedException) { }
    }

    public Task<bool> ReplaceAssistantPresentationAsync(IReadOnlyList<TranscriptLine> rows) =>
        ReplaceNativeAssistantAsync(rows.Select(NativeRow).ToArray());
}
