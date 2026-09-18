using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Pairing runs with assistant bubbles when a session is opened again.
/// </summary>
/// <remarks>
/// This is the rule that puts a turn's replayed rows back on the right bubble. It is
/// asserted as behaviour rather than as source text because the source-text version
/// passed while the alignment could still have been wrong.
/// </remarks>
public sealed class ReplayIndexPerAssistantTests
{
    private static bool[] Renders(params string[] roles) =>
        roles.Select(role => ReloadActivitiesPolicy.RendersAsAssistant(
            role,
            "text",
            _ => false)).ToArray();

    [Fact]
    public void Each_bubble_gets_its_own_run_when_the_counts_match()
    {
        // Three turns, three runs, oldest first: bubble 0 -> run 0, and so on.
        var indices = ReloadActivitiesPolicy.PlaceReplayedRuns(
            3,
            Renders("user", "assistant", "user", "assistant", "user", "assistant"));

        Assert.Equal(new[] { -1, 0, -1, 1, -1, 2 }, indices.Select(p => p.ReplayIndex));
    }

    [Fact]
    public void User_messages_never_receive_a_run()
    {
        var indices = ReloadActivitiesPolicy.PlaceReplayedRuns(
            2,
            Renders("user", "assistant", "user", "assistant"));

        Assert.Equal(-1, indices[0].ReplayIndex);
        Assert.Equal(-1, indices[2].ReplayIndex);
    }

    [Fact]
    public void A_turn_with_no_persisted_text_shifts_the_head_not_the_tail()
    {
        // Four runs but only two visible assistant bubbles: a denied or failed turn left
        // no text. The newest bubbles must still line up with the newest runs, because
        // that is where the user is looking.
        var indices = ReloadActivitiesPolicy.PlaceReplayedRuns(
            4,
            Renders("user", "assistant", "user", "assistant"));

        Assert.Equal(new[] { -1, 2, -1, 3 }, indices.Select(p => p.ReplayIndex));
    }

    [Fact]
    public void A_hidden_envelope_does_not_consume_a_run()
    {
        // A machine tool envelope is persisted as an assistant message but never renders.
        // Counting it would shift every later bubble onto the wrong run.
        var flags = new[]
        {
            ReloadActivitiesPolicy.RendersAsAssistant("user", "hi", _ => false),
            ReloadActivitiesPolicy.RendersAsAssistant("assistant", "envelope", _ => true),
            ReloadActivitiesPolicy.RendersAsAssistant("assistant", "real reply", _ => false),
        };

        var indices = ReloadActivitiesPolicy.PlaceReplayedRuns(1, flags);

        Assert.Equal(new[] { -1, -1, 0 }, indices.Select(p => p.ReplayIndex));
    }

    [Fact]
    public void No_run_backs_a_bubble_when_history_has_more_turns_than_runs()
    {
        // Older turns predate the durable replay window, so they render without rows
        // rather than borrowing a newer run's rows.
        var indices = ReloadActivitiesPolicy.PlaceReplayedRuns(
            1,
            Renders("user", "assistant", "user", "assistant"));

        Assert.Equal(new[] { -1, -1, -1, 0 }, indices.Select(p => p.ReplayIndex));
    }

    [Fact]
    public void An_empty_history_yields_no_indices()
    {
        Assert.Empty(ReloadActivitiesPolicy.PlaceReplayedRuns(3, Array.Empty<bool>()));
    }

    [Fact]
    public void Rendering_and_backing_are_answered_separately()
    {
        // The bug this replaced derived "renders" from the replay index, which is wrong in
        // both directions. A bubble may render with no run behind it, and a hidden envelope
        // must not claim a run even when one is available.
        var flags = new[]
        {
            ReloadActivitiesPolicy.RendersAsAssistant("assistant", "envelope", _ => true),
            ReloadActivitiesPolicy.RendersAsAssistant("assistant", "older turn", _ => false),
        };

        var placements = ReloadActivitiesPolicy.PlaceReplayedRuns(2, flags);

        Assert.False(placements[0].Renders);
        Assert.Equal(-1, placements[0].ReplayIndex);

        Assert.True(placements[1].Renders);
        Assert.Equal(1, placements[1].ReplayIndex);
    }

    [Fact]
    public void A_rendered_bubble_with_no_run_still_renders()
    {
        // History is longer than the replay window. The older bubble has no rows to show
        // but must still appear, or the transcript loses its text.
        var placements = ReloadActivitiesPolicy.PlaceReplayedRuns(
            1,
            Renders("assistant", "assistant"));

        Assert.True(placements[0].Renders);
        Assert.Equal(-1, placements[0].ReplayIndex);
    }
}
