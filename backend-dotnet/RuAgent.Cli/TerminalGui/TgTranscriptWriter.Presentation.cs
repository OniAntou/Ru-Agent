using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;

namespace RuAgent.Cli.TerminalGui;

public sealed partial class TgTranscriptWriter
{
    private static TgTranscriptRow NativeRow(TranscriptLine row)
    {
        var column = row.StartColumn;
        var spans = row.Runs.Select(run =>
        {
            var clean = TuiText.SanitizeUntrusted(run.Text);
            var text = TuiText.ExpandTabs(clean, column, out var endColumn);
            column = endColumn;
            var rgb = TuiPalette.Rgb(run.Token);
            var foreground = run.Dim ? TgPalette.ToColor((rgb.R / 2, rgb.G / 2, rgb.B / 2)) : TgPalette.ToColor(rgb);
            var attr = new Terminal.Gui.Drawing.Attribute(foreground, TgPalette.ToColor((0, 0, 0)),
                run.Bold ? TextStyle.Bold : TextStyle.None);
            return new TgSpan(text, attr);
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
            row.Runs.All(run => run.Token == TuiToken.Ink && !run.Bold && !run.Dim) ? null : spans)
            { ContinuesPrevious = row.ContinuesPrevious };
    }

    public void PreviewPresentation(IReadOnlyList<TranscriptLine> rows) => ProjectPresentation(rows, false);
    public void CommitPresentation(IReadOnlyList<TranscriptLine> rows) => ProjectPresentation(rows, true);

    private void ProjectPresentation(IReadOnlyList<TranscriptLine> rows, bool complete)
    {
        var native = rows.SelectMany(ParagraphRows).ToArray();
        try
        {
            app.Invoke(() =>
            {
                var unchanged = 0;
                while (unchanged < Math.Min(_projectedNative.Length, native.Length)
                    && ReferenceEquals(_projectedNative[unchanged], native[unchanged])) unchanged++;
                // Do not reinsert stable segments the history budget already evicted.
                var evicted = Math.Max(0, _projectedNative.Length - _projectedRows);
                var skip = unchanged >= evicted ? evicted : 0;
                var visible = skip == 0 ? native : native.Skip(skip).ToArray();
                var retained = transcript.ReplaceProjectedRows(_projectedRows, visible, unchanged - skip);
                _projectedRows = complete ? 0 : retained;
                _projectedNative = complete ? [] : native;
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
        var hidden = details?.Select(TuiText.SanitizeUntrusted).ToArray();
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
        ReplaceNativeAssistantAsync(rows.SelectMany(ParagraphRows).ToArray());
}
