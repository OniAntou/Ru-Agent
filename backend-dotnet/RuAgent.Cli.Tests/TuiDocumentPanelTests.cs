using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiDocumentPanelTests
{
    [Fact]
    public void Offset_ClampsSoTheTailStaysReachable()
    {
        // A document shorter than a page cannot scroll at all.
        Assert.Equal(0, TuiDocumentPanel.MaxOffset(3, 10));
        Assert.Equal(20, TuiDocumentPanel.MaxOffset(30, 10));

        Assert.Equal(0, TuiDocumentPanel.ClampOffset(-5, 30, 10));
        Assert.Equal(20, TuiDocumentPanel.ClampOffset(999, 30, 10));
        Assert.Equal(7, TuiDocumentPanel.ClampOffset(7, 30, 10));
    }

    [Fact]
    public void ReportCommands_AreTheReadOnlyOnes()
    {
        foreach (var command in new[] { "/tree", "/status", "/stats", "/tokens", "/help", "/export", "/attachments", "/history" })
        {
            Assert.True(InteractiveLineReader.IsReportCommand(command), command);
        }

        // Anything that changes state or asks a question stays on the composer and the
        // modal pickers, because those need a decision rather than a reading.
        foreach (var command in new[] { "/model", "/backend", "/key", "/reasoning", "/delete", "/clear", "/exit", "/attach", "/rewind", "/continue" })
        {
            Assert.False(InteractiveLineReader.IsReportCommand(command), command);
        }

        Assert.False(InteractiveLineReader.IsReportCommand(null));
        Assert.False(InteractiveLineReader.IsReportCommand("/nope"));
    }
}