namespace RuAgent.Cli.Tui;

/// <summary>Presentation data, independent of terminal escape sequences and screen controls.</summary>
public enum TranscriptRole { Prose, Meta, Tool, User }

public sealed record TranscriptRun(string Text, TuiToken Token = TuiToken.Ink, bool Bold = false, bool Dim = false);

public sealed record TranscriptLine(IReadOnlyList<TranscriptRun> Runs, TranscriptRole Role = TranscriptRole.Prose)
{
    internal bool ContinuesPrevious { get; init; }
    internal int StartColumn { get; init; }
    public string Text => string.Concat(Runs.Select(run => run.Text));
    public static TranscriptLine Plain(string text, TranscriptRole role = TranscriptRole.Prose, TuiToken token = TuiToken.Ink) =>
        new([new(text, token)], role);
}

/// <summary>A screen consumes typed rows directly; plain output keeps its existing serializer.</summary>
public interface ITranscriptPresentationWriter
{
    void PreviewPresentation(IReadOnlyList<TranscriptLine> rows);
    void CommitPresentation(IReadOnlyList<TranscriptLine> rows);
    void AppendPresentation(TranscriptLine row);
    void AppendPresentationChunk(TranscriptLine row);
    void AppendActivityPresentation(string activityId, TranscriptLine row, IReadOnlyList<string>? details = null);
    void UpdateActivityPresentation(string activityId, TranscriptLine row);
    Task<bool> ReplaceAssistantPresentationAsync(IReadOnlyList<TranscriptLine> rows);
}

internal sealed class TranscriptRunBuilder
{
    private readonly List<TranscriptRun> _runs = [];
    private readonly System.Text.StringBuilder _pending = new();
    private TuiToken _token;
    private bool _bold, _dim;
    internal TranscriptRunBuilder Append(string text, TuiToken token = TuiToken.Ink, bool bold = false, bool dim = false)
    {
        if (text.Length == 0) return this;
        // Each styled fragment closes its own paint. Keep that boundary even
        // between equal styles, so an enclosing heading ends after the first one.
        if (_token != token || _bold != bold || _dim != dim || token != TuiToken.Ink || bold || dim) Flush();
        (_token, _bold, _dim) = (token, bold, dim);
        _pending.Append(text);
        return this;
    }
    internal TranscriptRunBuilder Append(IEnumerable<TranscriptRun> runs)
    { foreach (var run in runs) Append(run.Text, run.Token, run.Bold, run.Dim); return this; }
    internal TranscriptRunBuilder Append(char value)
    {
        if (_token != TuiToken.Ink || _bold || _dim) Flush();
        (_token, _bold, _dim) = (TuiToken.Ink, false, false);
        _pending.Append(value);
        return this;
    }
    private void Flush()
    {
        if (_pending.Length == 0) return;
        _runs.Add(new(_pending.ToString(), _token, _bold, _dim));
        _pending.Clear();
    }
    internal TranscriptLine Line(TranscriptRole role = TranscriptRole.Prose) { Flush(); return new(_runs.ToArray(), role); }
}
