using RuAgent.Harness.Core.Context;

namespace RuAgent.Harness.Core.Prompting;

public static class FailedToolCompletionGuard
{
    public static bool TryGetFailure(ModelContextSnapshot context, out string errorCode)
    {
        ArgumentNullException.ThrowIfNull(context);

        errorCode = string.Empty;
        if (context.Entries.Count == 0)
        {
            return false;
        }

        var lastIndex = context.Entries.Count - 1;
        // A recovery note committed after the failed receipt is feedback about
        // that receipt, not a new model action. Skip trailing notes so the
        // failure stays visible to the next final-prose step and the bounded
        // note budget remains reachable in the real flow.
        while (lastIndex >= 0 && IsTrailingRecoveryNote(context.Entries[lastIndex]))
        {
            lastIndex--;
        }

        if (lastIndex < 0 || context.Entries[lastIndex].Kind != ModelContextEntryKind.ToolResult)
        {
            return false;
        }

        var startIndex = lastIndex;
        while (startIndex >= 0 && context.Entries[startIndex].Kind == ModelContextEntryKind.ToolResult)
        {
            startIndex--;
        }

        string? firstFailureCode = null;
        var onlyFirstDeny = true;
        for (var i = startIndex + 1; i <= lastIndex; i++)
        {
            var receipt = context.Entries[i].ToolReceipt;
            if (receipt is null || receipt.Outcome != ToolExecutionOutcome.Succeeded)
            {
                var code = receipt?.Code;
                code = !string.IsNullOrWhiteSpace(code) && IsSafeCode(code)
                    ? code
                    : "tool_failed";
                firstFailureCode ??= code;
                // A first policy deny is not terminal: the denied receipt carries
                // a bounded repair hint and the model gets one chance to adjust.
                // Any repeat deny (or any other failure) ends the run as before.
                if (!string.Equals(code, "policy_denied", StringComparison.Ordinal)
                    || HasEarlierDeny(context, startIndex))
                {
                    onlyFirstDeny = false;
                }
            }
        }

        if (firstFailureCode is null || onlyFirstDeny || HasPartialWebEvidence(context, startIndex, lastIndex))
        {
            return false;
        }

        errorCode = firstFailureCode;
        return true;
    }

    private static bool HasPartialWebEvidence(ModelContextSnapshot context, int assistantIndex, int lastReceipt)
    {
        if (assistantIndex < 0 || context.Entries[assistantIndex].Kind != ModelContextEntryKind.AssistantStep)
            return false;
        var calls = context.Entries[assistantIndex].AssistantStep!.ToolCalls;
        if (calls.Count != lastReceipt - assistantIndex) return false;
        var bindings = new HashSet<string>(StringComparer.Ordinal);
        var readable = false;
        var empty = false;
        for (var index = 0; index < calls.Count; index++)
        {
            var call = calls[index];
            var receipt = context.Entries[assistantIndex + index + 1].ToolReceipt;
            if (call.ToolId != "web_fetch" || call.ToolVersion != "1.0.0"
                || !bindings.Add(call.ProviderCallId) || receipt is null || receipt.SideEffectsPossible
                || receipt.ProviderCallId != call.ProviderCallId) return false;
            if (receipt.Outcome == ToolExecutionOutcome.Succeeded && receipt.Code == "ok") readable = true;
            else if (receipt.Outcome == ToolExecutionOutcome.Failed && receipt.Code == "web_content_empty") empty = true;
            else return false;
        }
        // Empty optional pages do not invalidate facts read from other pages.
        // This exception cannot clear a denial, mutation, cancellation or unknown result.
        return readable && empty;
    }

    private static bool IsTrailingRecoveryNote(ModelContextEntry entry) =>
        entry.Kind == ModelContextEntryKind.Text
        && entry.Role == ModelContextRole.System
        && entry.Content is { } content
        && (content.StartsWith(AgentRecoveryNotes.NotePrefix, StringComparison.Ordinal)
            || content.StartsWith(ConsecutiveToolCallGuard.RepairNotePrefix, StringComparison.Ordinal));

    private static bool HasEarlierDeny(ModelContextSnapshot context, int beforeIndex)
    {
        for (var i = 0; i <= beforeIndex; i++)
        {
            if (string.Equals(
                    context.Entries[i].ToolReceipt?.Code,
                    "policy_denied",
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSafeCode(string value) =>
        value is { Length: >= 1 and <= 64 }
        && value[0] is >= 'a' and <= 'z'
        && value.All(character =>
            character is (>= 'a' and <= 'z')
                or (>= '0' and <= '9')
                or '_'
                or '.');
}
