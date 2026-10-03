using System.Text;
using RuAgent.Cli.Tui;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

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

    /// <summary>Ctrl+V: the secret panel reads the clipboard text itself.</summary>
    Paste,
}

/// <summary>One mapped panel key; <see cref="Digit"/> carries the 1-based character.</summary>
public readonly record struct TgPanelKey(TgPanelKeyKind Kind, char Digit = '\0', char Character = '\0');

/// <summary>
/// Floating panel over the transcript for the approval, trust, picker and secret surfaces.
/// Content is composed by the classic renderers by the owner (<see cref="ApprovalPrompt"/>,
/// <see cref="ChoiceList"/>, <see cref="KeyInputPanel"/>); the host only strips the palette
/// escapes, centers the frame and forwards 1/2/3, arrow, Enter, Escape, Backspace and
/// printable keys (the secret panel needs to accept a typed key).
/// </summary>
public sealed class TgPanelHost : View
{
    private Func<int, IReadOnlyList<string>> _render = _ => [];

    public TgPanelHost()
    {
        CanFocus = true;
        SetScheme(TgPalette.Scheme(TuiToken.Ink));
        KeyDown += OnKeyDown;
    }

    /// <summary>Raised for every mapped key while the panel owns the keyboard.</summary>
    public event Action<TgPanelKey>? PanelKey;

    /// <summary>The panel content for a viewport width, composed by the classic renderer.</summary>
    public void SetRenderer(Func<int, IReadOnlyList<string>> render)
    {
        ArgumentNullException.ThrowIfNull(render);
        _render = render;
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        if (Viewport.Width <= 0 || Viewport.Height <= 0)
        {
            return true;
        }

        var stripped = _render(Viewport.Width).Select(TuiText.Strip).ToList();
        if (stripped.Count == 0)
        {
            return true;
        }

        var centered = TuiFloatingPanel.Center(stripped, Viewport.Width);
        var top = Math.Max(0, (Viewport.Height - centered.Count) / 2);
        for (var i = 0; i < centered.Count && top + i < Viewport.Height; i++)
        {
            var isFrame = i == 0 || i == centered.Count - 1;
            SetAttribute(TgPalette.Attr(isFrame ? TuiToken.Brand : TuiToken.Ink));
            Move(0, top + i);
            AddStr(centered[i]);
        }

        return true;
    }

    private void OnKeyDown(object? sender, Key key)
    {
        var mapped = Map(key);
        if (mapped is null)
        {
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
