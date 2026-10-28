using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgSquareWordmarkIntegrationTests
{
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Raster_is_used_only_when_the_driver_supports_it(bool supported)
    {
        Run(supported, (app, w, _) =>
        {
            Assert.True(supported == w.Landing.SquareWordmarkActive,
                $"supported={supported}; reported={app.Driver!.SixelSupport?.IsSupported}; landing={w.Landing.Visible}/{w.Landing.Frame}; image={w.Landing.SquareWordmarkBounds}; pixels={w.Landing.SquareWordmarkActive}; app={w.Landing.App is not null}; paused={w.Landing.AnimationPaused}; input={w.Composer.Frame}");
            var requests = app.Driver!.GetOutputBuffer().GetRasterImages();
            if (supported)
            {
                Assert.True(w.Landing.SquareWordmarkActive);
                Assert.NotEmpty(requests);
                var output = app.Driver.GetOutput().GetLastOutput();
                var rasterOffset = output.LastIndexOf("\x1bP", StringComparison.Ordinal);
                var presentOffset = output.LastIndexOf(TgSynchronizedFrame.EndSequence, StringComparison.Ordinal);
                Assert.True(rasterOffset >= 0 && presentOffset > rasterOffset,
                    "The text/raster frame must be presented after raster data has been flushed.");
                Assert.All(requests, request =>
                {
                    Assert.NotNull(request.Pixels); Assert.NotNull(request.Encoder);
                    var encoded = request.EncodedSixel ?? request.Encoder.EncodeSixel(request.Pixels);
                    Assert.StartsWith("\x1bP", encoded); Assert.Contains("q", encoded);
                });
                Assert.False(w.Landing.SquareWordmarkBounds.IntersectsWith(w.Composer.Frame));
                Assert.True(w.Landing.SquareWordmarkBounds.Y >= 1);
                Assert.True(w.Landing.SquareWordmarkBounds.Bottom <= w.Hints.Frame.Y);
                var sky = new TuiGalaxySky();
                sky.Render(app.Driver.Cols, w.Landing.SkyRows!.Value, w.Landing.AnimationTime);
                var frame = w.Landing.SquareWordmarkBounds;
                var stars = 0;
                for (var row = frame.Top; row < frame.Bottom; row++)
                {
                    var bits = sky.Dots[row * sky.Columns + frame.Left];
                    if (bits == 0) continue;
                    stars++;
                    Assert.Equal(((char)(0x2800 + bits)).ToString(), app.Driver.Contents![row, frame.Left].Grapheme);
                }
                Assert.True(stars > 0, "The transparent padding must leave underlying Braille stars visible.");
            }
            else
            {
                Assert.Empty(requests);
                Assert.Contains("█▀▀█ █  █ █▀▀█", string.Join('\n', TgSnapshot.Capture(app)));
                Assert.DoesNotContain(TgSynchronizedFrame.BeginSequence, app.Driver.GetOutput().GetLastOutput());
                Assert.DoesNotContain(TgSynchronizedFrame.EndSequence, app.Driver.GetOutput().GetLastOutput());
            }
            return false;
        });
    }

    [Fact]
    public void Quiet_redraws_reuse_encoded_data_and_respect_the_terminal_palette_limit()
    {
        RasterImageCommand? previous = null;
        Run(true, (app, w, stage) =>
        {
            var image = Assert.Single(app.Driver!.GetOutputBuffer().GetRasterImages());
            if (stage == 0)
            {
                previous = image; w.Landing.SetNeedsDraw(); return true;
            }
            if (stage == 1)
            {
                Assert.Equal(previous!.SourceVersion, image.SourceVersion);
                Assert.Same(previous.EncodedSixel, image.EncodedSixel);
                app.Driver.SetSixelSupport(new SixelSupportResult
                {
                    IsSupported = true, Resolution = new System.Drawing.Size(10, 20),
                    SupportsTransparency = true, MaxPaletteColors = 16,
                });
                w.Landing.SetNeedsDraw(); return true;
            }
            Assert.True(image.SourceVersion > previous!.SourceVersion);
            Assert.Equal(16, image.Encoder!.Quantizer.MaxColors);
            Assert.NotSame(previous.EncodedSixel, image.EncodedSixel);
            return false;
        });
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Kitty_output_requires_both_capability_and_output_enablement(bool enabled)
    {
        Run(true, (app, w, stage) =>
        {
            if (stage == 0)
            {
                app.Driver!.SetSixelSupport(new SixelSupportResult { IsSupported = false });
                app.Driver.SetKittyGraphicsSupport(new KittyGraphicsSupportResult
                {
                    IsSupported = true, Resolution = new System.Drawing.Size(10, 20),
                });
                app.Driver.GetOutput().UseKittyGraphics = enabled;
                w.Landing.SetNeedsDraw(); return true;
            }
            Assert.Equal(enabled, w.Landing.SquareWordmarkActive);
            var images = app.Driver!.GetOutputBuffer().GetRasterImages();
            if (enabled)
            {
                var image = Assert.Single(images);
                Assert.StartsWith("\x1b_G", image.EncodedKitty);
                Assert.Null(image.EncodedSixel);
                Assert.True(image.RenderAfterText);
            }
            else
            {
                Assert.Empty(images);
                Assert.Contains("█▀▀█ █  █ █▀▀█", string.Join('\n', TgSnapshot.Capture(app)));
            }
            return false;
        });
    }

    [Fact]
    public void Raster_placements_are_removed_for_overlays_and_chat_then_restored()
    {
        Run(true, (app, w, stage) =>
        {
            var images = app.Driver!.GetOutputBuffer().GetRasterImages();
            switch (stage)
            {
                case 0:
                    Assert.NotEmpty(images); w.OpenCommandPalette(); w.TickWelcome(); return true;
                case 1:
                    Assert.False(w.Landing.SquareWordmarkActive); Assert.Empty(images);
                    w.DismissCommandPalette(); w.TickWelcome(); w.Landing.SetNeedsDraw(); return true;
                case 2:
                    Assert.NotEmpty(images); w.Transcript.Append("hello", TranscriptStyle.Prose); return true;
                case 3:
                    Assert.False(w.Landing.Visible); Assert.Empty(images);
                    Assert.DoesNotContain(string.Join('\n', TgSnapshot.Capture(app)), ch => ch is >= '\u2801' and <= '\u28ff');
                    w.Transcript.Clear(); return true;
                default:
                    Assert.True(w.Landing.Visible); Assert.NotEmpty(images); return false;
            }
        });
    }

    [Fact]
    public void Capability_loss_and_restore_keep_the_readable_glyph_logo()
    {
        Run(true, (app, w, stage) =>
        {
            switch (stage)
            {
                case 0:
                    Assert.True(w.Landing.SquareWordmarkActive);
                    app.Driver!.SetSixelSupport(new SixelSupportResult { IsSupported = false });
                    w.Landing.SetNeedsDraw(); return true;
                case 1:
                    Assert.False(w.Landing.SquareWordmarkActive);
                    Assert.Empty(app.Driver!.GetOutputBuffer().GetRasterImages());
                    Assert.Contains("█▀▀█ █  █ █▀▀█", string.Join('\n', TgSnapshot.Capture(app)));
                    SetSupport(app, true); w.Landing.SetNeedsDraw(); return true;
                default:
                    Assert.True(w.Landing.SquareWordmarkActive);
                    Assert.NotEmpty(app.Driver!.GetOutputBuffer().GetRasterImages()); return false;
            }
        });
    }

    [Theory]
    [InlineData(35)] [InlineData(0)]
    public void Constrained_or_empty_viewports_remove_the_raster_and_restore_it_when_room_returns(int columns)
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var window = new Terminal.Gui.Views.Window { BorderStyle = LineStyle.None };
        var landing = new TgLandingView { Width = 120, Height = 36, WordmarkRow = 15 };
        window.Add(landing);
        Exception? failure = null; var completed = false; var stage = 0;
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            try
            {
                if (stage++ == 0)
                {
                    SetSupport(app, true); landing.SetNeedsDraw(); return true;
                }
                if (stage == 2)
                {
                    Assert.True(landing.SquareWordmarkActive);
                    // A standalone owner avoids both real-console size polling
                    // and the chat layout's automatic wordmark/input placement.
                    landing.Width = columns; landing.Height = 14; landing.WordmarkRow = 3;
                    landing.SkyRows = 6; landing.HintRow = 12;
                    landing.ComposerBounds = new System.Drawing.Rectangle(2, 7, 31, 5);
                    landing.Layout(); landing.SetNeedsDraw(); return true;
                }
                if (stage == 3)
                {
                    Assert.Equal(columns, landing.Viewport.Width);
                    Assert.False(landing.SquareWordmarkActive);
                    if (columns > 0) Assert.Contains("█▀▀█ █  █ █▀▀█", TgSnapshot.Capture(app)[3]);
                    Assert.Empty(app.Driver!.GetOutputBuffer().GetRasterImages());
                    landing.Width = 120; landing.Height = 36; landing.WordmarkRow = 15;
                    landing.SkyRows = 26; landing.HintRow = 32;
                    landing.ComposerBounds = new System.Drawing.Rectangle(20, 27, 80, 5);
                    landing.Layout(); landing.SetNeedsDraw(); return true;
                }
                Assert.True(landing.SquareWordmarkActive);
                Assert.Single(app.Driver!.GetOutputBuffer().GetRasterImages());
                completed = true;
            }
            catch (Exception ex) { failure = ex; }
            app.RequestStop(); return false;
        });
        app.Run(window);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.True(completed);
    }

    [Theory]
    [InlineData(0, 20)] [InlineData(int.MaxValue, 1)] [InlineData(128, 256)]
    public void Invalid_or_excessive_pixel_resolutions_do_not_enable_a_large_image(int width, int height)
    {
        Run(true, (app, w, stage) =>
        {
            if (stage == 0)
            {
                app.Driver!.SetSixelSupport(new SixelSupportResult
                    { IsSupported = true, Resolution = new System.Drawing.Size(width, height) });
                w.Landing.SetNeedsDraw(); return true;
            }
            Assert.False(w.Landing.SquareWordmarkActive);
            Assert.Empty(app.Driver!.GetOutputBuffer().GetRasterImages());
            var screen = TgSnapshot.Capture(app);
            Assert.True(string.Join('\n', screen).Contains("█▀▀█ █  █ █▀▀█", StringComparison.Ordinal),
                $"Fallback missing at {w.Landing.Frame}, top={w.Landing.WordmarkRow}: " +
                string.Join(" / ", screen.Skip(Math.Max(0,w.Landing.WordmarkRow!.Value-1)).Take(6).Select(row=>row.PadRight(120).Substring(35,50))));
            return false;
        });
    }

    private static void SetSupport(IApplication app, bool supported) => app.Driver!.SetSixelSupport(
        new SixelSupportResult { IsSupported = supported, Resolution = new System.Drawing.Size(10, 20),
            SupportsTransparency = true, MaxPaletteColors = 32 });

    private static void Run(bool supported, Func<IApplication, TgChatWindow, int, bool> inspect)
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        Exception? failure = null; var completed = false; var stage = 0; var ready = false;
        var clock = System.Diagnostics.Stopwatch.StartNew(); var readyAt = TimeSpan.Zero;
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            if (app.Driver!.Cols != 120 || app.Driver.Rows != 36 || window.Frame.Width != 120 || window.Frame.Height != 36)
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(3))
                {
                    failure = new TimeoutException("Raster test could not establish a stable 120x36 frame.");
                    app.RequestStop(); return false;
                }
                app.Driver.SetScreenSize(120, 36); window.FocusComposer(); return true;
            }
            SetSupport(app, supported); window.FocusComposer(); window.Landing.SetNeedsDraw();
            ready = true; readyAt = clock.Elapsed; return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            if (!ready || clock.Elapsed - readyAt < TimeSpan.FromMilliseconds(100)) return true;
            try
            {
                if (inspect(app, window, stage++)) return true;
                completed = true;
            }
            catch (Exception ex) { failure = ex; }
            app.RequestStop(); return false;
        });
        app.Run(window);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.True(completed);
    }
}
