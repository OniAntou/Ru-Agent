namespace RuAgent.Client;

/// <summary>
/// The shared wording for an activity row that says the run is trying again.
/// The Host keeps the durable cause in the activity's bounded safe code; the
/// terminal and the desktop row resolve it here so both tell the same reason.
/// </summary>
/// <remarks>
/// Retry rows used to read only "Retried" and an attempt number. That told the
/// reader that something failed and not what failed, so the row looked like
/// unexplained noise next to the work it repeated. The codes are machine tokens,
/// not sentences; an unknown code keeps its raw token so a newer Host never has
/// its cause silently replaced by a generic phrase.
/// </remarks>
public static class AgentRetryCopy
{
    private static readonly IReadOnlyDictionary<string, string> Reasons =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tool_transport_error"] = "a tool transport error",
            ["transport_error"] = "a transport error",
            ["model_output_truncated"] = "a truncated reply",
            ["model_commentary_only"] = "a commentary-only reply",
            ["empty_model_reply"] = "an empty reply",
            ["unverified_after_tool_failure"] = "an unverified tool result",
            ["unverified_source_edit"] = "an unverified change",
            ["open_todos"] = "open tasks",
            ["completion_review"] = "a requirements check",
            ["advisor_recommended"] = "an advisor recommendation",
            ["model_repairable"] = "a rejected tool reply",
            ["invalid_selection"] = "a rejected tool selection",
            ["model_tool_call_invalid"] = "a malformed tool call",
            ["model_tool_call_unavailable"] = "an unavailable tool",
            ["model_tool_batch_too_large"] = "a tool batch over the step limit",
            ["context_window_exceeded"] = "context pressure",
            ["repeated_tool_call"] = "a repeated tool call",
            ["provider_retry"] = "an earlier provider failure",
            ["provider_rate_limited"] = "a rate limit",
            ["provider_timeout"] = "a timeout",
            ["provider_server_error"] = "a server error",
            ["provider_transport_error"] = "a transport error",
            ["protocol_failure"] = "a malformed response",
            // Older durable rows and the row's generic default carry no cause.
            ["retrying"] = "an earlier failure",
            ["started"] = "an earlier failure",
            ["succeeded"] = "an earlier failure",
        };

    /// <summary>Every catalogued retry cause, keyed by its exact wire token.</summary>
    public static IReadOnlyDictionary<string, string> Table => Reasons;

    /// <summary>
    /// The cause as a noun phrase ("a transport error"), or null for a blank
    /// code. Unknown codes fall back to their raw token so a future cause stays
    /// visible instead of pretending to be an older one.
    /// </summary>
    public static string? Reason(string? safeCode)
    {
        var key = (safeCode ?? string.Empty).Trim();
        if (key.Length == 0)
        {
            return null;
        }

        return Reasons.TryGetValue(key, out var reason) ? reason : $"an earlier failure ({key})";
    }

    /// <summary>The live row label.</summary>
    public static string Started(string? safeCode, int? attempt)
    {
        var reason = Reason(safeCode) ?? "an earlier failure";
        var suffix = attempt is > 0 ? $" (attempt {attempt})" : string.Empty;
        return $"Retrying after {reason}{suffix}";
    }

    /// <summary>The settled row label.</summary>
    public static string Completed(string? safeCode)
    {
        var reason = Reason(safeCode) ?? "an earlier failure";
        return $"Retried after {reason}";
    }

    /// <summary>The lower-case notice the terminal prints for a live retry.</summary>
    public static string Notice(string? safeCode)
    {
        var reason = Reason(safeCode) ?? "an earlier failure";
        return $"retrying after {reason}";
    }
}
