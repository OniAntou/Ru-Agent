using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Session history projection: what the transcript renders from a persisted history,
/// which rows are machine envelopes, and where the live activity rows may attach.
/// </summary>
public sealed class SessionHistoryProjectionTests
{
    [Fact]
    public void Assistant_text_has_product_markers_removed()
    {
        var rows = SessionHistoryProjection.Project(
        [
            ("assistant", "Hello [[VTS:joy]] there"),
            ("assistant", "Pace [[VOICE_PACE:fast]] is fine"),
        ]);

        Assert.All(rows, row => Assert.DoesNotContain("[[", row.Content, StringComparison.Ordinal));
        Assert.Equal("Hello there", rows[0].Content);
        Assert.Equal("Pace is fine", rows[1].Content);
    }

    [Fact]
    public void A_user_turn_is_shown_exactly_as_stored()
    {
        // A user may legitimately type the marker shape; only assistant output was
        // ever annotated with it.
        var rows = SessionHistoryProjection.Project(
        [
            ("user", "what does [[VTS:joy]] mean?"),
        ]);

        Assert.Equal("what does [[VTS:joy]] mean?", rows[0].Content);
        Assert.True(rows[0].Render);
    }

    [Fact]
    public void A_machine_tool_envelope_is_never_rendered_as_chat()
    {
        // A repeated tool call that failed the run is persisted but must not appear
        // as prose. The parser recognises the in-band selection envelope.
        var envelope = """
            <tool_call>{"tool":"read","arguments":{"path":"README.md"}}</tool_call>
            """;

        var rows = SessionHistoryProjection.Project(
        [
            ("user", "read the readme"),
            ("assistant", envelope),
            ("assistant", "Here is what I found."),
        ]);

        Assert.True(rows[0].Render);
        Assert.False(rows[1].Render);
        Assert.True(rows[2].Render);
    }

    [Fact]
    public void A_user_turn_that_looks_like_an_envelope_is_still_rendered()
    {
        // The envelope rule is scoped to assistant rows, so a pasted JSON question
        // from the user keeps its bubble.
        var json = """
            {"tool":"read","arguments":{"path":"README.md"}}
            """;

        var rows = SessionHistoryProjection.Project([("user", json)]);

        Assert.True(rows[0].Render);
    }

    [Fact]
    public void Every_row_carries_its_original_role()
    {
        var rows = SessionHistoryProjection.Project(
        [
            ("user", "one"),
            ("assistant", "two"),
            ("user", "three"),
        ]);

        Assert.Equal(new[] { "user", "assistant", "user" }, rows.Select(row => row.Role));
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public void An_empty_history_projects_to_nothing()
    {
        Assert.Empty(SessionHistoryProjection.Project([]));
    }

    [Fact]
    public void A_null_content_becomes_an_empty_string_instead_of_throwing()
    {
        var rows = SessionHistoryProjection.Project([("assistant", null!)]);

        Assert.Single(rows);
        Assert.Equal(string.Empty, rows[0].Content);
    }

    [Theory]
    [InlineData(new[] { "user", "assistant" }, 1)]
    [InlineData(new[] { "assistant", "user" }, 0)]
    [InlineData(new[] { "user", "assistant", "user" }, 1)]
    [InlineData(new[] { "assistant", "assistant" }, 1)]
    [InlineData(new[] { "user", "user" }, -1)]
    [InlineData(new string[0], -1)]
    public void The_last_assistant_index_matches_the_raw_history(string[] roles, int expected)
    {
        var history = roles.Select(role => (role, "content")).ToList();

        Assert.Equal(expected, SessionHistoryProjection.LastAssistantIndex(history));
    }

    [Fact]
    public void Live_rows_ride_on_a_final_persisted_assistant()
    {
        // history = [user, assistant], that assistant is last, and there are live rows.
        Assert.True(SessionHistoryProjection.AttachLiveToLastAssistant(
            historyCount: 2,
            lastAssistantIndex: 1,
            liveActivityCount: 3));
    }

    [Fact]
    public void Live_rows_do_not_ride_when_the_turn_has_no_persisted_assistant()
    {
        // denial or failure: the user turn is the only persisted row, so the live
        // rows need their own bubble to stay visible.
        Assert.False(SessionHistoryProjection.AttachLiveToLastAssistant(
            historyCount: 1,
            lastAssistantIndex: -1,
            liveActivityCount: 3));
    }

    [Fact]
    public void Live_rows_do_not_ride_when_the_user_spoke_after_the_assistant()
    {
        Assert.False(SessionHistoryProjection.AttachLiveToLastAssistant(
            historyCount: 3,
            lastAssistantIndex: 1,
            liveActivityCount: 3));
    }

    [Fact]
    public void No_live_rows_means_nothing_to_attach()
    {
        Assert.False(SessionHistoryProjection.AttachLiveToLastAssistant(
            historyCount: 2,
            lastAssistantIndex: 1,
            liveActivityCount: 0));
    }

    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData("  padded  ", "padded")]
    [InlineData("marker [[VTS:joy]] here", "marker here")]
    [InlineData("[[VTS:a]][[VTS:b]]only markers", "only markers")]
    [InlineData("[[OTHER:keep]]", "[[OTHER:keep]]")]
    public void Display_marker_stripping_is_scoped_to_the_known_markers(string input, string expected)
    {
        Assert.Equal(expected, ChatDisplayMarkers.Strip(input));
    }

    [Fact]
    public void Stripping_an_empty_value_yields_an_empty_string()
    {
        Assert.Equal(string.Empty, ChatDisplayMarkers.Strip(null));
        Assert.Equal(string.Empty, ChatDisplayMarkers.Strip(string.Empty));
    }
}
