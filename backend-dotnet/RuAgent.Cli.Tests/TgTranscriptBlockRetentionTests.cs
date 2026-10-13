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
    public void New_tool_details_evict_older_history_when_the_retention_budget_is_full(bool characterBudget)
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
        transcript.AppendCollapsed("new summary", ["NEW_DETAIL"], TranscriptStyle.Tool);
        transcript.MoveBlockSelection(-1);

        Assert.Equal("NEW_DETAIL", transcript.CopyText());
        Assert.True(transcript.IsHistoryTruncated);
        Assert.InRange(transcript.RetainedRowCount, 0, TgTranscriptView.MaxRetainedRows);
        Assert.InRange(transcript.RetainedCharacterCount, 0, TgTranscriptView.MaxRetainedCharacters);
    }

    [Fact]
    public void AppendingAnotherCollapsedToolBlockRetainsBothBlocksWithinTheTranscriptBudget()
    {
        using var transcript = new TgTranscriptView
        {
            Frame = new System.Drawing.Rectangle(0, 0, 80, 10),
        };
        const string firstSummary = "first tool summary";
        const string firstDetail = "FIRST_TOOL_DETAIL";
        const string secondSummary = "second tool summary";
        const string secondDetail = "SECOND_TOOL_DETAIL";

        transcript.AppendCollapsed(firstSummary, [firstDetail], TranscriptStyle.Tool);
        transcript.AppendCollapsed(secondSummary, [secondDetail], TranscriptStyle.Tool);

        Assert.Equal(4, transcript.RetainedRowCount);
        Assert.Equal(firstSummary.Length + firstDetail.Length + secondSummary.Length + secondDetail.Length,
            transcript.RetainedCharacterCount);
    }

    [Fact]
    public void BlockSelectionMovesBackwardAndCopyFallsBackToLatestAssistantTextWhenCleared()
    {
        using var transcript = new TgTranscriptView
        {
            Frame = new System.Drawing.Rectangle(0, 0, 80, 10),
        };
        const string assistantCode = "**assistant answer**\n```csharp\nreturn 1;\n```";
        transcript.Append(assistantCode, TranscriptStyle.Prose);
        transcript.AppendCollapsed("first summary", ["FIRST_TOOL_DETAIL"], TranscriptStyle.Tool);
        transcript.AppendCollapsed("second summary", ["SECOND_TOOL_DETAIL"], TranscriptStyle.Tool);

        transcript.MoveBlockSelection(-1);
        Assert.Equal("SECOND_TOOL_DETAIL", transcript.CopyText());
        transcript.MoveBlockSelection(-1);
        Assert.Equal("FIRST_TOOL_DETAIL", transcript.CopyText());
        Assert.False(transcript.IsFollowingTail);

        transcript.ClearBlockSelection();
        Assert.Equal(assistantCode, transcript.CopyText());
    }

    [Fact]
    public void DroppingRowsSinceMarkAlsoDropsBlocksOwnedByThoseRows()
    {
        using var transcript = new TgTranscriptView
        {
            Frame = new System.Drawing.Rectangle(0, 0, 80, 10),
        };
        transcript.AppendCollapsed("kept summary", ["KEPT_DETAIL"], TranscriptStyle.Tool);
        var mark = transcript.CommittedRowCount;
        transcript.AppendCollapsed("retried summary", ["RETRIED_DETAIL"], TranscriptStyle.Tool);

        transcript.DropRowsSince(mark);

        transcript.MoveBlockSelection(-1);
        Assert.Equal("KEPT_DETAIL", transcript.CopyText());
        Assert.Equal(2, transcript.RetainedRowCount);
        Assert.Equal("kept summary".Length + "KEPT_DETAIL".Length, transcript.RetainedCharacterCount);
    }

    [Fact]
    public void ExpandingOneOfSeveralBlocksDoesNotRevealTheOtherBlocks()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = Terminal.Gui.Drawing.LineStyle.None };
        var transcript = new TgTranscriptView { Width = 80, Height = 10 };
        window.Add(transcript);
        transcript.AppendCollapsed("first summary", ["FIRST_PRIVATE_DETAIL"], TranscriptStyle.Tool);
        transcript.AppendCollapsed("second summary", ["SECOND_PRIVATE_DETAIL"], TranscriptStyle.Tool);
        transcript.MoveBlockSelection(-1);
        transcript.MoveBlockSelection(-1);
        transcript.ToggleExpanded();

        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            var text = string.Join('\n', TgSnapshot.Capture(app));
            Assert.Contains("SECOND_PRIVATE_DETAIL", text);
            Assert.DoesNotContain("FIRST_PRIVATE_DETAIL", text);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
