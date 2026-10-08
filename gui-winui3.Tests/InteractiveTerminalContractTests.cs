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
}
