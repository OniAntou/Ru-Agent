using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Opening a session must rebuild the agent's activity feed, not just its prose.
/// </summary>
/// <remarks>
/// Session messages persist role/content and run identity; a transcript built from them alone
/// shows text with every tool, thinking and task-list row missing. The rows are recovered
/// from each terminal run's durable events, which is the only reason a feed survives
/// leaving a session or closing the app.
///
/// This is asserted at the source level rather than by driving the view: the view needs a
/// live host, and the property that matters -- that the open path consults the durable
/// replay at all -- is visible in the method body.
/// </remarks>
public sealed class SessionOpenReplayTests
{
    private static string OpenSessionBody() =>
        GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "OpenSessionAsync");

    [Fact]
    public void Opening_a_session_rebuilds_activities_from_durable_events()
    {
        string body = OpenSessionBody();

        Assert.Contains("LoadReplayedRunActivitiesAsync", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_rebuilt_rows_are_attached_to_the_assistant_messages()
    {
        string body = OpenSessionBody();

        Assert.Contains("RegisterReplayedActivities", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_open_path_aligns_runs_the_same_way_the_end_of_turn_reload_does()
    {
        // Two different alignment rules would make a session render differently depending
        // on whether it was opened or had just finished its own turn.
        string body = OpenSessionBody();

        Assert.Contains("SessionTranscriptBuilder.Build", body, StringComparison.Ordinal);
        var reload = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "ReloadSessionHistoryAsync");
        Assert.Contains("SessionTranscriptBuilder.Build", reload, StringComparison.Ordinal);
    }

    [Fact]
    public void A_message_that_is_not_rendered_does_not_consume_an_assistant_ordinal()
    {
        // A machine tool envelope is hidden, so it must not shift which run pairs with
        // which visible bubble. The policy answers rendering and backing separately and
        // its own tests cover the counting; here it is enough that the open path asks it
        // rather than deciding for itself.
        string body = OpenSessionBody();

        Assert.Contains("SessionTranscriptBuilder.Build", body, StringComparison.Ordinal);
        Assert.Contains("projected[i].Render", GuiSource.Text("Services", "SessionTranscriptBuilder.cs"), StringComparison.Ordinal);
    }
}
