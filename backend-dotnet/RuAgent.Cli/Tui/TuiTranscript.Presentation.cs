namespace RuAgent.Cli.Tui;

public sealed partial class TuiTranscript
{
    private void WritePresentationOutcome(TuiTurnOutcome outcome, string? errorCode, double seconds, bool streamedText)
    {
        if (outcome == TuiTurnOutcome.Completed)
        {
            var parts = new List<string> { TuiStatusLine.FormatElapsed(seconds), _toolsSettled == 1 ? "1 tool" : $"{_toolsSettled} tools" };
            if (!string.IsNullOrWhiteSpace(_summaryDetail)) parts.Add(_summaryDetail);
            _surface.AppendPresentation(new TranscriptRunBuilder().Append("  ✓ ", TuiToken.Ok)
                .Append(string.Join(" · ", parts), TuiToken.Meta).Line(TranscriptRole.Meta));
            if (!streamedText && _toolsSettled == 0)
                _surface.AppendPresentation(new TranscriptRunBuilder().Append("  ▲ no text arrived from the model", TuiToken.Warn)
                    .Append(" · the run completed but its reply never reached this terminal · retry, or switch model with /model", TuiToken.Line)
                    .Line(TranscriptRole.Meta));
            return;
        }
        if (outcome == TuiTurnOutcome.Cancelled)
        {
            _surface.AppendPresentation(new TranscriptRunBuilder().Append("  ✗ ", TuiToken.Err)
                .Append($"{TuiStatusLine.FormatElapsed(seconds)} · cancelled", TuiToken.Meta).Line(TranscriptRole.Meta));
            return;
        }
        var info = TuiFailureCatalog.For(errorCode ?? _terminalCode ?? "run_failed");
        _surface.AppendPresentation(new TranscriptRunBuilder().Append("  ✗ ", TuiToken.Err)
            .Append($"{TuiStatusLine.FormatElapsed(seconds)} · ", TuiToken.Meta).Append(info.Title, TuiToken.Err)
            .Append($" ({info.Code})", TuiToken.Meta).Line(TranscriptRole.Meta));
        _surface.AppendPresentation(TranscriptLine.Plain("    " + info.Cause, TranscriptRole.Meta, TuiToken.Line));
        _surface.AppendPresentation(TranscriptLine.Plain("    " + info.Fix, TranscriptRole.Meta, TuiToken.Line));
    }

    private string RetainedPaint(string text, TuiToken token) =>
        _surface.Presentation is not null ? text : TuiPalette.Paint(text, token);

    private void WriteStructuredTool(string? activityId, ToolLineStatus status, string display, string? target, string? meta)
    {
        var row = TuiToolRow.ComposePresentation(status, display, target, meta, Width);
        if (activityId is null) _surface.AppendPresentation(row);
        else _surface.AppendActivityPresentation(activityId, row,
            meta?.Contains("ctrl+o", StringComparison.Ordinal) == true ? _retained.ToArray() : null);
    }
}
