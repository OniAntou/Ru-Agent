using Xunit;

namespace RuApp.Tests;

public sealed class ScreenObservationViewContractTests
{
    [Fact]
    public void Screen_observation_keeps_controls_without_the_orange_notice_panel()
    {
        var source = File.ReadAllText(FindViewSource("SettingsView.xaml"));

        Assert.Contains("x:Name=\"ScreenObserverToggle\"", source, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GeminiVisionKeyBox\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Gemini Flash Vision Live Observer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Ru sẽ tự động quan sát màn hình", source, StringComparison.Ordinal);
    }

    private static string FindViewSource(string sourceName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var sourcePath = Path.Combine(
                directory.FullName,
                "gui-winui3",
                "Views",
                sourceName);
            if (File.Exists(sourcePath))
            {
                return sourcePath;
            }
        }

        throw new FileNotFoundException($"Could not locate Views/{sourceName}.");
    }
}
