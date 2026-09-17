using System.Xml.Linq;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Shell navigation behaviour. These assertions read the shipped XAML as XML and
/// exercise the routing policy, so a renamed attribute or a reordered element cannot
/// fail them while a real navigation change still does.
/// </summary>
public sealed class ChatShellUiContractTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Shell_side_panel_exposes_only_the_chat_and_settings_entries()
    {
        var named = NamedElements(LoadXaml("MainWindow.xaml"));

        Assert.Contains("NavChat", named);
        Assert.Contains("NavSettings", named);

        // The Chat entry is retired from view but stays wired for routing.
        Assert.Equal("Collapsed", named["NavChat"].Attribute("Visibility")?.Value);

        // The footer divider above Settings is gone.
        Assert.DoesNotContain("SidebarBottomDivider", named);

        // Removed surfaces must not come back as side-panel entries.
        Assert.DoesNotContain("NavVoice", named);
        Assert.DoesNotContain("NavTools", named);
        Assert.DoesNotContain("NavLog", named);
        Assert.DoesNotContain("NavDiagnostics", named);
        Assert.DoesNotContain("NavAgent", named);
        Assert.DoesNotContain("NavPolicy", named);
        Assert.DoesNotContain("NavAudit", named);
    }

    [Fact]
    public void Shell_side_panel_entries_carry_the_route_tag_the_policy_resolves()
    {
        var named = NamedElements(LoadXaml("MainWindow.xaml"));

        // The button tag is the routing input; if it stops matching, navigation
        // silently falls back to chat.
        Assert.Equal("chat", named["NavChat"].Attribute("Tag")?.Value);
        Assert.Equal("settings", named["NavSettings"].Attribute("Tag")?.Value);
    }

    [Theory]
    [InlineData("chat", ShellDestination.Chat)]
    [InlineData(null, ShellDestination.Chat)]
    [InlineData("", ShellDestination.Chat)]
    [InlineData("something-unknown", ShellDestination.Chat)]
    [InlineData("log", ShellDestination.Log)]
    [InlineData("diagnostics", ShellDestination.Diagnostics)]
    [InlineData("policy", ShellDestination.Policy)]
    [InlineData("audit", ShellDestination.Audit)]
    public void Route_policy_maps_tags_to_destinations(string? tag, ShellDestination expected)
    {
        Assert.Equal(expected, ShellRoutePolicy.Resolve(tag));
    }

    [Fact]
    public void Only_the_chat_surface_keeps_the_provider_catalog_polling()
    {
        Assert.True(ShellRoutePolicy.KeepsCatalogPolling("chat"));
        Assert.False(ShellRoutePolicy.KeepsCatalogPolling("log"));
        Assert.False(ShellRoutePolicy.KeepsCatalogPolling("diagnostics"));
        Assert.False(ShellRoutePolicy.KeepsCatalogPolling("policy"));
        Assert.False(ShellRoutePolicy.KeepsCatalogPolling("audit"));
    }

    [Fact]
    public void Active_view_flags_follow_the_resolved_destination()
    {
        Assert.True(ShellRoutePolicy.IsDiagnosticsActive("diagnostics"));
        Assert.False(ShellRoutePolicy.IsDiagnosticsActive("log"));
        Assert.True(ShellRoutePolicy.IsLogActive("log"));
        Assert.False(ShellRoutePolicy.IsLogActive("diagnostics"));
        Assert.False(ShellRoutePolicy.IsDiagnosticsActive("chat"));
        Assert.False(ShellRoutePolicy.IsLogActive("chat"));
    }

    private static Dictionary<string, XElement> NamedElements(XDocument document)
    {
        var named = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var element in document.Descendants())
        {
            if (element.Attribute(Xaml + "Name")?.Value is { Length: > 0 } name)
            {
                named[name] = element;
            }
        }

        return named;
    }

    private static XDocument LoadXaml(params string[] parts)
    {
        var path = FindSource(parts);
        return XDocument.Load(path);
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
