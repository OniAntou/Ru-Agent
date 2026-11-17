using System.Text;
using System.Text.Json;

namespace RuAgent.Harness.Core.Context;

/// <summary>
/// Native continuation (ADR 0049): a new root run under the same pinned model as the
/// session's immediately preceding root run reads that run's own projected history as
/// native entries, instead of the portable text handoff. Only the closed part is carried:
/// a tool batch the source run never settled is replaced by a Host note, never by a
/// synthesized receipt. Provider continuation state and execution authority never move.
/// </summary>
public static class NativeRunContinuation
{
    /// <summary>Reserved base entry naming the source run; never sent to the model.</summary>
    public const string MarkerSourceId = "base:native-continuation";

    /// <summary>Host note that lists the calls the source run left unsettled.</summary>
    public const string NoteSourceId = "base:native-continuation-note";

    /// <summary>The share of the window the carried history may take when the run starts.</summary>
    public const double MaximumWindowShare = 0.5;

    /// <summary>
    /// The window assumed for that check when the catalog publishes none. It is deliberately
    /// small: the shared assumed window would let a large history overflow a real small model.
    /// </summary>
    public const long UnknownWindowTokens = 128_000;

    private const int CharactersPerToken = 3;
    private const int MaximumListedCalls = 16;
    private const int MaximumNoteTextCharacters = 500;
    private const string MarkerProperty = "run_id";

    /// <summary>
    /// A source run that ended because its context did not fit would fail the new run the
    /// same way; those continue through the text handoff instead.
    /// </summary>
    public static bool AllowsTerminalCode(string? terminalErrorCode) =>
        terminalErrorCode is null
        || !(terminalErrorCode is "context_limit_reached" or "request_too_large"
            || terminalErrorCode.StartsWith("checkpoint_", StringComparison.Ordinal)
            || terminalErrorCode.StartsWith("context_", StringComparison.Ordinal));

    /// <summary>
    /// The most entries carried. Carried entries take base sequences, and the recovery and
    /// repetition guards measure their window back from the newest sequence in context; this
    /// keeps the carried base well inside that window, so the new run's own notes still count.
    /// </summary>
    public const int MaximumCarriedEntries = 256;

    /// <summary>
    /// Whether the slice can be carried: at most <see cref="MaximumCarriedEntries"/> entries,
    /// leaving the new run at least half of its window.
    /// </summary>
    public static bool Fits(Slice slice, long? knownContextWindowTokens)
    {
        ArgumentNullException.ThrowIfNull(slice);
        if (slice.IsEmpty || slice.Entries.Count > MaximumCarriedEntries) return false;
        long window = knownContextWindowTokens is > 0 ? knownContextWindowTokens.Value : UnknownWindowTokens;
        return slice.Characters / (double)CharactersPerToken <= window * MaximumWindowShare;
    }

    public static string EncodeMarker(RunId sourceRunId) =>
        JsonSerializer.Serialize(new Dictionary<string, string> { [MarkerProperty] = sourceRunId.Value });

    /// <summary>The source run named by a reserved marker; quoted marker text elsewhere is ignored.</summary>
    public static RunId? DecodeMarker(ModelContextEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.SourceId != MarkerSourceId || entry.Kind != ModelContextEntryKind.Text || entry.Content is null)
            return null;
        try
        {
            using var document = JsonDocument.Parse(entry.Content);
            return document.RootElement.TryGetProperty(MarkerProperty, out var value)
                && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } id
                    ? new RunId(id)
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public sealed record Slice(IReadOnlyList<ModelContextEntry> Entries, string? UnsettledNote, int Characters)
    {
        public bool IsEmpty => Entries.Count == 0;
    }

    /// <summary>
    /// Keeps the longest prefix in which every proposed call has its receipt. Whatever follows
    /// the first unsettled batch is dropped and described in a note, so the provider never sees
    /// a call without its result.
    /// </summary>
    public static Slice Close(IReadOnlyList<ModelContextEntry> runLocal)
    {
        ArgumentNullException.ThrowIfNull(runLocal);
        // Recovery and repetition notes were guidance for the source run. The guards count
        // them by context position, so carrying them would spend the new run's note budget.
        var entries = runLocal.Where(entry => !Prompting.RuntimeContextSnapshot.IsSnapshot(entry)
            && !IsSourceRunNote(entry)).ToArray();
        var pending = new HashSet<string>(StringComparer.Ordinal);
        int closedEnd = 0;
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (entry.Kind == ModelContextEntryKind.ToolResult)
            {
                if (entry.ToolReceipt?.ProviderCallId is not { } callId || !pending.Remove(callId)) break;
                if (pending.Count == 0) closedEnd = index + 1;
                continue;
            }
            if (pending.Count != 0) break;
            if (entry.AssistantStep is { ToolCalls.Count: > 0 } step)
            {
                foreach (var call in step.ToolCalls) pending.Add(call.ProviderCallId);
                continue;
            }
            closedEnd = index + 1;
        }

        var kept = entries[..closedEnd];
        var dropped = entries[closedEnd..];
        return new Slice(kept, dropped.Length == 0 ? null : DescribeUnsettled(dropped), kept.Sum(Characters));
    }

    private static string DescribeUnsettled(IReadOnlyList<ModelContextEntry> dropped)
    {
        var receipts = dropped.Where(entry => entry.ToolReceipt?.ProviderCallId is not null)
            .GroupBy(entry => entry.ToolReceipt!.ProviderCallId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().ToolReceipt!, StringComparer.Ordinal);
        var note = new StringBuilder("[native continuation] The previous run stopped before these tool calls were "
            + "settled, so they are left out of this history. Any effect they had is unverified: check the current "
            + "files or state before repeating one.");
        var calls = dropped.Where(entry => entry.AssistantStep is not null)
            .SelectMany(entry => entry.AssistantStep!.ToolCalls).ToArray();
        foreach (var call in calls.Take(MaximumListedCalls))
        {
            var receipt = receipts.TryGetValue(call.ProviderCallId, out var found)
                ? $"{found.Outcome} {found.Code}"
                : "none";
            note.Append($"\n- tool={call.ToolId} call={call.ProviderCallId} receipt={receipt}");
        }
        if (calls.Length > MaximumListedCalls) note.Append($"\n- {calls.Length - MaximumListedCalls} more calls omitted.");
        var text = dropped.Select(entry => entry.AssistantStep?.Text).LastOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (text is not null)
        {
            if (text.Length > MaximumNoteTextCharacters) text = text[..MaximumNoteTextCharacters] + " [truncated]";
            note.Append("\nIts last statement, not proof of execution: ").Append(JsonSerializer.Serialize(text));
        }
        return note.ToString();
    }

    private static bool IsSourceRunNote(ModelContextEntry entry) =>
        entry.Kind == ModelContextEntryKind.Text
        && entry.Content is { } content
        && (content.StartsWith(Prompting.AgentRecoveryNotes.NotePrefix, StringComparison.Ordinal)
            || content.StartsWith(Prompting.ConsecutiveToolCallGuard.RepairNotePrefix, StringComparison.Ordinal));

    private static int Characters(ModelContextEntry entry) =>
        (entry.Content?.Length ?? 0)
        + (entry.AssistantStep is { } step
            ? (step.Text?.Length ?? 0) + step.ToolCalls.Sum(call => call.Arguments.GetRawText().Length + call.ToolId.Length)
            : 0);
}
