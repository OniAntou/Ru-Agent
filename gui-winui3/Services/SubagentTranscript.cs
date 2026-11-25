using System.Collections.ObjectModel;
using RuAgent.Client;
using RuApp.Models;

namespace RuApp.Services;

/// <summary>The attempt the detail view reads: its run, brief, state and recorded instants.</summary>
internal sealed record SubagentAttemptView(
    string RunId, string? Task, string Status, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt);

/// <summary>
/// One subagent attempt drawn as a conversation: the brief the main agent delegated as the
/// user's message, and the child's own run replay as the assistant's reply, built by the
/// same projection a reopened main-chat turn uses.
/// </summary>
/// <remarks>
/// Re-applied on every read of the replay. Each activity id keeps one item that is updated
/// in place, and the reply's segments are merged rather than replaced, so a row the reader
/// expanded stays expanded and a live attempt does not repaint on each tick. Both messages
/// are observed rows: they offer no rewind and claim no turn changes.
/// </remarks>
internal sealed class SubagentTranscript
{
    private readonly Dictionary<string, AgentActivityItem> _items = new(StringComparer.Ordinal);
    private ChatMessageItem? _prompt;
    private ChatMessageItem? _reply;

    public ObservableCollection<ChatMessageItem> Messages { get; } = [];
    public string? RunId { get; private set; }
    public ChatMessageItem? Reply => _reply;

    /// <summary>Whether the reply already carries the child's words.</summary>
    public bool HasReplyText => _reply?.HasText == true;

    /// <summary>
    /// A completed result is the child's final reply, which the conversation already shows.
    /// Any other result is text the Host wrote about how the attempt ended, so it stays.
    /// </summary>
    public bool ShowsPublishedText(string? resultStatus) => resultStatus != "completed" || !HasReplyText;

    public void Clear()
    {
        Messages.Clear();
        _items.Clear();
        _prompt = _reply = null;
        RunId = null;
    }

    public void Apply(SubagentAttemptView attempt, AgentRunActivityReplay replay, bool compact)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(replay);
        if (attempt.RunId != RunId) Start(attempt.RunId);
        var started = attempt.StartedAt?.LocalDateTime;
        ApplyPrompt(attempt.Task, started, compact);

        var reply = _reply!;
        var running = !SubagentTreeModel.IsTerminal(attempt.Status);
        // Live before any text lands: a settled-looking reply folds itself on its first words.
        if (running) reply.IsStreaming = true;
        if (started is { } origin) reply.Timestamp = origin;
        reply.CompactAgentActivity = compact;
        ApplyActivities(reply, replay);
        ApplySegments(reply, replay);

        reply.IsError = attempt.Status == "failed";
        if (!running)
        {
            if (attempt.EndedAt is { } ended) reply.SettleAt(ended.LocalDateTime);
            else reply.IsStreaming = false;
            reply.SettleSegments();
        }
    }

    private void Start(string runId)
    {
        Clear();
        RunId = runId;
        _reply = new ChatMessageItem
        {
            Role = "assistant", IsObserved = true, SourceRunId = runId,
            AgentActivities = new ObservableCollection<AgentActivityItem>(),
        };
        Messages.Add(_reply);
    }

    /// <summary>The brief as the user's message; an attempt without one shows no bubble at all.</summary>
    private void ApplyPrompt(string? task, DateTime? started, bool compact)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            if (_prompt is not null) { Messages.Remove(_prompt); _prompt = null; }
            return;
        }
        if (_prompt is null)
        {
            _prompt = new ChatMessageItem { Role = "user", IsObserved = true, SourceRunId = RunId };
            Messages.Insert(0, _prompt);
        }
        _prompt.Content = task;
        _prompt.CompactAgentActivity = compact;
        if (started is { } origin) _prompt.Timestamp = origin;
    }

    private void ApplyActivities(ChatMessageItem reply, AgentRunActivityReplay replay)
    {
        var order = new List<AgentActivityItem>(replay.Activities.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in replay.Activities)
        {
            if (string.IsNullOrWhiteSpace(model.ActivityId)) continue;
            if (_items.TryGetValue(model.ActivityId, out var item)) item.UpdateFrom(model);
            else _items[model.ActivityId] = item = new AgentActivityItem(model) { OwnerRunId = RunId };
            if (seen.Add(model.ActivityId)) order.Add(item);
        }

        var rows = reply.AgentActivities!;
        if (!rows.SequenceEqual(order))
        {
            // Rows only ever arrive at the end of a live replay; append when that holds.
            var prefix = rows.Count <= order.Count && rows.Select((row, i) => ReferenceEquals(row, order[i])).All(same => same);
            if (!prefix) rows.Clear();
            for (var i = rows.Count; i < order.Count; i++) rows.Add(order[i]);
        }
        AgentActivityGrouper.Reconcile(rows);
    }

    private void ApplySegments(ChatMessageItem reply, AgentRunActivityReplay replay)
    {
        var transcript = replay.Transcript
            .Select(entry => new ApiService.RunTranscriptEntry(entry.Sequence, entry.Kind, entry.Text, entry.ActivityId))
            .ToArray();
        var built = AgentReplaySegmentBuilder.Build(reply.AgentActivities!.ToArray(), transcript);
        if (built is null)
        {
            // A Host without a transcript: the grouped feed shows the rows on their own.
            reply.Segments = null;
            reply.ActivitySplitOffsets = null;
            reply.Content = "";
            return;
        }

        reply.ActivitySplitOffsets = built.Offsets;
        if (reply.Segments is { } shown) Merge(shown, built.Segments);
        else reply.Segments = built.Segments;
        reply.Content = built.Content;
    }

    /// <summary>Keeps every unchanged segment instance, so its realized row is left alone.</summary>
    internal static void Merge(ObservableCollection<AssistantSegment> shown, IReadOnlyList<AssistantSegment> next)
    {
        for (var i = 0; i < next.Count; i++)
        {
            if (i >= shown.Count) { shown.Add(next[i]); continue; }
            switch (shown[i], next[i])
            {
                case (ActivitySegment a, ActivitySegment b) when ReferenceEquals(a.Item, b.Item):
                    break;
                case (TextSegment a, TextSegment b):
                    a.Text = b.Text;
                    break;
                default:
                    shown[i] = next[i];
                    break;
            }
        }
        while (shown.Count > next.Count) shown.RemoveAt(shown.Count - 1);
    }
}
