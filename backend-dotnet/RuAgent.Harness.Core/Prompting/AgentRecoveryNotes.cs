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
    OutputTruncated,
    CommentaryOnly,
    ContextOverflow,
}

/// <summary>
/// Durable corrective notes that keep a run alive instead of failing it: the note
/// lands in the model context as a system entry and the run takes one more step.
/// Counts are read back from the assembled context, exactly like the repetition
/// guard reads its own notes, so the context stays the single source of truth for
/// "the model was already told".
/// </summary>
/// <remarks>
/// The cap is <see cref="MaximumNotesPerRun"/>, not per budget window. A note is
/// persisted with <see cref="ModelContextRole.System"/> and the compaction planner
/// protects every system entry, so a note is never folded into a summary and never
/// leaves the count mid-run. The name says "run" because that is what it measures.
/// </remarks>
public static class AgentRecoveryNotes
{
    // Model-visible marker shared by every recovery note, so the global cap can
    // count notes from every case, including ones added later.
    public const string NotePrefix = "[HARNESS RECOVERY]";
    public const string BlockedReplyPrefix = "[BLOCKED]";

    public static bool TryReadBlockedReply(AssistantStep step, out string report)
    {
        report = string.Empty;
        if (step.ToolCalls.Count != 0 || step.FinishReason != AssistantFinishReason.Stop
            || step.Text is not { } text || !text.StartsWith(BlockedReplyPrefix, StringComparison.Ordinal)) return false;
        report = text[BlockedReplyPrefix.Length..].Trim();
        return report.Length > 0;
    }

    // Bounded self-repair budgets per case: a model that ignores the note still
    // fails, but a weak model gets a real chance to answer or change course. The
    // sizing assumption is a long task: a 40-tool-call run that hits the same
    // edit or verification failure twice per case is ordinary work, not a bad
    // model, and the previous 2/2/1 spent the global cap on exactly that.
    public const int MaximumUnverifiedNotes = 4;
    public const int MaximumEmptyNotes = 3;
    public const int MaximumModelRepairableNotes = 4;
    public const int MaximumTruncatedNotes = 3;

    // Global cap across every recovery case, counted from the context for the
    // whole run. This bounds recovery across all cases, including truncation.
    public const int MaximumNotesPerRun = 24;
    public const int MaximumCommentaryNotes = 3;
    public const int MaximumContextOverflowNotes = 2;

    public static string BuildContextOverflowNote() =>
        $"{NotePrefix} {KindMarker(AgentRecoveryKind.ContextOverflow)} - the provider rejected the previous context. "
        + "The harness reduced older history; no proposed tools from the rejected reply executed. Continue the current request using this checkpoint and retained evidence.";

    /// <summary>
    /// Names the failed receipt and gives recovery guidance for known effect codes.
    /// The code comes from the sanitized receipt code, so it is safe text.
    /// </summary>
    public static string BuildUnverifiedNote(string? receiptCode)
    {
        var code = string.IsNullOrWhiteSpace(receiptCode) ? "tool_failed" : receiptCode;
        return $"{NotePrefix} {KindMarker(AgentRecoveryKind.UnverifiedAfterToolFailure)} "
            + $"(code: {code}) - the final reply was suppressed because a tool call failed "
            + "and its result is still unverified. " + RecoveryAction(code)
            + " If no safe repair remains, start your final text with [BLOCKED] and explain the blocker or partial outcome. This ends the run as unsuccessful; do not claim the failed action succeeded.";
    }

    private static string RecoveryAction(string code) => code switch
    {
        "edit_no_match" => "Re-read the same file, then correct the exact old text before retrying the edit.",
        "edit_not_unique" => "Re-read the same file and narrow old_string or set expected_occurrences to the observed match count.",
        "workspace_changed" => "Inspect the current workspace and reconcile the change; do not overwrite stale content.",
        "process_exit_nonzero" => "For a declared required check, inspect the verification diagnostics, fix the cause and rerun that check. For exploratory work, repair the command or use an alternative relevant source; do not claim the failed command succeeded.",
        "expected_exit_mismatch" => "Report the observed exit code and the unmet expectation. Do not retry a deliberately failing command merely to force the expected code, or claim a different invocation cleared it. Use run_process when a native process's exact exit code matters; PowerShell wrappers can return 1 instead.",
        "verification_stale" => "Code changed after a successful check. Rerun that same verification against the current changes.",
        "verification_pending" => "The required check is still running. Read its terminal output until its real exit is known; starting it is not a pass.",
        "verification_result_missing" => "The check has no confirmed terminal exit. Inspect its retained output or rerun it; do not infer success.",
        "process_start_failed" => "Inspect the executable and invocation error, then retry only after correcting it.",
        "malformed_call" or "invalid_arguments" =>
            "The call was rejected before anything ran. Follow the receipt's hint to correct the arguments and run it again; when it declared purpose verification or expected_failure, keep that purpose on the corrected call so its passing result resolves the check.",
        "side_effects_possible" => "Inspect the affected state before deciding whether any retry is safe.",
        "approval_denied" or "access_denied" or "rule_denied" or "workspace_path_outside_root" =>
            "The action was denied; report the blocker and do not route around the same restriction.",
        _ => "Inspect the failed receipt, correct its named cause, then retry only when the action is safe; otherwise report the blocker.",
    };

    public static string BuildEmptyNote() =>
        $"{NotePrefix} {KindMarker(AgentRecoveryKind.EmptyModelReply)} - the last model "
        + "reply was empty. Answer with text or a valid tool call.";

    public static string BuildCommentaryNote() =>
        $"{NotePrefix} {KindMarker(AgentRecoveryKind.CommentaryOnly)} - the provider marked its last reply as commentary, not a final answer. "
        + "Continue the requested work with advertised tools when needed, or supply the final closeout when done. Do not repeat the progress message.";

    /// <summary>Progress is retained for replay but cannot reset tool failure/repetition evidence.</summary>
    public static bool IsCommentaryProgress(ModelContextEntry entry) =>
        entry.AssistantStep?.ResponsesMessages?.IsCommentaryOnly == true
        || entry.Kind == ModelContextEntryKind.Text && entry.Role == ModelContextRole.System
            && entry.Content?.StartsWith(NotePrefix + " model_commentary_only -", StringComparison.Ordinal) == true;

    public static string BuildTruncatedNote() =>
        $"{NotePrefix} {KindMarker(AgentRecoveryKind.OutputTruncated)} - the provider "
        + "hit its output limit. The incomplete reply was not accepted and none of "
        + "its tool calls executed. Use a shorter valid tool call or a concise reply; "
        + "split large edits into smaller steps. Keep reasoning brief and call a tool "
        + "early: write a minimal first version, then refine it with separate edits. "
        + "Do not repeat the unfinished output.";

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
        AgentRecoveryKind.OutputTruncated => "model_output_truncated",
        AgentRecoveryKind.CommentaryOnly => "model_commentary_only",
        AgentRecoveryKind.ContextOverflow => "context_window_exceeded",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static int MaximumNotes(AgentRecoveryKind kind) => kind switch
    {
        AgentRecoveryKind.UnverifiedAfterToolFailure => MaximumUnverifiedNotes,
        AgentRecoveryKind.EmptyModelReply => MaximumEmptyNotes,
        AgentRecoveryKind.ModelRepairable => MaximumModelRepairableNotes,
        AgentRecoveryKind.OutputTruncated => MaximumTruncatedNotes,
        AgentRecoveryKind.CommentaryOnly => MaximumCommentaryNotes,
        AgentRecoveryKind.ContextOverflow => MaximumContextOverflowNotes,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// True while both the per-case bound and the global per-run cap leave room
    /// for one more note. The global cap is checked first so it bounds behavior
    /// even if a case's marker ever changes shape.
    /// </summary>
    public static bool CanAdvise(ModelContextSnapshot context, AgentRecoveryKind kind)
    {
        ArgumentNullException.ThrowIfNull(context);
        // A truncated reply says nothing about later steps: a long run that recovers and then
        // truncates again far later is still progressing, so truncation is bounded by the
        // notes issued in a row, while the global cap still bounds the whole run.
        var caseCount = kind == AgentRecoveryKind.OutputTruncated
            ? ConsecutiveNoteCount(context, kind)
            : NoteCount(context, kind);
        return NoteCount(context) < MaximumNotesPerRun && caseCount < MaximumNotes(kind);
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

    /// <summary>
    /// Notes of one case at the end of the context with no other entry between them and the
    /// end; notes of other cases in that tail are skipped, any other entry stops the count.
    /// </summary>
    public static int ConsecutiveNoteCount(ModelContextSnapshot context, AgentRecoveryKind kind)
    {
        ArgumentNullException.ThrowIfNull(context);
        var marker = $"{NotePrefix} {KindMarker(kind)}";
        var count = 0;
        for (var index = context.Entries.Count - 1; index >= 0; index--)
        {
            var entry = context.Entries[index];
            if (!IsRecoveryNote(entry)) break;
            if (entry.Content!.StartsWith(marker, StringComparison.Ordinal)) count++;
        }

        return count;
    }

    private static bool IsRecoveryNote(ModelContextEntry entry) =>
        entry.Kind == ModelContextEntryKind.Text
        && entry.Content is { } content
        && content.StartsWith(NotePrefix, StringComparison.Ordinal);
}
