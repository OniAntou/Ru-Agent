using RuAgent.Cli.Tui;
using Spectre.Console;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class GrokHeaderTests
{
    [Fact]
    public void GrokHeader_Renders_Session_Workspace_Model()
    {
        AnsiTheme.IsEnabled = true;
        try
        {
            using var writer = new StringWriter();
            var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer) });
            GrokHeader.Render(console, "sess_12345678", "D:\\repo", "Kilo Gateway", "grok-4.5", "standard");
            var output = writer.ToString();

            Assert.Contains("RuAgent", output, StringComparison.Ordinal);
            Assert.Contains("sess_123", output, StringComparison.Ordinal);
            Assert.Contains("D:\\repo", output, StringComparison.Ordinal);
            Assert.Contains("grok-4.5", output, StringComparison.Ordinal);
            Assert.Contains("/model", output, StringComparison.Ordinal);
            Assert.Contains("/exit", output, StringComparison.Ordinal);
            Assert.Contains("╭", output, StringComparison.Ordinal);
        }
        finally
        {
            AnsiTheme.IsEnabled = false;
        }
    }

    [Fact]
    public void GrokHeader_FallsBack_To_PlainText_When_Theme_Disabled()
    {
        AnsiTheme.IsEnabled = false;
        try
        {
            using var writer = new StringWriter();
            var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer) });
            GrokHeader.Render(console, "sess_1", "D:\\repo", "Kilo Gateway", "m", "standard");
            Assert.Contains("sess_1", writer.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            AnsiTheme.IsEnabled = true;
        }
    }
}
