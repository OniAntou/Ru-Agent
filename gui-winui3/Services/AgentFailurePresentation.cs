using RuAgent.Client;

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

    /// <summary>
    /// Copy for codes whose desktop wording is pinned by tests and product copy.
    /// Everything else resolves from <see cref="AgentFailureCopy.Table"/> and is
    /// composed into one line. These stay outside the shared table only because
    /// the desktop sentence differs from the shared terminal wording; the code
    /// set itself is checked against the table by the parity test.
    /// </summary>
    private static readonly Dictionary<string, string> DesktopOverrides = new(StringComparer.Ordinal)
    {
        ["provider_rate_limited"] = "Model is rate-limited. Please select another model and try again.",
        ["workspace_busy"] = "Coding Agent is already running. Please complete or stop the current turn first.",
        ["repeated_tool_call"] = "Ru repeated the same action instead of answering, so the run was stopped. Please rephrase your request and send it again.",
        ["invalid_selection"] = "Ru produced an action the host could not run, so the run was stopped. Please rephrase your request and send it again.",
        // The runner rejected the assembled request before the provider was
        // called (a malformed history or a bad image payload), so the model
        // never saw the turn: retrying the same turn can repeat it.
        ["invalid_request"] = "Ru could not assemble the model request, so the run was stopped. Please send the request again — if it keeps happening, start a new chat.",
        ["empty_model_reply"] = "The model returned an empty reply. Please try again, or select another model.",
        // Parked, not failed. The host stops driving the run until a
        // reconciliation decision arrives; Continue is that decision, and it
        // resumes with a read-only inspection in front of further mutations.
        ["reconciliation_required"] = "The last action was interrupted, so its result is unverified. Ru is paused — send Continue to resume with a read-only inspection, or Stop to end the run.",
        // Historical code: the model worker used to be its own process, and the
        // run could not reach it. Nothing about the request was wrong, so the copy
        // asks for a retry instead of a rephrase — and it says the code out loud,
        // because the raw token used to reach the reader unchanged and read like a
        // bug in whatever they had just done.
        ["worker_unavailable"] = "Ru could not reach the model backend, so the turn did not start (worker_unavailable). Send the message again — if it keeps happening, restart Ru.",
    };

    /// <summary>The codes the desktop surface renders with copy of its own; kept for the parity test.</summary>
    internal static IReadOnlyCollection<string> OverrideCodes => DesktopOverrides.Keys;

    internal static bool ShouldKeepVisible(string? runStatus, string? safeStatus) =>
        string.Equals(runStatus, "failed", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(Render(safeStatus));

    /// <summary>
    /// Keeps terminal failure visible above the composer. The transcript does
    /// not render a terminal summary card, and completed thinking rows alone
    /// cannot explain why no answer arrived.
    /// </summary>
    internal static string InlineFailureStatus(
        string? runStatus,
        string? failureReason,
        bool isRunning,
        bool hasRenderedReply)
        => Render(failureReason);

    internal static string Render(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)
            || string.Equals(status.Trim(), "Run ended.", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        string trimmed = status.Trim();
        if (DesktopOverrides.TryGetValue(trimmed, out var pinned))
        {
            return pinned;
        }

        if (AgentFailureCopy.TryGet(trimmed, out var copy))
        {
            return ComposeSingleLine(copy);
        }

        if (trimmed.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Exception", StringComparison.OrdinalIgnoreCase))
        {
            return "Coding Agent encountered an error. Open Tools → Activity Log to check status.";
        }

        return Bound(trimmed.Replace('\r', ' ').Replace('\n', ' ').Trim());
    }

    /// <summary>
    /// Renders the shared title/cause/fix copy as the desktop one-liner: the
    /// failure title followed by the actionable fix. The cause line stays in the
    /// terminal card, where the three-line shape has room for it.
    /// </summary>
    private static string ComposeSingleLine(FailureCopy copy)
    {
        string title = copy.Title.Trim();
        string fix = copy.Fix.Trim();
        string sentence = fix.Length == 0
            ? title
            : char.IsLetter(fix[0])
                ? char.ToUpperInvariant(fix[0]) + fix[1..]
                : fix;

        string composed = title.Length == 0 ? sentence : $"{title}. {sentence}";
        composed = composed.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (composed.Length > 0
            && !composed.EndsWith('.')
            && !composed.EndsWith('!')
            && !composed.EndsWith('?'))
        {
            composed += ".";
        }

        return Bound(composed);
    }

    private static string Bound(string text) =>
        text.Length <= 256 ? text : text[..256] + "…";

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
            // answer or cancel exactly that run instead of guessing. A parked run
            // has no pending prompt: reconciliation needs a Continue (cancelling it
            // would lose the inspection) and waiting_user only needs the Continue.
            var status = string.IsNullOrWhiteSpace(blockingStatus) ? "active" : blockingStatus.Trim();
            string runId = blockingRunId.Trim();
            if (string.Equals(status, "reconciliation_required", StringComparison.Ordinal))
            {
                return $"Run {runId} is paused for inspection. Send Continue to resume it (or cancel it), then try again.";
            }

            if (string.Equals(status, "waiting_user", StringComparison.Ordinal))
            {
                return $"Run {runId} is waiting to continue. Send Continue to resume it, then try again.";
            }

            return $"Run {runId} ({status}) still owns this session. Answer its pending prompt, or cancel it and try again.";
        }

        switch (code)
        {
            case "invalid_workspace":
                return "Invalid or non-existent workspace folder.";
            case "workspace_busy":
                return "This session already has an active Agent run. Wait for it to finish, or cancel it and try again.";
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
