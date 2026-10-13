using RuAgent.Cli.Tui;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace RuAgent.Cli.TerminalGui;

/// <summary>
/// The opencode-style filled prompt surface: one panel-toned block that grows with a
/// multiline draft. There is no frame and no accent rail — the fill is the shape — and the
/// preset, model and provider sit on the meta row inside the block.
/// </summary>
public sealed class TgComposerView : View
{
    /// <summary>Rows of an empty composer: blank, input, blank, meta, blank.</summary>
    public const int RowCount = 5;

    /// <summary>Meta row while the draft is one line; it moves down as the editor grows.</summary>
    public const int MetadataRow = 3;

    private const int Inset = 3;
    private readonly Label _ghost;
    private int _maxInputRows = 6;
    private int _inputWidth = 70;
    public int InputHeight { get; private set; } = 1;

    /// <summary>Blank row, input rows, blank row, meta row, blank row.</summary>
    public int LayoutHeight => InputHeight + 4;
    public event EventHandler? LayoutChanged;
    public string Preset { get; private set; } = string.Empty;
    public string Model { get; private set; } = string.Empty;
    public string Provider { get; private set; } = string.Empty;

    public TgComposerView()
    {
        BorderStyle = LineStyle.None;
        SetScheme(TgPalette.Scheme(TuiToken.Ink, TuiPalette.SurfaceRgb()));
        CanFocus = true;
        Input = new TgPromptEditor { X = Inset, Y = 1, Width = Dim.Fill(Inset), Height = 1, Text = string.Empty };
        Input.Cursor = new Cursor { Style = CursorStyle.SteadyBlock };
        Input.SetScheme(TgPalette.Scheme(TuiToken.Ink, TuiPalette.SurfaceRgb()));
        _ghost = new Label { X = Inset, Y = 1, Width = Dim.Fill(Inset), Height = 1, Text = "Ask anything…" };
        _ghost.SetScheme(TgPalette.Scheme(TuiToken.Meta, TuiPalette.SurfaceRgb()));
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

    /// <summary>The meta row for the current draft height; three while the draft is one line.</summary>
    private int MetaRow => InputHeight + 2;

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

        // The block's fill comes from the view's scheme; the meta row is the only row the
        // composer paints itself.
        var row = MetaRow;
        if (row >= Viewport.Height) return true;
        var available = Math.Max(0, width - Inset * 2);
        var preset = TuiText.Truncate(Preset, Math.Min(12, available));
        DrawMeta(Inset, preset, TuiToken.Brand, row);
        var column = Inset + TuiText.VisibleWidth(preset) + 2;
        var remaining = Math.Max(0, width - Inset - column);
        var providerWidth = remaining >= 28 ? Math.Min(18, remaining / 3) : 0;
        var model = TuiText.MiddleTruncate(Model, Math.Max(0, remaining - providerWidth - (providerWidth > 0 ? 2 : 0)));
        DrawMeta(column, model, TuiToken.Ink, row);
        if (providerWidth > 0)
            DrawMeta(column + TuiText.VisibleWidth(model) + 2, TuiText.Truncate(Provider, providerWidth), TuiToken.Meta, row);
        return true;
    }

    private void DrawMeta(int column, string text, TuiToken token, int row)
    {
        if (text.Length == 0 || column >= Viewport.Width) return;
        SetAttribute(TgPalette.Attr(token, TuiPalette.SurfaceRgb()));
        Move(column, row);
        AddStr(TuiText.Truncate(text, Viewport.Width - column));
    }
}
