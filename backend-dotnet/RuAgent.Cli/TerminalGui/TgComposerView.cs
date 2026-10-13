using RuAgent.Cli.Tui;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace RuAgent.Cli.TerminalGui;

/// <summary>Square prompt frame and editor sharing the terminal background.</summary>
public sealed class TgComposerView : View
{
    public const int RowCount = 3;
    public const int MetadataRow = 2;
    private const int Inset = 3;
    private readonly Label _ghost;
    private int _maxInputRows = 6;
    private int _inputWidth = 70;
    public int InputHeight { get; private set; } = 1;
    public int LayoutHeight => InputHeight + 2;
    public event EventHandler? LayoutChanged;
    public string Preset { get; private set; } = string.Empty;
    public string Model { get; private set; } = string.Empty;
    public string Provider { get; private set; } = string.Empty;

    public TgComposerView()
    {
        BorderStyle = LineStyle.None;
        SetScheme(TgPalette.Scheme(TuiToken.Ink));
        CanFocus = true;
        Input = new TgPromptEditor { X = Inset, Y = 1, Width = Dim.Fill(Inset), Height = 1, Text = string.Empty };
        Input.Cursor = new Cursor { Style = CursorStyle.SteadyBlock };
        Input.SetScheme(TgPalette.Scheme(TuiToken.Ink));
        _ghost = new Label { X = Inset, Y = 1, Width = Dim.Fill(Inset), Height = 1, Text = "Ask anything…" };
        _ghost.SetScheme(TgPalette.Scheme(TuiToken.Meta));
        Input.TextChanged += (_, _) => RefreshHeight();
        Input.ContentsChanged += (_, _) => RefreshHeight();
        Input.SubViewsLaidOut += (_, _) => RefreshHeight();
        Add(Input, _ghost);
    }

    public TgPromptEditor Input { get; }

    public void SetViewportBudget(int width, int maxRows)
    {
        _inputWidth = Math.Max(1, width - Inset * 2);
        _maxInputRows = Math.Clamp(maxRows, 1, 6);
        RefreshHeight();
    }

    private void RefreshHeight()
    {
        _ghost.Visible = string.IsNullOrEmpty(Input.Text);
        var rows = Input.Viewport.Width == _inputWidth && Input.Viewport.Width > 0
            ? Math.Max(1, Input.Lines)
            : Input.Text.Split('\n').Sum(line => Math.Max(1,
                (TuiText.VisibleWidth(line) + _inputWidth - 1) / _inputWidth));
        var height = Math.Clamp(rows, 1, _maxInputRows);
        if (height == InputHeight) return;
        InputHeight = height;
        Input.Height = height;
        Height = LayoutHeight;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        SetNeedsDraw();
    }

    public void SetPlaceholder(string placeholder)
    {
        _ghost.Text = placeholder;
        _ghost.Visible = string.IsNullOrEmpty(Input.Text);
    }

    public void SetMeta(string preset, string model, string provider)
    {
        Preset = ChatDisplayText.Sanitize(preset, 32);
        Model = ChatDisplayText.Sanitize(model, 256);
        const string prefix = "models.dev:";
        Provider = ChatDisplayText.Sanitize(provider.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? provider[prefix.Length..] : provider, 128);
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        if (width <= 0 || Viewport.Height <= 0) return true;
        DrawFrame(width);
        if (Viewport.Height > InputHeight + 1)
        {
            var available = Math.Max(0, width - Inset * 2);
            var column = Inset;
            var remaining = available;
            var providerWidth = remaining >= 28 ? Math.Min(18, remaining / 3) : 0;
            var model = TuiText.MiddleTruncate(Model, Math.Max(0, remaining - providerWidth - (providerWidth > 0 ? 2 : 0)));
            DrawMeta(column, model, TuiToken.Meta);
            if (providerWidth > 0)
                DrawMeta(column + TuiText.VisibleWidth(model) + 2, TuiText.Truncate(Provider, providerWidth), TuiToken.Meta);
        }
        return true;
    }

    private void DrawFrame(int width)
    {
        if (width < 2) return;
        SetAttribute(TgPalette.Attr(TuiToken.Line));
        Move(0, 0);
        AddStr("┌" + new string('─', width - 2) + "┐");
        for (var row = 1; row < Viewport.Height - 1; row++)
        {
            SetAttribute(TgPalette.Attr(TuiToken.Line));
            Move(0, row); AddStr("│");
            Move(width - 1, row); AddStr("│");
            SetAttribute(TgPalette.Attr(TuiToken.Ink));
            Move(1, row); AddStr(new string(' ', width - 2));
        }
        SetAttribute(TgPalette.Attr(TuiToken.Line));
        Move(0, Viewport.Height - 1);
        AddStr("└" + new string('─', width - 2) + "┘");
        SetAttribute(TgPalette.Attr(TuiToken.Meta));
        if (width >= 10 && Preset.Length > 0)
        {
            Move(2, 0);
            AddStr(" " + TuiText.Truncate(Preset, Math.Min(16, width - 6)) + " ");
        }
        SetAttribute(TgPalette.Attr(TuiToken.Aurora));
        Move(1, 1); AddStr("›");
    }

    private void DrawMeta(int column, string text, TuiToken token)
    {
        if (text.Length == 0 || column >= Viewport.Width) return;
        SetAttribute(TgPalette.Attr(token));
        var x = Math.Max(1, column - 1);
        var label = " " + TuiText.Truncate(text, Math.Max(0, Viewport.Width - Inset - column)) + " ";
        Move(x, Viewport.Height - 1);
        AddStr(new string(' ', TuiText.VisibleWidth(label)));
        Move(x, Viewport.Height - 1);
        AddStr(label);
    }
}
