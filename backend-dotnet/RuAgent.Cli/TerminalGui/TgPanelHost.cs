using System.Text;
using RuAgent.Cli.Tui;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace RuAgent.Cli.TerminalGui;

/// <summary>Semantic key the hosted panel consumed, so the owner can drive its model.</summary>
public enum TgPanelKeyKind
{
    Up,
    Down,
    Enter,
    Escape,
    Digit,
    Backspace,

    /// <summary>A printable rune; <see cref="TgPanelKey.Character"/> carries it.</summary>
    Character,

    /// <summary>Delete or Ctrl+D: the owner may remove the row under the cursor.</summary>
    Delete,

    /// <summary>Ctrl+V: the secret panel reads the clipboard text itself.</summary>
    Paste,
}

/// <summary>One mapped panel key; <see cref="Digit"/> carries the 1-based character.</summary>
public readonly record struct TgPanelKey(TgPanelKeyKind Kind, char Digit = '\0', char Character = '\0');

/// <summary>
/// How a card row is painted: the card fill behind every row, and the chosen entry reversed
/// onto a filled bar the way a selection reads in a floating card.
/// </summary>
public static class TgCard
{
    public static Attribute Attribute(CardRowKind kind)
    {
        var card = TgPalette.CardBgRgb();
        return kind == CardRowKind.Selected
            ? TgPalette.Attr(TgPalette.CardOnSelectionRgb(), TgPalette.CardSelectionRgb())
            : kind switch
            {
                CardRowKind.Title => TgPalette.Attr(TuiToken.Bright, card),
                CardRowKind.Rule => TgPalette.Attr(TuiToken.Line, card),
                CardRowKind.Section => TgPalette.Attr(TuiToken.Brand2, card),
                CardRowKind.Search => TgPalette.Attr(TuiToken.Meta, card),
                CardRowKind.Field => TgPalette.Attr(TuiToken.Bright, card),
                CardRowKind.Footer or CardRowKind.Spacer => TgPalette.Attr(TuiToken.Meta, card),
                CardRowKind.Disabled => TgPalette.Attr(TuiToken.Line, card),
                _ => TgPalette.Attr(TuiToken.Bright, card),
            };
    }
}

/// <summary>
/// Floating panel over the transcript for the approval, trust, picker and secret surfaces.
/// Content is composed by the classic renderers by the owner (<see cref="ApprovalPrompt"/>,
/// <see cref="ChoiceList"/>, <see cref="KeyInputPanel"/>, <see cref="TuiCard"/>); the host only
/// centers the card and forwards 1/2/3, arrow, Enter, Escape, Backspace and printable keys
/// (the secret panel needs to accept a typed key).
/// The card is a filled block with no border glyphs, which is what makes it
/// read as a card floating on the dark terminal rather than a box drawn on it.
/// </summary>
public sealed class TgPanelHost : View
{
    private Func<int, int, IReadOnlyList<CardLine>> _card = (_, _) => [];

    public TgPanelHost()
    {
        CanFocus = true;
        SetScheme(TgPalette.Scheme(TuiToken.Ink));
        KeyDown += OnKeyDown;
    }

    /// <summary>Raised for every mapped key while the panel owns the keyboard.</summary>
    public event Action<TgPanelKey>? PanelKey;

    /// <summary>The card content for the live viewport size, composed by the classic renderer.</summary>
    public void SetCard(Func<int, int, IReadOnlyList<CardLine>> card)
    {
        ArgumentNullException.ThrowIfNull(card);
        _card = card;
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        if (Viewport.Width <= 0 || Viewport.Height <= 0)
        {
            return true;
        }

        DrawCard(_card(Viewport.Width, Viewport.Height));
        return true;
    }

    private void DrawCard(IReadOnlyList<CardLine> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        var width = Math.Min(lines[0].Width, Viewport.Width);
        var left = Math.Max(0, (Viewport.Width - width) / 2);
        var top = Math.Max(0, (Viewport.Height - lines.Count) / 2);
        for (var i = 0; i < lines.Count && top + i < Viewport.Height; i++)
        {
            SetAttribute(TgCard.Attribute(lines[i].Kind));
            Move(left, top + i);
            AddStr(lines[i].Text);
        }
    }

    private void OnKeyDown(object? sender, Key key)
    {
        var mapped = Map(key);
        if (mapped is null)
        {
            // The panel owns focus for its entire lifetime. Even keys that have no meaning to
            // this modal must not bubble into the composer or its global editing bindings.
            key.Handled = true;
            return;
        }

        key.Handled = true;
        PanelKey?.Invoke(mapped.Value);
    }

    private static TgPanelKey? Map(Key key)
    {
        if (key == Key.CursorUp)
        {
            return new TgPanelKey(TgPanelKeyKind.Up);
        }

        if (key == Key.CursorDown)
        {
            return new TgPanelKey(TgPanelKeyKind.Down);
        }

        if (key == Key.Enter)
        {
            return new TgPanelKey(TgPanelKeyKind.Enter);
        }

        if (key == Key.Esc)
        {
            return new TgPanelKey(TgPanelKeyKind.Escape);
        }

        if (key == Key.Delete || key == Key.D.WithCtrl)
        {
            return new TgPanelKey(TgPanelKeyKind.Delete);
        }

        if (key == Key.V.WithCtrl)
        {
            return new TgPanelKey(TgPanelKeyKind.Paste);
        }

        if (key.KeyCode == KeyCode.Backspace)
        {
            return new TgPanelKey(TgPanelKeyKind.Backspace);
        }

        // Printable input for the masked secret panel. Unshifted digits stay the numbered-pick
        // shortcut (the owner re-reads them as characters while a secret is open); a shifted
        // digit is the symbol the operator typed (`!`), never the base digit. Modified keys
        // are left alone so Ctrl+ chords cannot leak their base letter into a secret.
        if (!key.IsCtrl && !key.IsAlt)
        {
            var rune = key.AsRune;
            if (rune.Value > 0 && rune.Value <= char.MaxValue && !Rune.IsControl(rune))
            {
                var code = key.KeyCode;
                var isUnshiftedDigit = code >= KeyCode.D1 && code <= KeyCode.D9
                    && (char)rune.Value == (char)('1' + (code - KeyCode.D1));
                return isUnshiftedDigit
                    ? new TgPanelKey(TgPanelKeyKind.Digit, (char)('1' + (code - KeyCode.D1)))
                    : new TgPanelKey(TgPanelKeyKind.Character, Character: (char)rune.Value);
            }
        }

        return null;
    }
}
