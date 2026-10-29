using RuAgent.Cli.Tui;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace RuAgent.Cli.TerminalGui;

/// <summary>
/// The opencode-style filled prompt surface: one panel-toned block that grows with a
/// multiline draft. There is no frame and no accent rail — the fill is the shape — and the
/// model and provider sit on the meta row; permission is forwarded to the header.
/// </summary>
public sealed class TgComposerView : View
{
    /// <summary>Rows of an empty composer: blank, input, images/spacer, meta, blank.</summary>
    public const int RowCount = 5;

    /// <summary>Meta row while the draft is one line; it moves down as the editor grows.</summary>
    public const int MetadataRow = 3;

    private const int Inset = 3;
    private readonly Label _ghost;
    private int _maxInputRows = 6;
    private int _inputWidth = 70;
    private string _imagePreview = string.Empty;
    public int InputHeight { get; private set; } = 1;

    /// <summary>Blank row, input rows, images/spacer row, meta row, blank row.</summary>
    public int LayoutHeight => InputHeight + 4;
    public event EventHandler? LayoutChanged;
    public string Preset { get; private set; } = string.Empty;
    public event Action<string>? PresetChanged;
    public string Model { get; private set; } = string.Empty;
    public string ReasoningEffort { get; private set; } = string.Empty;
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

    /// <summary>Presentation only: image bytes remain owned by the submission state.</summary>
    public void SetImages(int pendingCount, int retryCount = 0)
    {
        static string Labels(int count) => string.Join(" ", Enumerable.Range(1,
            Math.Clamp(count, 0, Images.CliImagePolicy.MaxImageCount)).Select(index => $"[image {index}]"));
        var preview = Labels(pendingCount);
        if (retryCount > 0)
            preview += (preview.Length > 0 ? " · " : string.Empty) + "held: " + Labels(retryCount);
        if (_imagePreview == preview) return;
        _imagePreview = preview;
        SetNeedsDraw();
    }

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
        var safePreset = ChatDisplayText.Sanitize(preset, 32);
        if (Preset != safePreset)
        {
            Preset = safePreset;
            PresetChanged?.Invoke(Preset);
        }
        var safeModel = ChatDisplayText.Sanitize(model, 256);
        const string prefix = "models.dev:";
        var safeProvider = ChatDisplayText.Sanitize(provider.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? provider[prefix.Length..] : provider, 128);
        if (Model != safeModel || Provider != safeProvider) ReasoningEffort = string.Empty;
        Model = safeModel;
        Provider = safeProvider;
        SetNeedsDraw();
    }

    public void SetReasoningEffort(string? effort)
    {
        var safe = string.IsNullOrWhiteSpace(effort) ? "—" : ChatDisplayText.Sanitize(effort, 32);
        if (ReasoningEffort == safe) return;
        ReasoningEffort = safe;
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        if (width <= 0 || Viewport.Height <= 0) return true;

        // Reuse the spacer below the draft, keeping the editor and layout height unchanged.
        if (_imagePreview.Length > 0 && InputHeight + 1 < Viewport.Height)
            DrawMeta(Inset, _imagePreview, TuiToken.Brand, InputHeight + 1);

        // The block's fill comes from the view's scheme.
        var row = MetaRow;
        if (row >= Viewport.Height) return true;
        var column = Inset;
        var remaining = Math.Max(0, width - Inset - column);
        var effort = remaining >= 24 && ReasoningEffort.Length > 0
            ? TuiText.Truncate(ReasoningEffort, Math.Max(0, remaining - 10)) : string.Empty;
        var effortWidth = TuiText.VisibleWidth(effort);
        var afterEffort = remaining - effortWidth - (effortWidth > 0 ? 2 : 0);
        var providerWidth = afterEffort >= 28 ? Math.Min(18, afterEffort / 3) : 0;
        var model = TuiText.MiddleTruncate(Model, Math.Max(0, afterEffort - providerWidth - (providerWidth > 0 ? 2 : 0)));
        DrawMeta(column, model, TuiToken.Ink, row);
        column += TuiText.VisibleWidth(model);
        if (effortWidth > 0)
        {
            column += 2;
            DrawMeta(column, effort, TuiToken.Meta, row);
            column += effortWidth;
        }
        if (providerWidth > 0)
            DrawMeta(column + 2, TuiText.Truncate(Provider, providerWidth), TuiToken.Meta, row);
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
