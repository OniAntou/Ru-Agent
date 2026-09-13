using Xunit;

namespace RuApp.Tests;

public sealed class ChatShellUiContractTests
{
    [Fact]
    public void Shell_keeps_side_panel_clean_without_user_facing_tools_entry()
    {
        string xaml = File.ReadAllText(FindSource("MainWindow.xaml"));

        Assert.Contains("x:Name=\"NavChat\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NavSettings\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"NavVoice\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"NavTools\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"NavLog\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"NavDiagnostics\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"NavAgent\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"NavPolicy\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"NavAudit\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Shell_routes_tools_menu_items_without_a_second_agent_dashboard()
    {
        string code = File.ReadAllText(FindSource("MainWindow.xaml.cs"));

        Assert.Contains("OnToolsMenuItemClick", code, StringComparison.Ordinal);
        Assert.Contains("item.Tag is not string tag", code, StringComparison.Ordinal);
        Assert.Contains("_chatView.Initialize", code, StringComparison.Ordinal);
        Assert.Contains("_agentController", code, StringComparison.Ordinal);
        Assert.DoesNotContain("_agentControlPanel", code, StringComparison.Ordinal);
    }

    private static string FindSource(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string root = directory.FullName;
            if (File.Exists(Path.Combine(root, "gui-winui3", "MainWindow.xaml")))
            {
                return Path.Combine(new[] { root, "gui-winui3" }.Concat(parts).ToArray());
            }
        }

        throw new DirectoryNotFoundException("gui-winui3 source root was not found.");
    }
}
