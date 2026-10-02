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
}

/// <summary>One mapped panel key; <see cref="Digit"/> carries the 1-based character.</summary>
public readonly record struct TgPanelKey(TgPanelKeyKind Kind, char Digit = '\0');

/// <summary>
/// Floating panel over the transcript for the approval, trust, picker and secret surfaces.
/// Content is composed by the classic renderers by the owner (<see cref="ApprovalPrompt"/>,
/// <see cref="ChoiceList"/>, <see cref="KeyInputPanel"/>); the host only strips the palette
/// escapes, centers the frame and forwards 1/2/3, arrow, Enter and Escape keys.
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

        var code = key.KeyCode;
        if (code >= KeyCode.D1 && code <= KeyCode.D9)
        {
            return new TgPanelKey(TgPanelKeyKind.Digit, (char)('1' + (code - KeyCode.D1)));
        }

        return null;
    }
}
