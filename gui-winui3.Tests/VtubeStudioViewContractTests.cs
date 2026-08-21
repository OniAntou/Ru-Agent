using Xunit;

namespace RuApp.Tests;

public sealed class VtubeStudioViewContractTests
{
    [Fact]
    public void Vtube_studio_keeps_connection_and_motion_controls_without_manual_actions()
    {
        var xaml = File.ReadAllText(FindSource("SettingsView.xaml"));
        var code = File.ReadAllText(FindSource("SettingsView.xaml.cs"));

        Assert.Contains("x:Name=\"ConnectVtsButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SwaySlider\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"OverlayToggle\"", xaml, StringComparison.Ordinal);

        foreach (var manualActionMarker in new[]
        {
            "Kích hoạt thủ công biểu cảm",
            "VtsActionsPanel",
            "OnVtsActionClick",
            "Vui vẻ",
            "Ngạc nhiên",
            "Dịu dàng",
            "Giơ tay",
            "Dựng đuôi"
        })
        {
            Assert.DoesNotContain(manualActionMarker, xaml, StringComparison.Ordinal);
            Assert.DoesNotContain(manualActionMarker, code, StringComparison.Ordinal);
        }
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
