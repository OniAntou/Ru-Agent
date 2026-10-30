using RuAgent.Cli.Tui;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace RuAgent.Cli.TerminalGui;

/// <summary>Welcome-only Braille sky behind the existing Ru wordmark and composer.</summary>
public sealed partial class TgLandingView : ImageView
{
    public int? WordmarkRow { get; set; }
    internal int? HintRow { get; set; }
    internal int? SkyRows { get; set; }
    internal System.Drawing.Rectangle? ComposerBounds { get; set; }
    internal System.Drawing.Rectangle WordmarkBackdropBounds { get; private set; }
    internal Func<System.Drawing.Rectangle, bool>? IsWordmarkOccluded { get; set; }
    private readonly TuiGalaxySky _sky = new();
    private TimeSpan? _lastTick;
    private double _animationTime;
    internal double AnimationTime => _animationTime;
    private bool _animationPaused;
    internal bool AnimationPaused
    {
        get => _animationPaused;
        set
        {
            if (_animationPaused == value) return;
            _animationPaused = value;
            if (value) HideSquareWordmark();
            SetNeedsDraw();
        }
    }
    private static readonly string[] Glyphs = Enumerable.Range(0, 256)
        .Select(bits => bits == 0 ? " " : ((char)(0x2800 + bits)).ToString()).ToArray();
    private static readonly Terminal.Gui.Drawing.Attribute[] SkyColors =
    [
        TgPalette.Attr((0, 0, 0)), TgPalette.Attr((16, 23, 45)),
        TgPalette.Attr((36, 69, 101)), TgPalette.Attr((63, 120, 129)),
        TgPalette.Attr((147, 133, 180)), TgPalette.Attr((193, 142, 175)),
        TgPalette.Attr((92, 159, 203)), TgPalette.Attr((181, 217, 228)),
        TgPalette.Attr((244, 233, 211)),
    ];
    private static readonly Terminal.Gui.Drawing.Attribute[] MeteorColors =
    [
        TgPalette.Attr((0, 0, 0)), TgPalette.Attr((28, 28, 28)),
        TgPalette.Attr((45, 45, 45)), TgPalette.Attr((70, 70, 70)),
        TgPalette.Attr((100, 100, 100)), TgPalette.Attr((135, 135, 135)),
        TgPalette.Attr((175, 175, 175)), TgPalette.Attr((225, 225, 225)),
        TgPalette.Attr((255, 255, 255)),
    ];

    public TgLandingView()
    {
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = false;
        Enabled = false;
        UseRasterGraphics = true;
        UseBackgroundRendering = false;
        AllowSixelUpscaling = false;
        MaxSixelPaletteColors = 32;
        SetScheme(TgPalette.Scheme(TuiToken.Meta));
    }

    public void Tick() => Tick(TimeSpan.FromSeconds(
        (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency));

    internal void Tick(TimeSpan now)
    {
        if (!Visible || AnimationPaused || Viewport.Width <= 0 || Viewport.Height <= 0)
        {
            _lastTick = null;
            return;
        }
        if (_lastTick is not { } previous) { _lastTick = now; return; }
        var elapsed = now - previous;
        if (elapsed.TotalMilliseconds < TuiGalaxySky.FrameMilliseconds) return;
        _lastTick = now;
        // Resume without jumping forward after a hidden window or slow terminal.
        _animationTime += Math.Min(elapsed.TotalSeconds, 0.1);
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        var height = Viewport.Height;
        if (width <= 0 || height <= 0)
        {
            ClearRasterWordmark();
            return true;
        }

        if (width < TuiHeader.MinWordmarkWidth)
        {
            HideSquareWordmark();
            const string fallback = "Ru Agent";
            SetAttribute(TgPalette.Attr(TuiToken.Aurora));
            Move(Math.Max(0, (width - fallback.Length) / 2), WordmarkRow ?? Math.Max(0, height / 2));
            AddStr(fallback);
            return true;
        }

        var left = Math.Max(0, (width - TgWelcomeWordmark.Width) / 2);
        var top = WordmarkRow ?? Math.Max(0, (height - TuiHeader.WordmarkRows) / 2);
        var squareWordmark = TryDrawSquareWordmark(width, height, top);
        // Keep the holes, soft seams and transparent padding quiet as well as
        // the letter faces. The encoded image also paints this black matte.
        WordmarkBackdropBounds = squareWordmark ? SquareWordmarkBounds
            : new System.Drawing.Rectangle(left, top, TgWelcomeWordmark.Width + 1, TuiHeader.WordmarkRows + 1);
        if (squareWordmark) BeginWordmarkFrame();
        _sky.Render(width, Math.Clamp(SkyRows ?? height, 0, height), _animationTime);
        var dots = _sky.Dots;
        var colors = _sky.Colors;
        byte previousColor = byte.MaxValue;
        for (var r = 0; r < height; r++)
        {
            Move(0, r);
            for (var c = 0; c < width; c++)
            {
                var index = r * _sky.Columns + c;
                // Keep the command hint's entire row quiet, including outside its
                // centered view bounds where the full-width sky would otherwise show.
                var behindWordmark = WordmarkBackdropBounds.Contains(c, r);
                var inside = !behindWordmark && c < _sky.Columns && r < _sky.Rows && r != HintRow
                    && ComposerBounds?.Contains(c, r) != true;
                var bits = inside ? dots[index] : (byte)0;
                var color = inside ? colors[index] : (byte)0;
                if (!behindWordmark && r >= _sky.Rows && r != HintRow && ComposerBounds?.Contains(c, r) != true)
                    (bits, color) = TuiWelcomeStars.At(c, r, _animationTime);
                if (color != previousColor) { SetAttribute(SkyColors[color]); previousColor = color; }
                AddStr(Glyphs[bits]);
            }
        }

        DrawMeteors(width, height);
        if (squareWordmark) return true;
        var sweep = TgWelcomeWordmark.SweepPosition(_animationTime);
        for (var row = 0; row < TuiHeader.WordmarkRows; row++)
        {
            var y = top + row;
            if (y < 0 || y >= height || y == HintRow) continue;
            for (var column = 0; column < TgWelcomeWordmark.Width && left + column < width; column++)
            {
                if (ComposerBounds?.Contains(left + column, y) == true) continue;
                SetAttribute(TgWelcomeWordmark.AttributeAt(row, column, sweep));
                Move(left + column, y);
                AddStr(TgWelcomeWordmark.Glyph(row, column));
            }
        }
        DrawWordmarkShadow(left, top + TuiHeader.WordmarkRows, width, height);
        return true;
    }

    private void DrawWordmarkShadow(int left, int row, int width, int height)
    {
        if (row < 0 || row >= height || row == HintRow) return;
        SetAttribute(TgWelcomeWordmark.ShadowAttribute);
        for (var column = 0; column < TgWelcomeWordmark.Width; column++)
        {
            var x = left + column + 1;
            if (x >= width || ComposerBounds?.Contains(x, row) == true) continue;
            var glyph = TgWelcomeWordmark.ShadowGlyph(column);
            if (glyph == " ") continue;
            Move(x, row);
            AddStr(glyph);
        }
    }

    private void DrawMeteors(int width, int height)
    {
        Span<TuiShootingStars.Cell> cells = stackalloc TuiShootingStars.Cell[TuiShootingStars.MaxCells];
        var count = TuiShootingStars.Collect(cells, width, height, _animationTime, out _);
        for (var i = 0; i < count; i++)
        {
            var cell = cells[i];
            if (cell.Row == HintRow || ComposerBounds?.Contains(cell.Column, cell.Row) == true
                || WordmarkBackdropBounds.Contains(cell.Column, cell.Row)) continue;
            byte bits;
            if (cell.Column < _sky.Columns && cell.Row < _sky.Rows)
            {
                var index = cell.Row * _sky.Columns + cell.Column;
                bits = _sky.Dots[index];
            }
            else (bits, _) = TuiWelcomeStars.At(cell.Column, cell.Row, _animationTime);
            SetAttribute(MeteorColors[cell.Color]);
            Move(cell.Column, cell.Row);
            AddStr(Glyphs[bits | cell.Dots]);
        }
    }
}
