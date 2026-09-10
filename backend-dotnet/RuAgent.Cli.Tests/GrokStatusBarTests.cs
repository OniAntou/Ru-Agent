using RuAgent.Cli.Tui;
using Spectre.Console;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class GrokStatusBarTests
{
    [Fact]
    public void StatusBar_Shows_Turn_And_Total_Tokens()
    {
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer) });
        GrokStatusBar.Render(console, "D:\\repo", "standard", 3, 1500);
        var output = writer.ToString();

        Assert.Contains("turn 3", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1500", output, StringComparison.Ordinal);
        Assert.DoesNotContain("grok-4.5", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Kilo Gateway", output, StringComparison.Ordinal);
        Assert.DoesNotContain("─", output, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusBar_Hides_Token_Part_When_Null()
    {
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer) });
        GrokStatusBar.Render(console, "D:\\repo", "standard", 1, null);
        var output = writer.ToString();

        Assert.Contains("turn 1", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("⇣", output, StringComparison.Ordinal);
    }
}
