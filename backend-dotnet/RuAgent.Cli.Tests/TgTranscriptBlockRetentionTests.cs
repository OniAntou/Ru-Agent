using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Views;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgTranscriptBlockRetentionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void New_thinking_details_evict_older_history_when_the_retention_budget_is_full(bool characterBudget)
    {
        using var transcript = new TgTranscriptView();
        if (characterBudget)
        {
            for (var index = 0; index < 12; index++) transcript.Append(new string('x', 16_000), TranscriptStyle.Prose);
            transcript.Append(new string('x', 8_000), TranscriptStyle.Prose);
        }
        else
        {
            for (var index = 0; index < TgTranscriptView.MaxRetainedRows; index++)
                transcript.Append("older", TranscriptStyle.Prose);
        }
        transcript.SetCollapsedDetails(["NEW_DETAIL"], TranscriptStyle.Meta);
        transcript.MoveBlockSelection(-1);

        Assert.Equal("NEW_DETAIL", transcript.CopyText());
        Assert.True(transcript.IsHistoryTruncated);
        Assert.InRange(transcript.RetainedRowCount, 0, TgTranscriptView.MaxRetainedRows);
        Assert.InRange(transcript.RetainedCharacterCount, 0, TgTranscriptView.MaxRetainedCharacters);
    }

    [Fact]
    public void ReplacingThinkingDetailsKeepsOnlyTheLatestBlockWithinBudget()
    {
        using var transcript = new TgTranscriptView
        {
            Frame = new System.Drawing.Rectangle(0, 0, 80, 10),
        };

        transcript.SetCollapsedDetails(["FIRST_DETAIL"], TranscriptStyle.Meta);
        transcript.SetCollapsedDetails(["SECOND_DETAIL"], TranscriptStyle.Meta);

        Assert.Equal(1, transcript.RetainedRowCount);
        Assert.Equal("SECOND_DETAIL".Length, transcript.RetainedCharacterCount);
        transcript.MoveBlockSelection(-1);
        Assert.Equal("SECOND_DETAIL", transcript.CopyText());
    }

    [Fact]
    public void BlockSelectionAndCopyFallBackToLatestAssistantTextWhenCleared()
    {
        using var transcript = new TgTranscriptView
        {
            Frame = new System.Drawing.Rectangle(0, 0, 80, 10),
        };
        const string assistantCode = "**assistant answer**\n```csharp\nreturn 1;\n```";
        transcript.Append(assistantCode, TranscriptStyle.Prose);
        transcript.SetCollapsedDetails(["THINKING_DETAIL"], TranscriptStyle.Meta);

        transcript.MoveBlockSelection(-1);
        Assert.Equal("THINKING_DETAIL", transcript.CopyText());
        Assert.False(transcript.IsFollowingTail);

        transcript.ClearBlockSelection();
        Assert.Equal(assistantCode, transcript.CopyText());
    }

    [Fact]
    public void DroppingRowsSinceMarkAlsoDropsThinkingBlocksOwnedByThoseRows()
    {
        using var transcript = new TgTranscriptView
        {
            Frame = new System.Drawing.Rectangle(0, 0, 80, 10),
        };
        transcript.Append("assistant answer", TranscriptStyle.Prose);
        var mark = transcript.CommittedRowCount;
        transcript.SetCollapsedDetails(["RETRIED_DETAIL"], TranscriptStyle.Meta);

        transcript.DropRowsSince(mark);

        transcript.MoveBlockSelection(-1);
        Assert.Equal("assistant answer", transcript.CopyText());
        Assert.Empty(transcript.CollapsedRows);
        Assert.Equal(1, transcript.RetainedRowCount);
    }

    [Fact]
    public void ExpandingThinkingDoesNotRevealAnOldToolSummaryBlock()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = Terminal.Gui.Drawing.LineStyle.None };
        var transcript = new TgTranscriptView { Width = 80, Height = 10 };
        window.Add(transcript);
        transcript.AppendCollapsed("tool summary", ["TOOL_PRIVATE_DETAIL"], TranscriptStyle.Tool);
        transcript.SetCollapsedDetails(["REASONING_PRIVATE_DETAIL"], TranscriptStyle.Meta);
        transcript.MoveBlockSelection(-1);

        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            var text = string.Join('\n', TgSnapshot.Capture(app));
            Assert.Contains("REASONING_PRIVATE_DETAIL", text);
            Assert.DoesNotContain("TOOL_PRIVATE_DETAIL", text);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
