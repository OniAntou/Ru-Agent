using Xunit;

namespace RuApp.Tests;

public sealed class ResponsiveLayoutContractTests
{
    [Theory]
    [InlineData("MainWindow.xaml", "x:Name=\"RootLayoutGrid\"")]
    [InlineData("MainWindow.xaml", "SizeChanged=\"RootLayoutGrid_SizeChanged\"")]
    [InlineData("MainWindow.xaml", "VisualState x:Name=\"Wide\"")]
    [InlineData("MainWindow.xaml", "VisualState x:Name=\"Compact\"")]
    [InlineData("MainWindow.xaml", "VisualState x:Name=\"Narrow\"")]
    [InlineData("MainWindow.xaml", "AdaptiveTrigger MinWindowWidth=\"900\"")]
    [InlineData("MainWindow.xaml", "AdaptiveTrigger MinWindowWidth=\"1280\"")]
    [InlineData("MainWindow.xaml.cs", "ResponsiveLayoutPolicy.GetForWidth")]
    [InlineData("MainWindow.xaml.cs", "_lastExpandedSidebarWidth")]
    public void Main_shell_contains_the_responsive_boundary(string fileName, string expectedText)
    {
        Assert.Contains(expectedText, File.ReadAllText(FindSource(fileName)), StringComparison.Ordinal);
    }

    [Fact]
    public void Responsive_shell_does_not_add_a_horizontal_scroll_viewer()
    {
        var source = File.ReadAllText(FindSource("MainWindow.xaml"));
        Assert.DoesNotContain("HorizontalScrollBarVisibility=\"Auto\"", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ChatView.xaml", "x:Name=\"SuggestionPanel\"")]
    [InlineData("ChatView.xaml", "HorizontalScrollBarVisibility=\"Disabled\"")]
    [InlineData("ChatView.xaml", "VisualState x:Name=\"Narrow\"")]
    [InlineData("ChatView.xaml.cs", "ApplyInputWidth")]
    [InlineData("ChatView.xaml.cs", "RootGrid.ActualWidth")]
    public void Chat_preserves_vertical_flow_and_has_responsive_markers(string fileName, string marker)
    {
        Assert.Contains(marker, File.ReadAllText(FindSource(fileName)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("LogView.xaml", "VisualState x:Name=\"Narrow\"")]
    [InlineData("LogView.xaml", "HorizontalScrollBarVisibility=\"Disabled\"")]
    [InlineData("DiagnosticsView.xaml", "VisualState x:Name=\"Narrow\"")]
    [InlineData("DiagnosticsView.xaml", "x:Name=\"StatusMetricsGrid\"")]
    [InlineData("DiagnosticsView.xaml.cs", "ReflowStatusMetrics")]
    [InlineData("DiagnosticsView.xaml.cs", "Rows.ReplaceAll")]
    public void Log_and_diagnostics_keep_stable_vertical_data_surfaces(string fileName, string marker)
    {
        Assert.Contains(marker, File.ReadAllText(FindSource(fileName)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("LogView.xaml")]
    [InlineData("DiagnosticsView.xaml")]
    public void Log_and_diagnostics_never_enable_horizontal_scrolling(string fileName)
    {
        Assert.DoesNotContain(
            "HorizontalScrollBarVisibility=\"Auto\"",
            File.ReadAllText(FindSource(fileName)),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("VoiceView.xaml", "AdaptiveTrigger MinWindowWidth=\"900\"")]
    [InlineData("VoiceView.xaml", "AdaptiveTrigger MinWindowWidth=\"1280\"")]
    [InlineData("SettingsView.xaml", "AdaptiveTrigger MinWindowWidth=\"900\"")]
    [InlineData("SettingsView.xaml", "AdaptiveTrigger MinWindowWidth=\"1280\"")]
    [InlineData("VoiceView.xaml", "x:Name=\"VoiceProfileComboBox\"")]
    [InlineData("SettingsView.xaml", "x:Name=\"BackendComboBox\"")]
    [InlineData("SettingsView.xaml", "x:Name=\"GeminiVisionKeyBox\"")]
    public void Form_views_define_all_responsive_tiers(string fileName, string marker)
    {
        Assert.Contains(marker, File.ReadAllText(FindSource(fileName)), StringComparison.Ordinal);
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
                Path.Combine(directory.FullName, "gui-winui3", "Views", fileName)
            })
            {
                if (File.Exists(path)) return path;
            }
        }

        throw new FileNotFoundException(fileName);
    }
}
