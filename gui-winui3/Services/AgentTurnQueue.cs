using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using RuApp.Models;

namespace RuApp.Services;

/// <summary>
/// One turn typed while a run was in hand, waiting to be sent when that run ends.
/// </summary>
/// <param name="Text">The prompt the Agent receives, attachments already inlined.</param>
/// <param name="DisplayText">The text the user typed, shown on the user bubble.</param>
/// <param name="Attachments">The attachments that travel with this turn.</param>
public sealed record QueuedAgentTurn(
    string Text,
    string DisplayText,
    IReadOnlyList<ImageAttachmentItem> Attachments)
{
    /// <summary>The single-line chip label.</summary>
    public string Label => AgentTurnQueuePolicy.PreviewLabel(DisplayText, Text);

    /// <summary>Whether the chip should mention that this turn carries files.</summary>
    public bool HasAttachments => Attachments.Count > 0;
}

/// <summary>
/// The queue's fixed limits and the two decisions that are pure arithmetic:
/// how many turns may wait, and how a turn is spelled on one line.
/// </summary>
public static class AgentTurnQueuePolicy
{
    /// <summary>
    /// Turns that may wait behind the run in hand. A queue is a convenience for the
    /// next thought, not a backlog: past a handful the user is composing a plan, and
    /// silently running five turns on their behalf is not what they asked for.
    /// </summary>
    public const int MaxTurns = 5;

    /// <summary>Longest chip label before it is trimmed, ellipsis included.</summary>
    public const int MaxLabelLength = 48;

    public static bool CanEnqueue(int queuedCount) => queuedCount < MaxTurns;

    /// <summary>
    /// Whether the queue should hand its first turn over. An explicit Stop holds the
    /// queue for exactly one completion: the user asked for the run to end, so
    /// starting the next one without them saying so would misread the interrupt.
    /// </summary>
    public static bool ShouldAutoDrain(int queuedCount, bool heldAfterStop) =>
        queuedCount > 0 && !heldAfterStop;

    /// <summary>
    /// The one-line chip label: the first line of what the user typed, trimmed to
    /// the chip width, falling back to the attachment-only wording.
    /// </summary>
    public static string PreviewLabel(string? displayText, string? text = null)
    {
        string source = !string.IsNullOrWhiteSpace(displayText) ? displayText : text ?? string.Empty;
        string flattened = string.Join(
            " ",
            source.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (flattened.Length == 0)
        {
            return "Attachment";
        }

        return flattened.Length <= MaxLabelLength
            ? flattened
            : string.Concat(flattened.AsSpan(0, MaxLabelLength - 1), "…");
    }
}

/// <summary>
/// The turns waiting behind the run on screen, in the order the user typed them.
///
/// The composer used to answer a second thought with a shrug: while a run was in
/// hand the input accepted text and Enter did nothing, so the thought had to be
/// held in the user's head until the turn ended. The queue holds it instead, and
/// hands the first turn over when the run reaches its end.
/// </summary>
public sealed class AgentTurnQueue
{
    /// <summary>The queued turns, oldest first, bound directly to the strip.</summary>
    public ObservableCollection<QueuedAgentTurn> Items { get; } = new();

    /// <summary>True once an explicit Stop held the queue for the next completion.</summary>
    public bool IsHeldAfterStop { get; private set; }

    public bool IsEmpty => Items.Count == 0;

    /// <summary>
    /// Queues a turn unless the queue is full or the turn carries nothing. Returns
    /// false when the caller should keep the composer text instead of clearing it.
    /// </summary>
    public bool TryEnqueue(string text, string displayText, IReadOnlyList<ImageAttachmentItem>? attachments)
    {
        ArgumentNullException.ThrowIfNull(text);

        var carried = attachments ?? Array.Empty<ImageAttachmentItem>();
        if (!AgentTurnQueuePolicy.CanEnqueue(Items.Count)
            || (string.IsNullOrWhiteSpace(text) && carried.Count == 0))
        {
            return false;
        }

        Items.Add(new QueuedAgentTurn(text, displayText, carried.ToArray()));
        return true;
    }

    /// <summary>
    /// Holds the queue so the completion caused by this Stop does not start the
    /// queued turn on its own.
    /// </summary>
    public void HoldAfterStop() => IsHeldAfterStop = true;

    /// <summary>
    /// Drops a stop hold without waiting for the completion it was meant to guard,
    /// so a hand-driven flush leaves the queue draining normally afterwards.
    /// </summary>
    public void ReleaseHold() => IsHeldAfterStop = false;

    /// <summary>
    /// Claims the right to hand over the first turn, consuming a stop hold. False
    /// means the drain must not run: either the queue is empty or this completion
    /// is the one the user interrupted.
    /// </summary>
    public bool TryBeginAutoDrain()
    {
        if (IsHeldAfterStop)
        {
            IsHeldAfterStop = false;
            return false;
        }

        return Items.Count > 0;
    }

    /// <summary>The first turn in line, removed from the queue.</summary>
    public QueuedAgentTurn? TakeFirst()
    {
        if (Items.Count == 0)
        {
            return null;
        }

        var first = Items[0];
        Items.RemoveAt(0);
        return first;
    }

    /// <summary>
    /// Puts a taken turn back at the head of the queue. Used when a handover
    /// could not start a run, so the turn is not lost with its order intact.
    /// </summary>
    public void RequeueFirst(QueuedAgentTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        Items.Insert(0, turn);
    }

    /// <summary>Drops one turn by identity, so two identical turns stay distinguishable.</summary>
    public bool Remove(QueuedAgentTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        for (int index = 0; index < Items.Count; index++)
        {
            if (ReferenceEquals(Items[index], turn))
            {
                Items.RemoveAt(index);
                return true;
            }
        }

        return false;
    }

    /// <summary>Empties the queue, including any stop hold; used when the session changes.</summary>
    public void Clear()
    {
        Items.Clear();
        IsHeldAfterStop = false;
    }
}
