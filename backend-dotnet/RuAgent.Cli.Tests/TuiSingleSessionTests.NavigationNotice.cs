using RuAgent.Cli.TerminalGui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed partial class TuiSingleSessionTests
{
    [Theory]
    [InlineData(false, true, "Giao diện mới", "Giao diện mới")]
    [InlineData(true, true, "Giao diện mới", "Giao diện mới")]
    [InlineData(false, true, null, "Untitled session")]
    [InlineData(true, true, "   ", "Untitled session")]
    [InlineData(true, false, "Empty chat", "Empty chat")]
    [InlineData(false, true, "Đọc \u001b[31mảnh\u001b[0m\nmới", "Đọc ảnh mới")]
    public async Task Session_switch_shows_the_title_in_feedback_without_replay_metadata_in_the_transcript(
        bool durableReplay, bool hasHistory, string? title, string displayTitle)
    {
        using var host = new SingleHost
        {
            SavedTitle = title, ReadTurnPage = durableReplay,
            SavedMessages = hasHistory ?
                [new { role = "user", content = "Earlier request" }, new { role = "assistant", content = "Earlier answer" }]
                : Array.Empty<object>(),
        };
        var stage = 0;
        await Run(host, (app, page) =>
        {
            if (stage == 0) { Send(app, page, "/sessions id:session_2"); stage++; return true; }
            var rows = page.Transcript.Lines.Select(row => row.Text).ToArray();
            if (page.Hints.FeedbackMessage is null && !rows.Any(line => line.StartsWith("switched to session", StringComparison.Ordinal))) return true;
            Assert.Equal($"switch to session \"{displayTitle}\"", page.Hints.FeedbackMessage);
            Assert.DoesNotContain(rows, line => line.Contains("replayed the last", StringComparison.Ordinal));
            Assert.DoesNotContain(rows, line => line.Contains("switch to session", StringComparison.Ordinal)
                || line.Contains("switched to session", StringComparison.Ordinal) || line.Contains("session_2", StringComparison.Ordinal));
            if (hasHistory)
            {
                Assert.Single(rows, line => line.Contains("Earlier request", StringComparison.Ordinal));
                Assert.Single(rows, line => line == "Earlier answer");
            }
            else Assert.True(page.Landing.Visible);
            Assert.False(page.Hints.FeedbackIsError); Assert.Equal(0, host.CreatedSessions);
            Send(app, page, "/exit"); stage++; return false;
        });
        Assert.Equal(2, stage);
    }
}
