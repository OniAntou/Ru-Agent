namespace RuApp.Services;

internal static class AgentFailurePresentation
{
    /// <summary>
    /// Label on the button that opens the provider settings from a failure message.
    /// </summary>
    internal const string OpenProviderSettingsLabel = "Open provider settings";

    /// <summary>
    /// Action identifier carried by that button. Data rather than a method reference,
    /// so a message can describe what it offers without holding a view.
    /// </summary>
    internal const string OpenProviderSettingsAction = "open_provider_settings";

    internal static bool ShouldKeepVisible(string? runStatus, string? safeStatus) =>
        string.Equals(runStatus, "failed", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(Render(safeStatus));

    /// <summary>
    /// Composes the inline status copy above the composer. A failed run defers to
    /// the transcript failure card only when that card is actually present: the
    /// card is suppressed once a reply has been rendered, so blanking the inline
    /// status then would remove the only actionable failure signal.
    /// </summary>
    internal static string InlineFailureStatus(
        string? runStatus,
        string? failureReason,
        bool isRunning,
        bool hasRenderedReply)
    {
        string rendered = Render(failureReason);
        if (!ShouldKeepVisible(runStatus, failureReason) || isRunning)
        {
            return rendered;
        }

        return hasRenderedReply ? rendered : string.Empty;
    }

    internal static string Render(string? status)
    {        if (string.IsNullOrWhiteSpace(status)
            || string.Equals(status.Trim(), "Run ended.", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        string trimmed = status.Trim();
        if (string.Equals(trimmed, "provider_rate_limited", StringComparison.Ordinal))
        {
            return "Model is rate-limited. Please select another model and try again.";
        }

        if (string.Equals(trimmed, "workspace_busy", StringComparison.Ordinal))
        {
            return "Coding Agent is already running. Please complete or stop the current turn first.";
        }

        if (string.Equals(trimmed, "repeated_tool_call", StringComparison.Ordinal))
        {
            return "Ru repeated the same action instead of answering, so the run was stopped. Please rephrase your request and send it again.";
        }

        if (string.Equals(trimmed, "invalid_selection", StringComparison.Ordinal))
        {
            return "Ru produced an action the host could not run, so the run was stopped. Please rephrase your request and send it again.";
        }

        if (string.Equals(trimmed, "empty_model_reply", StringComparison.Ordinal))
        {
            return "The model returned an empty reply. Please try again, or select another model.";
        }

        if (string.Equals(trimmed, "reconciliation_required", StringComparison.Ordinal))
        {
            // Parked, not failed. The host stops driving the run until a
            // reconciliation decision arrives; Continue is that decision, and it
            // resumes with a read-only inspection in front of further mutations.
            return "The last action was interrupted, so its result is unverified. Ru is paused — send Continue to resume with a read-only inspection, or Stop to end the run.";
        }

        if (string.Equals(trimmed, "worker_unavailable", StringComparison.Ordinal))
        {
            // Historical code: the model worker used to be its own process, and the
            // run could not reach it. Nothing about the request was wrong, so the copy
            // asks for a retry instead of a rephrase — and it says the code out loud,
            // because the raw token used to reach the reader unchanged and read like a
            // bug in whatever they had just done.
            return "Ru could not reach the model backend, so the turn did not start (worker_unavailable). Send the message again — if it keeps happening, restart Ru.";
        }

        if (trimmed.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Exception", StringComparison.OrdinalIgnoreCase))
        {
            return "Coding Agent encountered an error. Open Tools → Activity Log to check status.";
        }

        string normalized = trimmed.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 256 ? normalized : normalized[..256] + "…";
    }

    /// <summary>
    /// Copy for a failure that stopped the turn before a run existed: the host
    /// rejected the request, so there is no durable run error code to render and the
    /// API error is all the user has.
    ///
    /// The table lives here with the rest of the code-to-copy mapping instead of
    /// inline in the view, so every branch is asserted rather than read.
    /// </summary>
    internal static string RenderStartFailure(string? errorCode, string? message, string? blockingRunId = null, string? blockingStatus = null)
    {
        string code = errorCode?.Trim() ?? string.Empty;
        if (code.Length == 0)
        {
            return "Coding Agent could not start. Open Tools → Activity Log to check.";
        }

        if (string.Equals(code, "workspace_busy", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(blockingRunId))
        {
            // The server names the blocking run; repeat it so the operator can
            // answer or cancel exactly that run instead of guessing.
            var status = string.IsNullOrWhiteSpace(blockingStatus) ? "active" : blockingStatus.Trim();
            return $"Run {blockingRunId.Trim()} ({status}) still owns this workspace. Answer its pending prompt, or cancel it and try again.";
        }

        switch (code)
        {
            case "invalid_workspace":
                return "Invalid or non-existent workspace folder.";
            case "workspace_busy":
                return "Workspace is currently in use by another Agent session.";
            case "preset_mismatch":
            case "preset_version_conflict":
                return "Permission preset has changed. Please select a preset and try again.";
            case "session_create_failed":
                return "Failed to create a session for Coding Agent. Check backend and try again.";
            case "session_not_found":
            case "invalid_session_id":
                return "Chat session is no longer valid. Send a message to create a new session.";
            case "unauthorized":
                return "Agent session credentials are no longer valid. Please restart backend.";
            case "agent_store_unavailable":
            case "harness_store_unavailable":
                return "Agent database is temporarily unavailable. Check permissions or restart.";
        }

        // An unmapped code still names itself: it is the only handle support has.
        // The server message is only repeated when it adds something the code does
        // not already say.
        bool messageAddsDetail = !string.IsNullOrWhiteSpace(message)
            && !string.Equals(message, code, StringComparison.OrdinalIgnoreCase);

        return messageAddsDetail
            ? $"Coding Agent could not start ({code}: {message}). Open Tools → Activity Log to check."
            : $"Coding Agent could not start ({code}). Open Tools → Activity Log to check.";
    }
}
