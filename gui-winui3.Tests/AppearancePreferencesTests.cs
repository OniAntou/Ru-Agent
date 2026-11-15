using System.Text;
using RuApp.Services;
using Windows.UI;
using Xunit;

namespace RuApp.Tests;

public sealed class AppearancePreferencesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ru-appearance-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Sanitizing_drops_malformed_colors_path_like_fonts_and_foreign_background_names()
    {
        var sanitized = new AppearancePreferences
        {
            BackgroundKind = AppearanceBackgroundKind.Image,
            BackgroundFile = @"..\..\Windows\win.ini",
            PanelOpacity = 7,
            DarkColors = new AppearanceColorSet(Canvas: "#12ab9f", Text: "red", Accent: "12345"),
            UiFont = @"C:\Fonts\evil.ttf#Evil",
            CodeFont = "  JetBrains Mono  ",
            SidebarSide = (AppearanceSidebarSide)42,
        }.Sanitized();

        Assert.Equal(AppearanceBackgroundKind.None, sanitized.BackgroundKind);
        Assert.Null(sanitized.BackgroundFile);
        Assert.Equal(AppearancePreferences.MaximumPanelOpacity, sanitized.PanelOpacity);
        Assert.Equal("#12AB9F", sanitized.DarkColors.Canvas);
        Assert.Null(sanitized.DarkColors.Text);
        Assert.Null(sanitized.DarkColors.Accent);
        Assert.Null(sanitized.UiFont);
        Assert.Equal("JetBrains Mono", sanitized.CodeFont);
        Assert.Equal(AppearanceSidebarSide.Left, sanitized.SidebarSide);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef.png", AppearanceBackgroundKind.Image, true)]
    [InlineData("0123456789abcdef0123456789abcdef.mp4", AppearanceBackgroundKind.Video, true)]
    [InlineData("0123456789abcdef0123456789abcdef.png", AppearanceBackgroundKind.Video, false)]
    [InlineData("0123456789abcdef0123456789abcdef.exe", AppearanceBackgroundKind.Image, false)]
    [InlineData("holiday.png", AppearanceBackgroundKind.Image, false)]
    public void A_background_survives_only_as_a_generated_name_of_the_matching_kind(
        string file, AppearanceBackgroundKind kind, bool kept)
    {
        var sanitized = new AppearancePreferences { BackgroundKind = kind, BackgroundFile = file }.Sanitized();

        Assert.Equal(kept, sanitized.HasBackground);
    }

    [Fact]
    public void The_store_round_trips_and_reads_a_file_with_a_byte_order_mark()
    {
        var path = Path.Combine(_directory, "appearance.json");
        var store = new AppearancePreferenceStore(path);
        var preferences = new AppearancePreferences
        {
            PanelOpacity = 0.6,
            LightColors = new AppearanceColorSet(Accent: "#7F22CE"),
            SidebarSide = AppearanceSidebarSide.Right,
            CornerStyle = AppearanceCornerStyle.Round,
            UiFont = "Georgia",
        };

        Assert.True(store.TrySave(preferences));
        Assert.Equal(preferences.Sanitized(), store.Load());

        var json = File.ReadAllText(path);
        Assert.DoesNotContain("isEmpty", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hasBackground", json, StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Equal(preferences.Sanitized(), store.Load());
    }

    [Fact]
    public void An_unknown_value_resets_only_its_own_field()
    {
        var parsed = AppearancePreferenceStore.Parse(Encoding.UTF8.GetBytes(
            """{ "sidebarSide": "top", "cornerStyle": 2, "backgroundFit": "center", "panelOpacity": 0.5, "uiFont": "Georgia" }"""));

        Assert.NotNull(parsed);
        Assert.Equal(AppearanceSidebarSide.Left, parsed!.SidebarSide);
        Assert.Equal(AppearanceCornerStyle.Default, parsed.CornerStyle);
        Assert.Equal(AppearanceBackgroundFit.Center, parsed.BackgroundFit);
        Assert.Equal(0.5, parsed.PanelOpacity);
        Assert.Equal("Georgia", parsed.UiFont);
    }

    [Fact]
    public void A_missing_or_corrupt_file_loads_the_stock_look()
    {
        var path = Path.Combine(_directory, "appearance.json");
        Assert.Equal(AppearancePreferences.Default, new AppearancePreferenceStore(path).Load());

        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "{ not json");
        Assert.Equal(AppearancePreferences.Default, new AppearancePreferenceStore(path).Load());
    }

    [Fact]
    public void An_exported_theme_leaves_the_background_behind_and_an_import_keeps_this_pcs_background()
    {
        const string file = "0123456789abcdef0123456789abcdef.png";
        var mine = new AppearancePreferences
        {
            BackgroundKind = AppearanceBackgroundKind.Image,
            BackgroundFile = file,
            PanelOpacity = 0.5,
            DarkColors = new AppearanceColorSet(Accent: "#7F77DD"),
        };

        var exported = AppearancePreferenceStore.Export(mine);
        Assert.DoesNotContain(file, exported, StringComparison.Ordinal);

        var theirs = new AppearancePreferences
        {
            BackgroundKind = AppearanceBackgroundKind.Image,
            BackgroundFile = "fedcba9876543210fedcba9876543210.png",
            SidebarSide = AppearanceSidebarSide.Right,
        };
        var imported = AppearancePreferenceStore.Import(
            Encoding.UTF8.GetBytes(AppearancePreferenceStore.Serialize(theirs)), mine);

        Assert.NotNull(imported);
        Assert.Equal(file, imported!.BackgroundFile);
        Assert.Equal(0.5, imported.PanelOpacity);
        Assert.Equal(AppearanceSidebarSide.Right, imported.SidebarSide);
        Assert.True(imported.DarkColors.IsEmpty);
        Assert.Null(AppearancePreferenceStore.Import(Encoding.UTF8.GetBytes("[1, 2, 3]"), mine));
    }

    [Fact]
    public void No_overrides_keep_the_theme_pack_exactly()
    {
        Assert.Same(
            RuThemePalettes.StudioDark,
            AppearancePalette.Build(RuThemePalettes.StudioDark, AppearanceColorSet.Empty, isDark: true));
    }

    [Fact]
    public void Text_matching_the_canvas_is_raised_until_it_reads_on_every_surface()
    {
        var palette = AppearancePalette.Build(
            RuThemePalettes.StudioDark,
            new AppearanceColorSet(Canvas: "#101826", Text: "#16202E", MutedText: "#101826", Accent: "#111111"),
            isDark: true);

        foreach (var background in new[] { palette.Canvas, palette.Surface, palette.Sidebar })
        {
            Assert.True(AppearancePalette.ContrastRatio(palette.Ink, background) >= AppearancePalette.MinimumTextContrast);
        }

        Assert.True(AppearancePalette.ContrastRatio(palette.Muted, palette.Canvas) >= AppearancePalette.MinimumSecondaryContrast);
        Assert.True(AppearancePalette.ContrastRatio(palette.Accent, palette.Canvas) >= AppearancePalette.MinimumSecondaryContrast);
        Assert.Equal(palette.Accent, palette.ToggleOn);
    }

    [Fact]
    public void A_readable_choice_is_kept_as_picked()
    {
        var picked = Color.FromArgb(255, 0x7F, 0x22, 0xCE);
        var palette = AppearancePalette.Build(
            RuThemePalettes.StudioLight,
            new AppearanceColorSet(Accent: AppearanceColor.ToHex(picked)),
            isDark: false);

        Assert.Equal(picked, palette.Accent);
        Assert.Equal(RuThemePalettes.StudioLight.Canvas, palette.Canvas);
        Assert.Equal(RuThemePalettes.StudioLight.Ink, palette.Ink);
    }

    [Fact]
    public async Task Imported_media_is_copied_under_a_generated_name_and_old_files_are_pruned()
    {
        var media = new AppearanceMediaFiles(Path.Combine(_directory, "backgrounds"));
        Directory.CreateDirectory(_directory);
        var source = Path.Combine(_directory, "holiday.PNG");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);

        var first = await media.ImportAsync(source);
        var second = await media.ImportAsync(source);

        Assert.True(first.Succeeded);
        Assert.Equal(AppearanceBackgroundKind.Image, first.Kind);
        Assert.True(AppearanceMediaFiles.IsGeneratedName(first.FileName));
        Assert.NotNull(media.Resolve(first.FileName));
        Assert.Null(media.Resolve("holiday.png"));

        media.DeleteAllExcept(second.FileName);
        Assert.Null(media.Resolve(first.FileName));
        Assert.NotNull(media.Resolve(second.FileName));
    }

    [Fact]
    public async Task Unsupported_or_oversized_media_is_refused_with_a_reason()
    {
        var media = new AppearanceMediaFiles(Path.Combine(_directory, "backgrounds"));
        Directory.CreateDirectory(_directory);
        var script = Path.Combine(_directory, "run.ps1");
        await File.WriteAllTextAsync(script, "Write-Host hi");
        var huge = Path.Combine(_directory, "huge.png");
        await using (var stream = File.Create(huge)) stream.SetLength(AppearanceMediaFiles.MaximumImageBytes + 1);

        var refusedType = await media.ImportAsync(script);
        var refusedSize = await media.ImportAsync(huge);

        Assert.False(refusedType.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(refusedType.Error));
        Assert.False(refusedSize.Succeeded);
        Assert.Contains("MB", refusedSize.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(6, AppearanceCornerStyle.Default, false, 6)]
    [InlineData(12, AppearanceCornerStyle.Square, false, 2)]
    [InlineData(12, AppearanceCornerStyle.Round, false, 18)]
    [InlineData(3, AppearanceCornerStyle.Square, true, 3)]
    public void Corner_styles_scale_the_radius_tokens(double stock, AppearanceCornerStyle style, bool microPill, double expected)
    {
        Assert.Equal(expected, AppearanceResources.ScaleRadius(stock, style, microPill));
    }

    [Fact]
    public void The_reset_stays_reachable_from_the_command_palette_and_settings()
    {
        Assert.Contains("\"appearance-reset\"", GuiSource.Text("MainWindow.CommandPalette.cs"), StringComparison.Ordinal);
        var settings = GuiSource.Text("Views", "SettingsView.xaml");
        Assert.Contains("x:Name=\"PanelAppearanceScrollViewer\"", settings, StringComparison.Ordinal);
        Assert.Contains("OnAppearanceResetClick", settings, StringComparison.Ordinal);
    }
}
