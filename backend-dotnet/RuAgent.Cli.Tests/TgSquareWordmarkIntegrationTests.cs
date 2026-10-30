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
                    Assert.Equal(new Color(0, 0, 0, 255), request.Pixels[0, 0]);
                    Assert.All(request.Pixels.Cast<Color>(), pixel => Assert.Equal(255, pixel.A));
                    var encoded = request.EncodedSixel ?? request.Encoder.EncodeSixel(request.Pixels);
                    Assert.StartsWith("\x1bP0;0;0q", encoded);
                });
                Assert.False(w.Landing.SquareWordmarkBounds.IntersectsWith(w.Composer.Frame));
                Assert.True(w.Landing.SquareWordmarkBounds.Y >= 1);
                Assert.True(w.Landing.SquareWordmarkBounds.Bottom <= w.Hints.Frame.Y);
                var frame = w.Landing.SquareWordmarkBounds;
                for (var row = frame.Top; row < frame.Bottom; row++)
                for (var column = frame.Left; column < frame.Right; column++)
                {
                    Assert.True(string.IsNullOrWhiteSpace(app.Driver.Contents![row, column].Grapheme));
                    Assert.Equal(TgPalette.ToColor((0, 0, 0)), TgSnapshot.CellAttribute(app, row, column).Background);
                }
                Assert.Contains(string.Join('\n', TgSnapshot.Capture(app)), ch => ch is >= '\u2801' and <= '\u28ff');
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

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Logo_backdrop_blocks_stars_and_a_crossing_meteor_without_hiding_the_rest_of_the_sky(bool supported)
    {
        Run(supported, (app, w, stage) =>
        {
            if (stage == 0)
            {
                w.Landing.Tick(TimeSpan.Zero);
                for (var tick = 1; tick <= 133; tick++)
                    w.Landing.Tick(TimeSpan.FromSeconds(13.25 * tick / 133));
                w.Landing.SetNeedsDraw();
                return true;
            }
            var backdrop = w.Landing.WordmarkBackdropBounds;
            Span<TuiShootingStars.Cell> cells = stackalloc TuiShootingStars.Cell[TuiShootingStars.MaxCells];
            var count = TuiShootingStars.Collect(cells, 120, 36, w.Landing.AnimationTime, out _);
            Assert.Contains(cells[..count].ToArray(), cell => backdrop.Contains(cell.Column, cell.Row));
            var screen = TgSnapshot.Capture(app);
            for (var row = backdrop.Top; row < backdrop.Bottom; row++)
            {
                var text = screen[row].PadRight(120).Substring(backdrop.Left, backdrop.Width);
                Assert.DoesNotContain(text, ch => ch is >= '\u2801' and <= '\u28ff');
            }
            Assert.Contains(string.Join('\n', screen), ch => ch is >= '\u2801' and <= '\u28ff');
            Assert.True(w.Composer.Input.HasFocus);
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
            Assert.Same(TgWordmarkPalette.Instance, image.Encoder!.Quantizer.PaletteBuildingAlgorithm);
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

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Slash_menu_keeps_sky_animating_without_raster_covering_the_commands(bool supported)
    {
        var previousTime = 0d;
        string[]? menuRows = null;
        string? visibleSky = null;
        Run(supported, (app, w, stage) =>
        {
            if (stage == 0)
            {
                w.Composer.Input.Text = "/";
                w.TickWelcome();
                Assert.True(w.IsSlashDropdownOpen);
                Assert.False(w.Landing.AnimationPaused);
                previousTime = w.Landing.AnimationTime;
                return true;
            }
            w.TickWelcome();
            Assert.True(w.Landing.AnimationTime > previousTime);
            previousTime = w.Landing.AnimationTime;
            Assert.False(w.Landing.AnimationPaused);
            if (stage <= 2)
            {
                var menu = Assert.Single(w.SubViews.OfType<TgSlashDropdown>());
                Assert.True(menu.Frame.IntersectsWith(new System.Drawing.Rectangle(
                    0, w.Landing.WordmarkRow!.Value, w.Frame.Width, TuiHeader.WordmarkRows)));
                Assert.False(w.Landing.SquareWordmarkActive);
                Assert.Empty(app.Driver!.GetOutputBuffer().GetRasterImages());
                var screen = TgSnapshot.Capture(app);
                var currentRows = screen.Skip(menu.Frame.Y).Take(menu.Frame.Height)
                    .Select(row => row.PadRight(app.Driver.Cols).Substring(menu.Frame.X, menu.Frame.Width)).ToArray();
                Assert.Contains("commands", string.Join('\n', currentRows));
                Assert.DoesNotContain(string.Join('\n', currentRows), ch => ch is >= '\u2801' and <= '\u28ff');
                var outsideMenu = string.Concat(screen.Take(w.Landing.SkyRows!.Value).Select(row =>
                    row.PadRight(app.Driver.Cols)[..menu.Frame.Left] + row.PadRight(app.Driver.Cols)[menu.Frame.Right..]));
                if (stage == 1) { menuRows = currentRows; visibleSky = outsideMenu; }
                else
                {
                    Assert.Equal(menuRows, currentRows);
                    Assert.NotEqual(visibleSky, outsideMenu);
                    w.Composer.Input.Text = string.Empty;
                    Assert.False(w.IsSlashDropdownOpen);
                }
                return true;
            }
            Assert.Equal(supported, w.Landing.SquareWordmarkActive);
            return false;
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
