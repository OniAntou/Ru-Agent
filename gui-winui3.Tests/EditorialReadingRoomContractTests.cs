using Xunit;

namespace RuApp.Tests;

public sealed class EditorialReadingRoomContractTests
{
    [Theory]
    [InlineData("RuCanvasBrush")]
    [InlineData("RuSidebarBrush")]
    [InlineData("RuSurfaceBrush")]
    [InlineData("RuInkBrush")]
    [InlineData("RuMutedBrush")]
    [InlineData("RuLineBrush")]
    [InlineData("RuAccentBrush")]
    [InlineData("RuAccentSoftBrush")]
    [InlineData("RuUiFontFamily")]
    [InlineData("RuDisplayFontFamily")]
    [InlineData("RuMonoFontFamily")]
    [InlineData("RuControlCornerRadius")]
    public void App_defines_each_editorial_resource(string resourceKey)
    {
        var source = File.ReadAllText(FindSource("App.xaml"));
        Assert.Contains($"x:Key=\"{resourceKey}\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void App_no_longer_contains_the_old_dark_default_palette()
    {
        var source = File.ReadAllText(FindSource("App.xaml"));

        foreach (var staleColor in new[] { "#0D0E12", "#12141B", "#171923", "#4F6BFF", "#6881FF", "#8FA2FF" })
        {
            Assert.DoesNotContain(staleColor, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Legacy_brush_keys_remain_available_for_untouched_views()
    {
        var source = File.ReadAllText(FindSource("App.xaml"));

        foreach (var legacyKey in new[]
        {
            "AppSurfaceBrush", "AppSidebarBackgroundBrush", "AppCardBackgroundBrush",
            "AppCardBackgroundHoverBrush", "AppCardBorderBrush", "AppTextPrimaryBrush",
            "AppTextSecondaryBrush", "AppTextTertiaryBrush", "AppAccentBrush",
            "AppAccentHoverBrush", "AppSuccessBrush", "AppWarningBrush", "AppDangerBrush"
        })
        {
            Assert.Contains($"x:Key=\"{legacyKey}\"", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void App_uses_the_single_six_pixel_control_radius()
    {
        var source = File.ReadAllText(FindSource("App.xaml"));

        Assert.Contains("<CornerRadius x:Key=\"AppCardCornerRadius\">6</CornerRadius>", source, StringComparison.Ordinal);
        Assert.Contains("<CornerRadius x:Key=\"AppControlCornerRadius\">6</CornerRadius>", source, StringComparison.Ordinal);
        Assert.Contains("<CornerRadius x:Key=\"AppPillCornerRadius\">6</CornerRadius>", source, StringComparison.Ordinal);
    }

    private static string FindSource(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "gui-winui3", fileName);
            if (File.Exists(path)) return path;
        }

        throw new FileNotFoundException(fileName);
    }
}
