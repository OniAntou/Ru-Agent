using RuAgent.Client;

namespace RuAgent.Cli.Tui;

/// <summary>What to do about a reply whose durable text and streamed text disagree.</summary>
public enum AssistantTextRecovery
{
    /// <summary>The run said nothing recoverable.</summary>
    None,

    /// <summary>Everything the run produced already reached the terminal.</summary>
    Complete,

    /// <summary>A suffix never arrived; printing it restores the answer exactly.</summary>
    AppendSuffix,

    /// <summary>
    /// The head never arrived. It cannot be restored: a terminal cannot unprint, so
    /// appending it after the tail would interleave the two and read as garbage.
    /// </summary>
    ReportTruncation,
}

/// <summary>
/// Decides how to reconcile what a turn streamed with what the run durably recorded.
///
/// The live event hub has no replay buffer, so deltas published before the client
/// subscribes are dropped, and because deltas are ordered what is lost is the head of the
/// reply. This is the whole reason a finished turn can look like it rendered a fragment.
/// The run's own <c>assistanttext</c> payload holds the complete text, so the gap is at
/// least measurable -- and the decision about what to do with it belongs here, where it can
/// be tested, rather than inside the chat loop.
/// </summary>
public static class AssistantTextReconciler
{
    public static AssistantTextRecovery Decide(string? streamed, string? durable)
    {
        if (string.IsNullOrEmpty(durable))
        {
            return AssistantTextRecovery.None;
        }

        if (string.IsNullOrEmpty(streamed))
        {
            // Nothing arrived at all, so the entire answer is missing rather than a part of
            // it. Printing the whole thing is still the right move: there is no earlier text
            // to interleave with.
            return AssistantTextRecovery.AppendSuffix;
        }

        if (durable.Length <= streamed.Length)
        {
            return AssistantTextRecovery.Complete;
        }

        return durable.StartsWith(streamed, StringComparison.Ordinal)
            ? AssistantTextRecovery.AppendSuffix
            : AssistantTextRecovery.ReportTruncation;
    }

    /// <summary>Recovers only Host-typed assistant text. An answer may itself describe
    /// the tool protocol in JSON, so its field names never determine its payload type.</summary>
    public static IReadOnlyList<string> ProsePayloads(IEnumerable<AgentPayloadContent> payloads)
    {
        var kept = new List<string>();
        foreach (var payload in payloads)
        {
            if (payload.Kind != "assistanttext" || payload.Encoding != "utf8text"
                || string.IsNullOrEmpty(payload.Content))
            {
                continue;
            }

            kept.Add(payload.Content);
        }

        return kept;
    }

    /// <summary>The text to print for a decision, or an empty string when nothing should be printed.</summary>
    public static string SuffixFor(AssistantTextRecovery decision, string? streamed, string? durable)
    {
        if (decision != AssistantTextRecovery.AppendSuffix || string.IsNullOrEmpty(durable))
        {
            return string.Empty;
        }

        return durable[(streamed?.Length ?? 0)..];
    }
}
