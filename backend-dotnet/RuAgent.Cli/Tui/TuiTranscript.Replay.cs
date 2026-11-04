using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli.Tui;

public sealed partial class TuiTranscript
{
    /// <summary>
    /// Draws a finished turn from its durable record through the same rows a live turn
    /// produced: narration and activity in transcript order, then the footer with the
    /// run's real duration. Reopening a session used to print only the user and reply
    /// texts, one block per turn, so history never looked like the turn had.
    /// </summary>
    public void ReplayTurn(
        AgentRunActivityReplay? replay,
        IReadOnlyList<string> replies,
        DateTimeOffset? started,
        DateTimeOffset? ended,
        TuiTurnOutcome outcome,
        string? errorCode)
    {
        ArgumentNullException.ThrowIfNull(replies);
        var activities = new Dictionary<string, AgentActivityModel>(StringComparer.Ordinal);
        foreach (var activity in replay?.Activities ?? []) activities.TryAdd(activity.ActivityId, activity);

        // Same rules as the Desktop's AgentReplaySegmentBuilder: transport chunks are not
        // paragraph boundaries, so consecutive text streams on like live deltas and only
        // an activity ends the paragraph; the persisted reply is added when the
        // best-effort transcript did not already carry it.
        var content = new System.Text.StringBuilder();
        var paragraph = new System.Text.StringBuilder();
        var lastParagraph = string.Empty;
        var sequence = 0L;
        void EndParagraph()
        {
            if (paragraph.Length == 0) return;
            AppendAssistant("\n\n");
            lastParagraph = paragraph.ToString().Trim();
            paragraph.Clear();
        }
        if (replay is { Transcript.Count: > 0 })
        {
            foreach (var entry in replay.Transcript.OrderBy(item => item.Sequence))
            {
                if (entry.Kind == "text" && !string.IsNullOrEmpty(entry.Text))
                {
                    AppendAssistant(entry.Text);
                    paragraph.Append(entry.Text);
                    content.Append(entry.Text);
                }
                else if (entry.ActivityId is { } id && activities.TryGetValue(id, out var activity))
                {
                    EndParagraph();
                    ReplayActivity(activity, ++sequence);
                }
            }
        }
        else
        {
            // A Host older than the narration transcript returns rows only; the reply
            // then follows them, which is where a live reply ended.
            foreach (var activity in activities.Values) ReplayActivity(activity, ++sequence);
        }
        EndParagraph();

        var final = replies.LastOrDefault(text => !string.IsNullOrWhiteSpace(text))?.Trim();
        if (final is not null && final != lastParagraph && final != content.ToString().Trim())
        {
            AppendAssistant(final);
            AppendAssistant("\n\n");
        }
        Flush();
        if (started is { } start && ended is { } end) StartedAt(start, end);
        EndTurn(outcome, errorCode);
    }

    /// <summary>
    /// A live tool row is announced, then settled by its durable completion. The record
    /// keeps only the final state, so replay walks the same two steps.
    /// </summary>
    private void ReplayActivity(AgentActivityModel activity, long sequence)
    {
        var settles = activity.State is AgentActivityState.Completed or AgentActivityState.Failed
            && (activity.Kind is AgentActivityKind.Explore or AgentActivityKind.Search or AgentActivityKind.Analyze
                    or AgentActivityKind.Edit or AgentActivityKind.Terminal or AgentActivityKind.Web or AgentActivityKind.Mcp
                    or AgentActivityKind.Computer or AgentActivityKind.Skill or AgentActivityKind.Program
                || activity.Kind is AgentActivityKind.Status or AgentActivityKind.Todo && !string.IsNullOrWhiteSpace(activity.ToolDisplay));
        if (!settles)
        {
            ApplyActivity(activity);
            return;
        }

        ApplyActivity(activity with { State = AgentActivityState.Started });
        ApplyActivity(activity);
        ApplyDurable(new HarnessSafeEventEnvelope(1, "replay_" + sequence, string.Empty, string.Empty, sequence,
            "tool.execution_completed", string.Empty,
            activity.State == AgentActivityState.Failed ? "failed" : "succeeded", activity.SafeCode));
    }
}
