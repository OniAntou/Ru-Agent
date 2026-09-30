namespace RuAgent.Cli.Tui;

/// <summary>One cataloged failure: title, cause and fix for a bounded error-code family.</summary>
public sealed record TuiFailureInfo(string Code, string Title, string Cause, string Fix);

/// <summary>
/// The single source of user-facing failure copy for the terminal client. Every
/// bounded error code the Host can end a turn with maps to a title, a cause and
/// an actionable fix; codes outside the table still name themselves and point
/// at the logs instead of rendering a bare token. Copy mirrors the GUI failure
/// presentation where the two surfaces overlap.
/// </summary>
public static class TuiFailureCatalog
{
    public static TuiFailureInfo For(string? code)
    {
        var key = (code ?? string.Empty).Trim();
        if (TryGet(key, out var info))
        {
            return info;
        }

        if (key.Length == 0)
        {
            key = "run_failed";
        }

        return new TuiFailureInfo(
            key,
            "Error",
            $"unrecognized error '{key}'",
            @"check the logs under %LOCALAPPDATA%\Ru\logs and run /status for details");
    }

    public static bool TryGet(string? code, out TuiFailureInfo info)
    {
        var key = (code ?? string.Empty).Trim();
        info = key switch
        {
            "provider_rate_limited" or "rate_limited" => new(
                key,
                "Rate limited",
                "the provider returned HTTP 429 for this turn",
                "provider returned HTTP 429 · retry in a moment or switch model with /model"),
            // A 429 on the catalog read is about listing models, not about this turn,
            // so it points at the model list rather than at the request that just ran.
            "catalog_rate_limited" => new(
                key,
                "Model list rate limited",
                "the provider returned HTTP 429 while listing models",
                "the provider limited how often its model list may be read · retry in a moment"),
            "provider_auth_failed" or "auth_failed" or "unauthorized" => new(
                key,
                "Provider rejected the credentials",
                "the provider refused the API key",
                "provider rejected the credentials · verify the API key with /key, then retry"),
            // The catalog read answers 401/403 both when the key is wrong and when the
            // provider declines that one request, and a failed read stays reported while
            // the retry throttle holds. So this code is not proof the key is bad, and
            // saying it is sends the user to replace a key that was never the problem.
            "catalog_unauthorized" => new(
                key,
                "Provider refused the model list",
                "the key is missing, or the provider declined this one request for the model list",
                "check the key with /key; if it is set, the provider refused a single model-list read · retry in a moment"),
            "provider_not_active" => new(
                key,
                "No provider credential",
                "this provider has no API key saved",
                "provider has no credential · sign in with /key <provider>, then retry"),
            "provider_configuration_changed" => new(
                key,
                "Provider credentials changed",
                "the saved credential no longer matches",
                "provider credentials changed · re-enter the key with /key <provider>, then retry"),
            "forbidden" => new(
                key,
                "Provider refused the request",
                "the model name or key may be wrong",
                "provider refused the request · check the model name and key, then retry"),
            "provider_timeout" or "timeout" or "catalog_timeout" or "transport_error" => new(
                key,
                "Provider timed out",
                "the upstream server may be overloaded",
                "provider timed out · the upstream server may be overloaded"),
            "server_error" or "malformed_payload" => new(
                key,
                "Provider error",
                "the provider returned an error",
                "provider returned an error · retry in a moment or switch model with /model"),
            "http_error" => new(
                key,
                "Provider rejected the request",
                "the provider answered 4xx: unknown model, no access, or bad parameters",
                "check the model with /model, verify the key with /key, then retry"),
            // Distinct from catalog_http_error: nothing was reached, so the network is the
            // suspect. Merging the two told users to check connectivity for a provider
            // that had answered, just not with a model list.
            "catalog_unavailable" => new(
                key,
                "Model catalog unavailable",
                "the provider's model list could not be reached",
                "check the network or the provider's base URL, then retry"),
            "catalog_http_error" => new(
                key,
                "Model list failed",
                "the provider answered an error while listing models",
                "the provider failed to list its models · retry in a moment"),
            "catalog_malformed" or "catalog_too_large" or "catalog_configuration_error" => new(
                key,
                "Model catalog misconfigured",
                "the provider settings need attention",
                "provider catalog misconfigured · check the provider settings, then retry"),
            "response_too_large" => new(
                key,
                "Response too large",
                "the provider reply exceeded the size bound",
                "provider response exceeded the size bound · shorten the prompt or attachments, then retry"),
            "vision_unsupported" => new(
                key,
                "Model has no vision",
                "this model cannot read images",
                "model does not support images · switch to a vision model with /model"),
            "empty_model_reply" => new(
                key,
                "Empty model reply",
                "the model returned no text",
                "the model returned an empty reply · retry the turn or switch model with /model"),
            "model_failed" => new(
                key,
                "Model step failed",
                "the model step failed before producing a reply",
                "retry the turn · run /model or check the API settings with /key"),
            "model_timeout" => new(
                key,
                "Model timed out",
                "the model did not answer in time",
                "retry the turn or switch model with /model"),
            "prompt_too_large" => new(
                key,
                "Prompt too large",
                "the composed prompt exceeded the size bound",
                "shorten the prompt or attachments, or compact the session"),
            "model_unavailable" => new(
                key,
                "Model unavailable",
                "the provider does not serve this model id",
                "the provider 404s this model · pick another with /model (a ':free' suffix only exists where the provider lists it)"),
            // Historical codes: the model worker no longer runs as its own
            // process, so the copy names the backend instead of a worker.
            "worker_unavailable" or "worker_failed" => new(
                key,
                "Model backend unreachable",
                "the model backend could not be reached; nothing about the request was wrong",
                "send the message again — if it keeps happening, restart Ru"),
            "provider_failed" or "generation_failed" or "drive_failed" => new(
                key,
                "Provider execution failed",
                "the model request failed before producing a reply",
                "provider execution failed · run /model or check the API settings"),
            "provider_bootstrap_failed" => new(
                key,
                "Provider setup failed",
                "the model backend could not start the provider session",
                "check the API key and model, then retry"),
            "provider_auth" => new(
                key,
                "Provider rejected the credentials",
                "the provider refused the API key (account or permission issue)",
                "re-enter the key with /key, then retry"),
            "provider_server_error" => new(
                key,
                "Provider server error",
                "the provider failed on its side",
                "retry in a moment or switch model with /model"),
            "provider_transport_error" => new(
                key,
                "Provider unreachable",
                "the provider could not be reached over the network",
                "check the network, then retry"),
            "provider_bootstrap_invalid" => new(
                key,
                "Provider setup invalid",
                "the provider session data was rejected",
                "check the API key and model, then retry"),
            "capability_unavailable" => new(
                key,
                "Capability unavailable",
                "the requested tool capability is not advertised",
                "rephrase your request and send it again"),
            // Historical code: the model step no longer crosses a worker
            // protocol, so the copy names the backend instead of a worker.
            "protocol_failure" => new(
                key,
                "Model protocol failure",
                "the model backend exchanged a malformed message",
                "retry the turn; if it repeats, restart Ru"),
            "runtime_drive_failed" => new(
                key,
                "Run driver failed",
                "the agent loop stopped on an internal error",
                "send the request again; if it repeats, restart Ru and check the logs"),
            "repeated_tool_call" => new(
                key,
                "Repeated tool call",
                "the model repeated the same action instead of answering",
                "rephrase your request and send it again"),
            "invalid_selection" => new(
                key,
                "Unrunnable model action",
                "the model produced an action the host could not run",
                "rephrase your request and send it again"),
            "model_tool_call_invalid" => new(
                key,
                "Invalid model tool call",
                "the model produced a tool call the host could not run",
                "rephrase and retry; if the same model repeats this, switch model with /model"),
            "model_tool_call_unavailable" => new(
                key,
                "Unavailable model tool call",
                "the model tried a tool that is not available",
                "rephrase your request and send it again"),
            "prompt_contract_invalid" => new(
                key,
                "Prompt contract invalid",
                "the composed prompt failed validation",
                "retry the turn; if it repeats, restart Ru"),
            "invalid_request" => new(
                key,
                "Request rejected",
                "the host could not assemble the model request from this turn",
                "send the request again; if it repeats, start a fresh session"),
            "run_not_runnable" => new(
                key,
                "Run not runnable",
                "the run is no longer in a runnable state",
                "send the request again to start a fresh run"),
            "run_failed" => new(
                key,
                "Run failed",
                "the turn ended in failure",
                "retry the turn; run /status for details"),
            "workspace_busy" or "session_busy" => new(
                key,
                "Workspace busy",
                "another Agent run still owns this workspace",
                "release the blocking run with 'ru agent cancel <run-id>'"),
            "reconciliation_required" => new(
                key,
                "Run paused for inspection",
                "the last action was interrupted so its result is unverified",
                "send Continue to resume with a read-only inspection, or Stop to end the run"),
            "reconcile_stale" => new(
                key,
                "Reconciliation outdated",
                "the workspace changed since the snapshot was taken",
                "re-run the reconcile step to get a fresh snapshot hash"),
            "approval_denied" => new(
                key,
                "Approval denied",
                "the run stopped before that tool executed",
                "send the request again and approve the action"),
            "approval_expired" => new(
                key,
                "Approval expired",
                "the approval expired before it was answered",
                "send the request again"),
            "approval_stale" => new(
                key,
                "Approval already answered",
                "the approval was already used or replaced",
                "send the request again if the action is still needed"),
            "tool_failed" => new(
                key,
                "Tool failed",
                "a tool call returned a failure",
                "fetch the tool output with 'agent output <run-id> <payload-id>' and retry"),
            "harness_store_unavailable" or "agent_store_unavailable" => new(
                key,
                "Agent database unavailable",
                "the local database is locked or unavailable",
                "check permissions or restart Ru"),
            "policy_snapshot_unavailable" => new(
                key,
                "Policy unavailable",
                "the permission snapshot could not be loaded",
                "policy snapshot unavailable · restart the backend, then retry"),
            "workspace_membership_expired" => new(
                key,
                "Workspace lease expired",
                "the workspace lease expired mid-turn",
                "the workspace lease expired · retry the turn"),
            "session_create_failed" => new(
                key,
                "Session creation failed",
                "the host could not create a chat session",
                "check the backend is running and try again"),
            "session_not_found" or "invalid_session_id" => new(
                key,
                "Session expired",
                "the chat session is no longer valid",
                "send a message to create a new session"),
            "invalid_workspace" => new(
                key,
                "Invalid workspace",
                "the workspace folder is invalid or missing",
                "open a valid project folder and try again"),
            "preset_mismatch" or "preset_version_conflict" => new(
                key,
                "Preset changed",
                "the permission preset changed mid-run",
                "select a preset and try again"),
            "compact_failed" => new(
                key,
                "Compaction failed",
                "the context compaction step failed",
                "retry; if it repeats, start a fresh session"),
            "command_invalid" or "command_unknown" => new(
                key,
                "Unknown command",
                "that slash command was not recognized",
                "type /help for the command list"),
            _ => null!,
        };

        return info is not null;
    }
}
