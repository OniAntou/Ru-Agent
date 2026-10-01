using RuAgent.Harness.Core.Context;

namespace RuAgent.Harness.Core.Prompting;

/// <summary>
/// Which bounded recovery note a dead-end model step is answered with. The marker
/// after <see cref="AgentRecoveryNotes.NotePrefix"/> names the case for the model
/// and lets the harness count how many notes of that case the context already
/// carries.
/// </summary>
public enum AgentRecoveryKind
{
    UnverifiedAfterToolFailure,
    EmptyModelReply,
    ModelRepairable,
}

/// <summary>
/// Durable corrective notes that keep a run alive instead of failing it: the note
/// lands in the model context as a system entry and the run takes one more step.
/// Counts are read back from the assembled context, exactly like the repetition
/// guard reads its own notes, so the context stays the single source of truth for
/// "the model was already told". Compaction can fold a note into a summary and
/// forget it; the global per-window cap still bounds each window.
/// </summary>
public static class AgentRecoveryNotes
{
    // Model-visible marker shared by every recovery note, so the global cap can
    // count notes from every case, including ones added later.
    public const string NotePrefix = "[HARNESS RECOVERY]";

    // Bounded self-repair budgets per case: a model that ignores the note still
    // fails, but a weak model gets a real chance to answer or change course.
    public const int MaximumUnverifiedNotes = 2;
    public const int MaximumEmptyNotes = 1;
    public const int MaximumModelRepairableNotes = 2;

    // Global per-window cap across every recovery case, counted from the context.
    public const int MaximumNotesPerWindow = 6;

    /// <summary>
    /// Names the failed receipt and asks the model to redo, reroute, or report the
    /// blocker. The code comes from the sanitized receipt code, so it is safe text.
    /// </summary>
    public static string BuildUnverifiedNote(string? receiptCode)
    {
        var code = string.IsNullOrWhiteSpace(receiptCode) ? "tool_failed" : receiptCode;
        return $"{NotePrefix} {KindMarker(AgentRecoveryKind.UnverifiedAfterToolFailure)} "
            + $"(code: {code}) - the final reply was suppressed because a tool call failed "
            + "and its result is still unverified. Fix and redo the failed call, use a "
            + "different tool, or report what is blocking you.";
    }

    public static string BuildEmptyNote() =>
        $"{NotePrefix} {KindMarker(AgentRecoveryKind.EmptyModelReply)} - the last model "
        + "reply was empty. Answer with text or a valid tool call.";

    /// <summary>
    /// Names the runner's model-repairable rejection and asks the model to redo
    /// it. The code is a bounded runner code, so it is safe text.
    /// </summary>
    public static string BuildModelRepairableNote(string? code)
    {
        var stable = string.IsNullOrWhiteSpace(code) ? "invalid_selection" : code;
        return $"{NotePrefix} {KindMarker(AgentRecoveryKind.ModelRepairable)} (code: {stable}) "
            + "- the model reply could not be used: the tool call or selection was rejected "
            + "as invalid. Re-issue a valid advertised tool call with complete JSON "
            + "arguments, or answer in plain text.";
    }

    /// <summary>
    /// The runner's stable codes a model can repair by taking another turn: a
    /// malformed, unavailable or otherwise invalid tool call or selection.
    /// Everything else (prompt, provider, history, image) stays terminal.
    /// </summary>
    public static bool IsModelRepairableCode(string? code) =>
        code is "model_tool_call_invalid" or "model_tool_call_unavailable" or "invalid_selection";

    public static string KindMarker(AgentRecoveryKind kind) => kind switch
    {
        AgentRecoveryKind.UnverifiedAfterToolFailure => "unverified_after_tool_failure",
        AgentRecoveryKind.EmptyModelReply => "empty_model_reply",
        AgentRecoveryKind.ModelRepairable => "model_repairable",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static int MaximumNotes(AgentRecoveryKind kind) => kind switch
    {
        AgentRecoveryKind.UnverifiedAfterToolFailure => MaximumUnverifiedNotes,
        AgentRecoveryKind.EmptyModelReply => MaximumEmptyNotes,
        AgentRecoveryKind.ModelRepairable => MaximumModelRepairableNotes,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// True while both the per-case bound and the global per-window cap leave room
    /// for one more note. The global cap is checked first so it bounds behavior even
    /// after compaction loses track of a note's case.
    /// </summary>
    public static bool CanAdvise(ModelContextSnapshot context, AgentRecoveryKind kind)
    {
        ArgumentNullException.ThrowIfNull(context);
        return NoteCount(context) < MaximumNotesPerWindow
            && NoteCount(context, kind) < MaximumNotes(kind);
    }

    /// <summary>
    /// Total recovery notes in the context, of every case.
    /// </summary>
    public static int NoteCount(ModelContextSnapshot context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var count = 0;
        foreach (var entry in context.Entries)
        {
            if (IsRecoveryNote(entry))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Recovery notes of one case, identified by the marker after the prefix.
    /// </summary>
    public static int NoteCount(ModelContextSnapshot context, AgentRecoveryKind kind)
    {
        ArgumentNullException.ThrowIfNull(context);
        var marker = $"{NotePrefix} {KindMarker(kind)}";
        var count = 0;
        foreach (var entry in context.Entries)
        {
            if (IsRecoveryNote(entry)
                && entry.Content!.StartsWith(marker, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsRecoveryNote(ModelContextEntry entry) =>
        entry.Kind == ModelContextEntryKind.Text
        && entry.Content is { } content
        && content.StartsWith(NotePrefix, StringComparison.Ordinal);
}
