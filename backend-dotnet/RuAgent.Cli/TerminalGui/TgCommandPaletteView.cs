using RuAgent.Cli.Tui;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;

namespace RuAgent.Cli.TerminalGui;

/// <summary>Compact searchable command list displayed above the pinned composer.</summary>
public sealed class TgCommandPaletteView : View
{
    private readonly TgCommandPalette _palette;

    public TgCommandPaletteView(TgCommandPalette palette)
    {
        _palette = palette;
        SetScheme(TgPalette.Scheme(TuiToken.Ink));
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        var height = Viewport.Height;
        if (width <= 0 || height <= 0)
        {
            return true;
        }

        DrawRow(0, $"Commands · {_palette.Query}", TuiToken.Brand, width);
        var visibleCount = Math.Min(TgCommandPalette.MaxVisibleCommands, Math.Max(0, height - 2));
        var firstIndex = Math.Clamp(
            _palette.SelectedIndex - visibleCount + 1,
            0,
            Math.Max(0, _palette.Matches.Count - visibleCount));
        var endIndex = Math.Min(_palette.Matches.Count, firstIndex + visibleCount);
        for (var index = firstIndex; index < endIndex; index++)
        {
            var command = _palette.Matches[index];
            DrawRow(
                index - firstIndex + 1,
                $"{(index == SelectedIndex ? "❯" : " ")} {command.Command,-12} {command.Description}",
                index == SelectedIndex ? TuiToken.Brand : TuiToken.Meta,
                width);
        }

        if (_palette.Matches.Count == 0 && height > 2)
        {
            DrawRow(1, "  no matching commands", TuiToken.Meta, width);
        }

        DrawRow(
            height - 1,
            _palette.StatusMessage ?? "type to search · ↑↓ select · enter apply · esc close",
            _palette.StatusMessage is null ? TuiToken.Meta : TuiToken.Err,
            width);
        return true;
    }

    internal int SelectedIndex => _palette.SelectedIndex;

    private void DrawRow(int row, string text, TuiToken token, int width)
    {
        SetAttribute(TgPalette.Attr(token));
        Move(0, row);
        AddStr(TuiText.Truncate(text, width));
    }
}
