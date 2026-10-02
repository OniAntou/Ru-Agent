using RuAgent.Cli.Tui;
using Terminal.Gui.ViewBase;

namespace RuAgent.Cli.TerminalGui;

/// <summary>Row styles the transcript can paint; maps 1:1 to the classic palette roles.</summary>
public enum TranscriptStyle
{
    Prose,
    User,
    Tool,
    Meta,
    Error,
}

/// <summary>
/// Append-only transcript with a tail pin and word wrap — the Terminal.Gui analogue of the
/// classic renderer's sticky transcript region. Drawing stays custom so the OpenCode look
/// (user block, inline tool rows, diff indentation) survives the migration.
/// </summary>
public sealed class TgTranscriptView : View
{
    private readonly List<(string Text, TranscriptStyle Style)> _lines = [];
    private List<(string Text, TranscriptStyle Style)> _wrapped = [];
    private int _wrapWidth = -1;

    public void Append(string text, TranscriptStyle style)
    {
        _lines.Add((text, style));
        SetNeedsDraw();
    }

    /// <summary>Appends to the tail line, the way streamed prose grows in place.</summary>
    public void AppendChunk(string text)
    {
        if (_lines.Count == 0)
        {
            Append(text, TranscriptStyle.Prose);
            return;
        }

        var last = _lines[^1];
        _lines[^1] = (last.Text + text, last.Style);
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        if (width != _wrapWidth)
        {
            _wrapped = WrapAll(width);
            _wrapWidth = width;
        }

        var height = Viewport.Height;
        var offset = Math.Max(0, _wrapped.Count - height);
        for (var i = 0; i < height && offset + i < _wrapped.Count; i++)
        {
            var (text, style) = _wrapped[offset + i];
            SetAttribute(StyleAttribute(style));
            Move(0, i);
            AddStr(text);
        }

        return true;
    }

    private List<(string Text, TranscriptStyle Style)> WrapAll(int width)
    {
        var wrapped = new List<(string, TranscriptStyle)>();
        foreach (var (text, style) in _lines)
        {
            if (TuiText.VisibleWidth(text) <= width)
            {
                wrapped.Add((text, style));
                continue;
            }

            var rest = text;
            while (TuiText.VisibleWidth(rest) > width)
            {
                wrapped.Add((TuiText.Truncate(rest, width), style));
                rest = rest[TuiText.Truncate(rest, width).Length..];
            }

            wrapped.Add((rest, style));
        }

        return wrapped;
    }

    private static Terminal.Gui.Drawing.Attribute StyleAttribute(TranscriptStyle style) => style switch
    {
        TranscriptStyle.User => TgPalette.Attr(TuiToken.Ink, TuiPalette.SurfaceRgb()),
        TranscriptStyle.Tool => TgPalette.Attr(TuiToken.Meta),
        TranscriptStyle.Meta => TgPalette.Attr(TuiToken.Meta),
        TranscriptStyle.Error => TgPalette.Attr(TuiToken.Err),
        _ => TgPalette.Attr(TuiToken.Ink),
    };
}
