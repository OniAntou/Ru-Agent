using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiActivityPresentationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(160)]
    public void LayoutStaysWithinViewportAndPreservesMetricsWhenSpaceAllows(int width)
    {
        var row = new TuiActivityPresentation("⠋", "Reading", "src/very/long/directory/AgentCoordinator.cs", "12.4s · 3 tools", TuiToken.Brand);
        var spans = row.Fit(width);
        var text = string.Concat(spans.Select(span => span.Text));
        Assert.True(TuiText.VisibleWidth(text) <= width);
        if (width >= 40) Assert.EndsWith("12.4s · 3 tools", text);
        if (width >= 80) Assert.Contains("AgentCoordinator.cs", text);
    }

    [Fact]
    public void PausedStatesHaveNoSpinnerOrElapsedClock()
    {
        var status = new TuiStatusLine(new TuiSurface(new StringWriter()));
        status.SetElapsed(35);
        foreach (var state in new[] { TuiRunState.WaitingApproval, TuiRunState.WaitingTrust, TuiRunState.Paused })
        {
            status.SetState(state);
            var presentation = status.Presentation(80);
            Assert.NotEqual(status.SpinnerFrame, presentation.Glyph);
            Assert.Empty(presentation.Metrics);
            Assert.Equal(TuiToken.Warn, presentation.Tone);
        }
    }

    [Fact]
    public void ResponseTextAndToolActivityHaveDifferentAccurateLabels()
    {
        var surface = new TuiSurface(new StringWriter());
        var status = new TuiStatusLine(surface);
        var transcript = new TuiTranscript(surface, status);
        status.SetState(TuiRunState.Thinking);
        Assert.Equal("Thinking", status.Presentation(80).Action);
        transcript.AppendAssistant("answer");
        Assert.Equal("Writing response", status.Presentation(80).Action);
        transcript.ResetAssistant();
        Assert.Equal("Thinking", status.Presentation(80).Action);
        status.SetState(TuiRunState.Tool);
        status.SetActivity("reading", "app.cs");
        Assert.Equal("Reading", status.Presentation(80).Action);
        Assert.Equal("app.cs", status.Presentation(80).Target);
        status.Reset();
        Assert.Equal("Ready", status.Presentation(80).Action);
        Assert.Null(status.Presentation(80).Target);
    }

    [Fact]
    public void LongFailedToolKeepsTheFailureVisible()
    {
        using var palette = TuiPalette.PinLevel(TuiColorLevel.None);
        var text = TuiToolRow.Compose(ToolLineStatus.Failed, "bash", new string('x', 200), "exit 1", 40);
        Assert.StartsWith("  ✗ Run", text);
        Assert.EndsWith("failed · exit 1", text);
        Assert.True(TuiText.VisibleWidth(text) <= 40);
    }

    [Fact]
    public void UntrustedLabelsCannotInjectTerminalControlSequences()
    {
        var row = new TuiActivityPresentation("⠋", "Read\u001b[2J", "a\nb\t.cs", "1s\r", TuiToken.Brand);
        var text = string.Concat(row.Fit(80).Select(span => span.Text));
        Assert.DoesNotContain('\u001b', text);
        Assert.DoesNotContain('\n', text);
        Assert.DoesNotContain('\r', text);
    }
}
