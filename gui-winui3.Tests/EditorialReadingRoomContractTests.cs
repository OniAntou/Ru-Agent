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

    [Theory]
    [InlineData("RuCanvasColor", "#E7E9E3")]
    [InlineData("RuSidebarColor", "#DCE1D9")]
    [InlineData("RuSurfaceColor", "#F0F2EC")]
    [InlineData("RuInkColor", "#20231F")]
    [InlineData("RuMutedColor", "#5F685F")]
    [InlineData("RuLineColor", "#C9CEC5")]
    [InlineData("RuAccentColor", "#A84B38")]
    [InlineData("RuAccentSoftColor", "#EEDFD9")]
    public void App_uses_the_muted_mineral_paper_palette(string resourceKey, string expectedColor)
    {
        var source = File.ReadAllText(FindSource("App.xaml"));

        Assert.Contains($"<Color x:Key=\"{resourceKey}\">{expectedColor}</Color>", source, StringComparison.Ordinal);
    }

    [Fact]
    public void App_requests_light_theme_for_default_control_contrast()
    {
        var source = File.ReadAllText(FindSource("App.xaml"));

        Assert.Contains("RequestedTheme=\"Light\"", source, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("MainWindow.xaml", "x:Name=\"ShellStatusRow\"")]
    [InlineData("MainWindow.xaml", "Background=\"{StaticResource RuSidebarBrush}\"")]
    [InlineData("MainWindow.xaml", "ToolTipService.ToolTip=\"Đoạn chat mới\"")]
    [InlineData("MainWindow.xaml", "AutomationProperties.Name=\"Trò chuyện\"")]
    [InlineData("MainWindow.xaml.cs", "RuAccentBrush")]
    [InlineData("MainWindow.xaml.cs", "activeBtn.BorderThickness = new Thickness(2, 0, 0, 0)")]
    [InlineData("MainWindow.xaml.cs", "UpdateShellStatusTooltip")]
    public void Shell_exposes_the_editorial_navigation_contract(string fileName, string marker)
    {
        Assert.Contains(marker, File.ReadAllText(FindSource(fileName)), StringComparison.Ordinal);
    }

    [Fact]
    public void Shell_does_not_render_the_old_status_badge_containers()
    {
        var source = File.ReadAllText(FindSource("MainWindow.xaml"));

        Assert.DoesNotContain("x:Name=\"StatusBackendBadge\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"StatusWorkerBadge\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"StatusVtsBadge\"", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ChatView.xaml", "x:Name=\"ReadingColumn\"")]
    [InlineData("ChatView.xaml", "x:Name=\"WelcomeIntro\"")]
    [InlineData("ChatView.xaml", "Background=\"{StaticResource RuCanvasBrush}\"")]
    [InlineData("ChatView.xaml", "Text=\"Viết cho Ru...\"")]
    [InlineData("ChatView.xaml.cs", "var promptText = button.Tag as string")]
    [InlineData("ChatView.xaml.cs", "ReadingColumnMaxWidth = 820")]
    public void Chat_exposes_the_reading_room_contract(string fileName, string marker)
    {
        Assert.Contains(marker, File.ReadAllText(FindSource(fileName)), StringComparison.Ordinal);
    }

    [Fact]
    public void Chat_removes_the_old_bubble_and_capsule_markers()
    {
        var source = File.ReadAllText(FindSource("ChatView.xaml"));

        Assert.DoesNotContain("CornerRadius=\"26\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CornerRadius=\"18\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Ru đang ở đây cùng anh nè ~\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Dot1Storyboard", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Dot2Storyboard", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Dot3Storyboard", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MarkdownBlock.cs", "new FontFamily(\"Segoe UI Variable\")")]
    [InlineData("MarkdownBlock.cs", "new FontFamily(\"Cambria\")")]
    [InlineData("MarkdownBlock.cs", "new FontFamily(\"Consolas\")")]
    [InlineData("MarkdownBlock.cs", "RegisterPropertyChangedCallback")]
    public void Markdown_renderer_has_explicit_editorial_typography(string fileName, string marker)
    {
        Assert.Contains(marker, File.ReadAllText(FindSource(fileName)), StringComparison.Ordinal);
    }

    [Fact]
    public void Welcome_content_is_left_anchored_inside_the_reading_column()
    {
        var source = File.ReadAllText(FindSource("ChatView.xaml")).Replace("\r\n", "\n");

        Assert.Contains(
            "x:Name=\"WelcomeHeroPanel\"\n                        MaxWidth=\"820\"\n                        HorizontalAlignment=\"Stretch\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"WelcomeIntro\"\n                           Text=\"Chào anh. Hôm nay mình bắt đầu từ đâu?\"\n                           FontFamily=\"{StaticResource RuDisplayFontFamily}\"\n                           FontSize=\"28\"\n                           Foreground=\"{StaticResource RuInkBrush}\"\n                           HorizontalAlignment=\"Left\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains("Text=\"Hỏi về một việc đang làm\"", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"Nói cho Ru nghe\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Chat_input_has_an_editorial_placeholder_brush()
    {
        var source = File.ReadAllText(FindSource("ChatView.xaml"));

        Assert.Contains(
            "x:Key=\"TextControlPlaceholderForeground\" Color=\"{StaticResource RuMutedColor}\"",
            source,
            StringComparison.Ordinal);
    }

    private static string FindSource(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            foreach (var path in new[]
            {
                Path.Combine(directory.FullName, "gui-winui3", fileName),
                Path.Combine(directory.FullName, "gui-winui3", "Views", fileName),
                Path.Combine(directory.FullName, "gui-winui3", "Controls", fileName)
            })
            {
                if (File.Exists(path)) return path;
            }
        }

        throw new FileNotFoundException(fileName);
    }
}
