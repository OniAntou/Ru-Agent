using System;
using System.IO;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The interactive terminal's vendored browser assets: xterm.js is shipped
/// inside the app (MIT), the shim page never reaches a CDN, and the scrollback
/// stays bounded.
/// </summary>
public sealed class InteractiveTerminalContractTests
{
    [Fact]
    public void The_terminal_ships_its_vendored_assets()
    {
        var root = GuiSource.Path("Assets", "terminal");
        foreach (var name in new[] { "xterm.js", "xterm.css", "addon-fit.js", "terminal.html", "LICENSE-xterm.txt" })
        {
            Assert.True(File.Exists(Path.Combine(root, name)), name + " is missing");
        }

        Assert.True(
            new FileInfo(Path.Combine(root, "xterm.js")).Length > 100_000,
            "xterm.js looks truncated");

        var html = File.ReadAllText(Path.Combine(root, "terminal.html"));
        Assert.DoesNotContain("https://", html, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", html, StringComparison.Ordinal);
        Assert.Contains("xterm.js", html, StringComparison.Ordinal);
        Assert.Contains("scrollback: 5000", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_terminal_control_hosts_the_vendored_page_offline()
    {
        var code = GuiSource.Text("Controls", "InteractiveTerminalControl.xaml.cs");
        Assert.Contains("EnsureCoreWebView2Async", code, StringComparison.Ordinal);
        Assert.Contains("SetVirtualHostNameToFolderMapping", code, StringComparison.Ordinal);
        Assert.Contains("terminal.html", code, StringComparison.Ordinal);
        Assert.Contains("powershell.exe", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Terminal_output_cannot_navigate_or_open_windows()
    {
        var html = File.ReadAllText(Path.Combine(GuiSource.Path("Assets", "terminal"), "terminal.html"));
        Assert.Contains("Content-Security-Policy", html, StringComparison.Ordinal);
        Assert.Contains("linkHandler", html, StringComparison.Ordinal);
        Assert.Contains("allowNonHttpProtocols: false", html, StringComparison.Ordinal);
        Assert.DoesNotContain("window.open", html, StringComparison.Ordinal);

        var code = GuiSource.Text("Controls", "InteractiveTerminalControl.xaml.cs");
        Assert.Contains("NavigationStarting += OnNavigationStarting", code, StringComparison.Ordinal);
        Assert.Contains("NewWindowRequested += OnNewWindowRequested", code, StringComparison.Ordinal);
        Assert.Contains("IsTerminalPage(args.Source)", code, StringComparison.Ordinal);
        Assert.Contains("AreDevToolsEnabled = false", code, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://example.com/a?b=c", true)]
    [InlineData("http://example.com", true)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ms-settings:privacy", false)]
    [InlineData("https://user:pass@example.com", false)]
    [InlineData("relative/path", false)]
    [InlineData("", false)]
    public void Only_http_links_leave_the_terminal(string value, bool expected) =>
        Assert.Equal(expected, RuApp.Controls.InteractiveTerminalControl.TryGetExternalLink(value, out _));
}
