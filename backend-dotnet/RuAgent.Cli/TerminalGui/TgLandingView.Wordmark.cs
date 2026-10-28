using RuAgent.Cli.Tui;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Pixel = Terminal.Gui.Drawing.Color;

namespace RuAgent.Cli.TerminalGui;

public sealed partial class TgLandingView
{
    private readonly RasterImageCommand _wordmarkCommand = new()
    {
        Id = "ru-welcome-" + Guid.NewGuid().ToString("N"), RenderAfterText = true,
        Encoder = new SixelEncoder { Quantizer = new ColorQuantizer { MaxColors = 32 } },
    };
    private TgSquareWordmarkRaster? _wordmarkRaster;
    private readonly KittyGraphicsEncoder _wordmarkKittyEncoder = new();
    private Pixel[,]? _wordmarkSource;
    private bool _wordmarkWasKitty;
    private int _wordmarkPaletteColors = 32;
    private bool _wordmarkOwnerRegistered;
    private TgSynchronizedFrame? _wordmarkFrame;
    internal bool SquareWordmarkActive { get; private set; }
    internal System.Drawing.Rectangle SquareWordmarkBounds { get; private set; }

    private bool TryDrawSquareWordmark(int width, int height, int top)
    {
        var driver = App?.Driver;
        var kitty = driver?.KittyGraphicsSupport;
        var sixel = driver?.SixelSupport;
        var useKitty = kitty?.IsSupported == true && driver!.GetOutput().UseKittyGraphics;
        if (AnimationPaused || !(useKitty || sixel?.IsSupported == true)) return HideSquareWordmark();
        var resolution = useKitty ? kitty!.Resolution : sixel!.Resolution;
        var paletteColors = !useKitty && sixel!.MaxPaletteColors > 0 ? Math.Min(32, sixel.MaxPaletteColors) : 32;
        if (resolution.Width <= 0 || resolution.Height <= 0 || resolution.Width > 128 || resolution.Height > 256)
            return HideSquareWordmark();
        var columns = TgWelcomeWordmark.Width + 3;
        var rows = (int)Math.Ceiling((double)TgSquareWordmarkRaster.Height / TgSquareWordmarkRaster.Width
            * columns * resolution.Width / resolution.Height);
        if (rows <= 0 || rows > height || columns > width
            || (long)columns * resolution.Width * rows * resolution.Height > 262144) return HideSquareWordmark();
        var frame = new System.Drawing.Rectangle((width - columns) / 2,
            top + (TuiHeader.WordmarkRows - rows) / 2, columns, rows);
        if (frame.Top < 1 || frame.Bottom > height || ComposerBounds?.IntersectsWith(frame) == true
            || HintRow is { } hint && hint >= frame.Top && hint < frame.Bottom) return HideSquareWordmark();

        _wordmarkRaster ??= new TgSquareWordmarkRaster();
        var source = _wordmarkRaster.Render(_animationTime);
        if (!ReferenceEquals(Image, source)) Image = source;
        if (!_wordmarkOwnerRegistered)
        {
            // ImageView owns the library's internal raster-retention hook. Let
            // it register once, then reuse that returned ID for the bounded
            // custom placement. No private ID format or reflection is used.
            var buffer = driver!.GetOutputBuffer();
            var previous = buffer.GetRasterImages().Select(image => image.Id).ToHashSet();
            base.OnDrawingContent(null);
            var registered = buffer.GetRasterImages().Where(image => image.Id is not null && !previous.Contains(image.Id)).ToArray();
            if (registered.Length != 1) return HideSquareWordmark();
            _wordmarkCommand.Id = registered[0].Id;
            _wordmarkOwnerRegistered = true;
        }
        var pixelWidth = columns * resolution.Width;
        var pixelHeight = rows * resolution.Height;
        var pixels = _wordmarkCommand.Pixels;
        var resized = pixels is null || pixels.GetLength(0) != pixelWidth || pixels.GetLength(1) != pixelHeight;
        if (resized) pixels = new Pixel[pixelWidth, pixelHeight];
        var changed = resized || !ReferenceEquals(source, _wordmarkSource) || _wordmarkWasKitty != useKitty
            || SquareWordmarkBounds.Size != frame.Size || _wordmarkPaletteColors != paletteColors;
        if (changed)
        {
            _wordmarkPaletteColors = paletteColors;
            _wordmarkCommand.Encoder!.Quantizer.MaxColors = paletteColors;
            ScaleWordmark(source, pixels!, resolution.Width);
            _wordmarkSource = source;
            _wordmarkCommand.Pixels = pixels!;
            _wordmarkCommand.SourceVersion++;
            _wordmarkCommand.EncodedSixel = useKitty ? null : _wordmarkCommand.Encoder!.EncodeSixel(pixels!);
            _wordmarkCommand.EncodedKitty = useKitty
                ? _wordmarkKittyEncoder.EncodeKitty(pixels!, columns, rows, KittyGraphicsEncoder.GetImageId(_wordmarkCommand.Id!)) : null;
        }
        var dirty = changed || !SquareWordmarkActive || SquareWordmarkBounds != frame;
        _wordmarkWasKitty = useKitty;
        SquareWordmarkBounds = frame;
        SquareWordmarkActive = true;
        // Hand the compositor a new placement snapshot, preserving its previous
        // geometry when the logo moves. Pixel/encoded-data caches stay reused.
        driver!.GetOutputBuffer().AddRasterImage(new RasterImageCommand
        {
            Id = _wordmarkCommand.Id, Pixels = _wordmarkCommand.Pixels,
            EncodedSixel = _wordmarkCommand.EncodedSixel, EncodedKitty = _wordmarkCommand.EncodedKitty,
            Encoder = _wordmarkCommand.Encoder, SourceVersion = _wordmarkCommand.SourceVersion,
            DestinationCells = ViewportToScreen(frame), IsDirty = dirty,
            AlwaysRender = !useKitty, RenderAfterText = true,
        });
        return true;
    }

    private static void ScaleWordmark(Pixel[,] source, Pixel[,] pixels, int cellWidth)
    {
        Array.Clear(pixels);
        // Scale both axes by the cell's pixel width so the tiles stay square;
        // leave symmetric transparent padding in the enclosing cell rectangle.
        var width = Math.Min(pixels.GetLength(0), (int)Math.Round((double)source.GetLength(0) * cellWidth / TgSquareWordmarkRaster.Tile));
        var height = Math.Min(pixels.GetLength(1), (int)Math.Round((double)source.GetLength(1) * cellWidth / TgSquareWordmarkRaster.Tile));
        var left = (pixels.GetLength(0) - width) / 2; var top = (pixels.GetLength(1) - height) / 2;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            pixels[left + x, top + y] = source[x * source.GetLength(0) / width, y * source.GetLength(1) / height];
    }

    private bool HideSquareWordmark()
    {
        ClearRasterWordmark();
        return false;
    }

    internal void ClearRasterWordmark()
    {
        if (SquareWordmarkActive) BeginWordmarkFrame();
        SquareWordmarkActive = false;
        if (Image is not null) Image = null;
    }

    private void BeginWordmarkFrame()
    {
        if (App is { } app) (_wordmarkFrame ??= new TgSynchronizedFrame(app)).Begin();
    }

    protected override void OnFrameChanged(in System.Drawing.Rectangle frame)
    {
        base.OnFrameChanged(frame);
        // Empty views may skip content drawing, so clear their retained image
        // here rather than waiting for the next paint pass.
        if (frame.Width <= 0 || frame.Height <= 0) ClearRasterWordmark();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClearRasterWordmark();
            _wordmarkFrame?.Dispose();
        }
        base.Dispose(disposing);
    }
}
