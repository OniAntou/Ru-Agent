using RuAgent.Cli.Tui;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace RuAgent.Cli.TerminalGui;

/// <summary>
/// The prompt frame, cloned from the classic ComposerBox stack: square border, two gray
/// padding rows, the input row with a ghost placeholder, and the meta row.
/// </summary>
public sealed class TgComposerView : View
{
    private readonly Label _ghost;
    private readonly Label _preset;
    private readonly Label _model;
    private readonly Label _provider;

    public TgComposerView()
    {
        BorderStyle = LineStyle.Single;
        SetScheme(TgPalette.Scheme(TuiToken.Line, TuiPalette.ElementRgb()));
        CanFocus = true;

        _ghost = new Label
        {
            X = 1,
            Y = 1,
        };
        _ghost.SetScheme(TgPalette.Scheme(TuiToken.Meta, TuiPalette.ElementRgb()));

        Input = new TextField
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(2),
            Text = string.Empty,
        };
        Input.SetScheme(TgPalette.Scheme(TuiToken.Ink, TuiPalette.ElementRgb()));
        Input.TextChanged += (_, _) => UpdateGhost();

        _preset = new Label { X = 1, Y = 3 };
        _preset.SetScheme(TgPalette.Scheme(TuiToken.Brand, TuiPalette.ElementRgb()));
        _model = new Label { X = Pos.Right(_preset) + 2, Y = 3 };
        _model.SetScheme(TgPalette.Scheme(TuiToken.Ink, TuiPalette.ElementRgb()));
        _provider = new Label { X = Pos.Right(_model) + 1, Y = 3 };
        _provider.SetScheme(TgPalette.Scheme(TuiToken.Meta, TuiPalette.ElementRgb()));

        Add(Input, _ghost, _preset, _model, _provider);
    }

    public TextField Input { get; }

    public void SetPlaceholder(string placeholder)
    {
        _ghost.Text = placeholder;
        UpdateGhost();
    }

    public void SetMeta(string preset, string model, string provider)
    {
        _preset.Text = preset;
        _model.Text = model;
        _provider.Text = ShortenProvider(provider);
    }

    private void UpdateGhost() => _ghost.Visible = string.IsNullOrEmpty(Input.Text);

    /// <summary><c>models.dev:kilo</c> reads as <c>kilo</c>; other ids pass through.</summary>
    private static string ShortenProvider(string provider)
    {
        const string prefix = "models.dev:";
        return provider.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? provider[prefix.Length..]
            : provider;
    }
}
