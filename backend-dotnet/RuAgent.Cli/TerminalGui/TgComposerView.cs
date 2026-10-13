using RuAgent.Cli.Tui;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace RuAgent.Cli.TerminalGui;

/// <summary>Filled prompt surface with bounded metadata.</summary>
public sealed class TgComposerView : View
{
    public const int RowCount = 5;
    public const int MetadataRow = 3;
    private const int Inset = 3;
    private readonly Label _ghost;
    public string Preset { get; private set; } = string.Empty;
    public string Model { get; private set; } = string.Empty;
    public string Provider { get; private set; } = string.Empty;

    public TgComposerView()
    {
        BorderStyle = LineStyle.None;
        SetScheme(TgPalette.Scheme(TuiToken.Ink, TuiPalette.SurfaceRgb()));
        CanFocus = true;
        Input = new TextField { X = Inset, Y = 1, Width = Dim.Fill(Inset), Text = string.Empty };
        Input.Cursor = new Cursor { Style = CursorStyle.SteadyBlock };
        Input.SetScheme(TgPalette.Scheme(TuiToken.Ink, TuiPalette.SurfaceRgb()));
        _ghost = new Label { X = Inset, Y = 1, Width = Dim.Fill(Inset), Height = 1, Text = "Ask anything…" };
        _ghost.SetScheme(TgPalette.Scheme(TuiToken.Meta, TuiPalette.SurfaceRgb()));
        Input.TextChanged += (_, _) => _ghost.Visible = string.IsNullOrEmpty(Input.Text);
        Add(Input, _ghost);
    }

    public TextField Input { get; }

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
        if (Viewport.Height > MetadataRow)
        {
            var available = Math.Max(0, width - Inset * 2);
            var preset = TuiText.Truncate(Preset, Math.Min(12, available));
            DrawMeta(Inset, preset, TuiToken.Brand);
            var column = Inset + TuiText.VisibleWidth(preset) + 2;
            var remaining = Math.Max(0, width - Inset - column);
            var providerWidth = remaining >= 28 ? Math.Min(18, remaining / 3) : 0;
            var model = TuiText.MiddleTruncate(Model, Math.Max(0, remaining - providerWidth - (providerWidth > 0 ? 2 : 0)));
            DrawMeta(column, model, TuiToken.Ink);
            if (providerWidth > 0)
                DrawMeta(column + TuiText.VisibleWidth(model) + 2, TuiText.Truncate(Provider, providerWidth), TuiToken.Meta);
        }
        return true;
    }

    private void DrawMeta(int column, string text, TuiToken token)
    {
        if (text.Length == 0 || column >= Viewport.Width) return;
        SetAttribute(TgPalette.Attr(token, TuiPalette.SurfaceRgb()));
        Move(column, MetadataRow);
        AddStr(TuiText.Truncate(text, Viewport.Width - column));
    }
}
