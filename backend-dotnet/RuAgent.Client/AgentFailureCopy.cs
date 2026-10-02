using System.Diagnostics.CodeAnalysis;

namespace RuAgent.Client;

/// <summary>One cataloged failure: title, cause and fix for a bounded error-code family.</summary>
public sealed record FailureCopy(string Title, string Cause, string Fix);

/// <summary>
/// The single source of user-facing failure copy shared by every surface. Every
/// bounded error code the Host, the Broker or the RPC bridge can end a turn with
/// maps to a title, a cause and an actionable fix. Aliases (the same failure under
/// more than one wire code) map to the same copy. Codes outside the table still
/// name themselves on the surface that renders them instead of disappearing.
/// </summary>
public static class AgentFailureCopy
{
    /// <summary>
    /// Every cataloged code, keyed by its exact wire token. The table is
    /// dependency-free so both the terminal and the desktop client can resolve
    /// from it.
    /// </summary>
    public static IReadOnlyDictionary<string, FailureCopy> Table { get; } = BuildTable();

    /// <summary>Resolves a wire code to its cataloged copy; blank and unknown codes return false.</summary>
    public static bool TryGet(string? code, [NotNullWhen(true)] out FailureCopy? copy)
    {
        var key = (code ?? string.Empty).Trim();
        if (key.Length == 0)
        {
            copy = null;
            return false;
        }

        return Table.TryGetValue(key, out copy);
    }

    private static Dictionary<string, FailureCopy> BuildTable()
    {
        var table = new Dictionary<string, FailureCopy>(StringComparer.Ordinal);

        // Provider credentials and reachability.
        Add(table, ["provider_auth_failed", "auth_failed", "unauthorized"],
            "Provider rejected the credentials",
            "the provider refused the API key",
            "provider rejected the credentials · verify the API key with /key, then retry");
        Add(table, ["provider_not_active"],
            "No provider credential",
            "this provider has no API key saved",
            "provider has no credential · sign in with /key <provider>, then retry");
        Add(table, ["provider_configuration_changed"],
            "Provider credentials changed",
            "the saved credential no longer matches",
            "provider credentials changed · re-enter the key with /key <provider>, then retry");
        Add(table, ["provider_auth"],
            "Provider rejected the credentials",
            "the provider refused the API key (account or permission issue)",
            "re-enter the key with /key, then retry");
        Add(table, ["forbidden"],
            "Provider refused the request",
            "the model name or key may be wrong",
            "provider refused the request · check the model name and key, then retry");
        Add(table, ["http_error"],
            "Provider rejected the request",
            "the provider answered 4xx: unknown model, no access, or bad parameters",
            "check the model with /model, verify the key with /key, then retry");
        Add(table, ["provider_server_error"],
            "Provider server error",
            "the provider failed on its side",
            "retry in a moment or switch model with /model");
        Add(table, ["provider_transport_error"],
            "Provider unreachable",
            "the provider could not be reached over the network",
            "check the network, then retry");
        Add(table, ["provider_bootstrap_failed"],
            "Provider setup failed",
            "the model backend could not start the provider session",
            "check the API key and model, then retry");
        Add(table, ["provider_bootstrap_invalid"],
            "Provider setup invalid",
            "the provider session data was rejected",
            "check the API key and model, then retry");

        // Provider rate limits and transport.
        Add(table, ["provider_rate_limited", "rate_limited"],
            "Rate limited",
            "the provider returned HTTP 429 for this turn",
            "provider returned HTTP 429 · retry in a moment or switch model with /model");
        // A 429 on the catalog read is about listing models, not about this turn,
        // so it points at the model list rather than at the request that just ran.
        Add(table, ["catalog_rate_limited"],
            "Model list rate limited",
            "the provider returned HTTP 429 while listing models",
            "the provider limited how often its model list may be read · retry in a moment");
        // The catalog read answers 401/403 both when the key is wrong and when the
        // provider declines that one request, and a failed read stays reported while
        // the retry throttle holds. So this code is not proof the key is bad, and
        // saying it is sends the user to replace a key that was never the problem.
        Add(table, ["catalog_unauthorized"],
            "Provider refused the model list",
            "the key is missing, or the provider declined this one request for the model list",
            "check the key with /key; if it is set, the provider refused a single model-list read · retry in a moment");
        Add(table, ["provider_timeout", "timeout", "catalog_timeout", "transport_error"],
            "Provider timed out",
            "the upstream server may be overloaded",
            "provider timed out · the upstream server may be overloaded");
        Add(table, ["server_error", "malformed_payload"],
            "Provider error",
            "the provider returned an error",
            "provider returned an error · retry in a moment or switch model with /model");

        // Model catalog reachability and shape.
        // Distinct from catalog_http_error: nothing was reached, so the network is the
        // suspect. Merging the two told users to check connectivity for a provider
        // that had answered, just not with a model list.
        Add(table, ["catalog_unavailable"],
            "Model catalog unavailable",
            "the provider's model list could not be reached",
            "check the network or the provider's base URL, then retry");
        Add(table, ["catalog_http_error"],
            "Model list failed",
            "the provider answered an error while listing models",
            "the provider failed to list its models · retry in a moment");
        Add(table, ["catalog_malformed", "catalog_too_large", "catalog_configuration_error"],
            "Model catalog misconfigured",
            "the provider settings need attention",
            "provider catalog misconfigured · check the provider settings, then retry");

        // Model steps and replies.
        Add(table, ["response_too_large"],
            "Response too large",
            "the provider reply exceeded the size bound",
            "provider response exceeded the size bound · shorten the prompt or attachments, then retry");
        Add(table, ["vision_unsupported"],
            "Model has no vision",
            "this model cannot read images",
            "model does not support images · switch to a vision model with /model");
        Add(table, ["image_limit_exceeded"],
            "Too many images",
            "a turn can carry at most 4 images",
            "remove images with /detach, then retry");
        Add(table, ["image_too_large"],
            "Image too large",
            "one image or the whole turn exceeded the size bound",
            "images cap at 4MB each and 20MB per turn · resize or remove some");
        Add(table, ["invalid_image_mime"],
            "Unsupported image type",
            "images must be png, jpeg or webp",
            "convert the image to png/jpeg/webp, then attach it again");
        Add(table, ["invalid_image_base64"],
            "Corrupt image payload",
            "the image data could not be decoded",
            "attach the image again; if it keeps failing, re-save it as png/jpeg");
        Add(table, ["empty_model_reply"],
            "Empty model reply",
            "the model returned no text",
            "the model returned an empty reply · retry the turn or switch model with /model");
        Add(table, ["model_failed"],
            "Model step failed",
            "the model step failed before producing a reply",
            "retry the turn · run /model or check the API settings with /key");
        Add(table, ["model_timeout"],
            "Model timed out",
            "the model did not answer in time",
            "retry the turn or switch model with /model");
        Add(table, ["prompt_too_large"],
            "Prompt too large",
            "the composed prompt exceeded the size bound",
            "shorten the prompt or attachments, or compact the session");
        Add(table, ["model_unavailable"],
            "Model unavailable",
            "the provider does not serve this model id",
            "the provider 404s this model · pick another with /model (a ':free' suffix only exists where the provider lists it)");
        // Historical codes: the model worker no longer runs as its own
        // process, so the copy names the backend instead of a worker.
        Add(table, ["worker_unavailable", "worker_failed"],
            "Model backend unreachable",
            "the model backend could not be reached; nothing about the request was wrong",
            "send the message again — if it keeps happening, restart Ru");
        Add(table, ["provider_failed", "generation_failed", "drive_failed"],
            "Provider execution failed",
            "the model request failed before producing a reply",
            "provider execution failed · run /model or check the API settings");
        // Historical code: the model step no longer crosses a worker
        // protocol, so the copy names the backend instead of a worker.
        Add(table, ["protocol_failure"],
            "Model protocol failure",
            "the model backend exchanged a malformed message",
            "retry the turn; if it repeats, restart Ru");
        Add(table, ["runtime_drive_failed"],
            "Run driver failed",
            "the agent loop stopped on an internal error",
            "send the request again; if it repeats, restart Ru and check the logs");

        // Tool and model-step contract failures.
        Add(table, ["capability_unavailable"],
            "Capability unavailable",
            "the requested tool capability is not advertised",
            "rephrase your request and send it again");
        Add(table, ["repeated_tool_call"],
            "Repeated tool call",
            "the model repeated the same action instead of answering",
            "rephrase your request and send it again");
        Add(table, ["invalid_selection"],
            "Unrunnable model action",
            "the model produced an action the host could not run",
            "rephrase your request and send it again");
        Add(table, ["model_tool_call_invalid"],
            "Invalid model tool call",
            "the model produced a tool call the host could not run",
            "rephrase and retry; if the same model repeats this, switch model with /model");
        Add(table, ["model_tool_call_unavailable"],
            "Unavailable model tool call",
            "the model tried a tool that is not available",
            "rephrase your request and send it again");
        Add(table, ["prompt_contract_invalid"],
            "Prompt contract invalid",
            "the composed prompt failed validation",
            "retry the turn; if it repeats, restart Ru");
        Add(table, ["invalid_request"],
            "Request rejected",
            "the host could not assemble the model request from this turn",
            "send the request again; if it repeats, start a fresh session");
        Add(table, ["unverified_after_tool_failure"],
            "Tool result unverified",
            "a tool failed and the run stopped before its result could be verified",
            "check the workspace, then send the request again");

        // Run lifecycle.
        Add(table, ["run_not_runnable"],
            "Run not runnable",
            "the run is no longer in a runnable state",
            "send the request again to start a fresh run");
        Add(table, ["run_not_parked"],
            "Run is not paused",
            "the run is not waiting to continue",
            "send a new message, or check the run state first");
        Add(table, ["run_failed"],
            "Run failed",
            "the turn ended in failure",
            "retry the turn; run /status for details");
        Add(table, ["workspace_busy", "session_busy"],
            "Workspace busy",
            "another Agent run still owns this workspace",
            "release the blocking run with 'ru agent cancel <run-id>'");
        Add(table, ["reconciliation_required"],
            "Run paused for inspection",
            "the last action was interrupted so its result is unverified",
            "send Continue to resume with a read-only inspection, or Stop to end the run");
        Add(table, ["reconcile_stale"],
            "Reconciliation outdated",
            "the workspace changed since the snapshot was taken",
            "re-run the reconcile step to get a fresh snapshot hash");
        Add(table, ["reconcile_failed"],
            "Reconciliation failed",
            "the reconcile step returned an error without a code",
            "run /status, then retry with a fresh reconcile");

        // Approvals.
        Add(table, ["approval_denied"],
            "Approval denied",
            "the run stopped before that tool executed",
            "send the request again and approve the action");
        Add(table, ["approval_expired"],
            "Approval expired",
            "the approval expired before it was answered",
            "send the request again");
        Add(table, ["approval_stale"],
            "Approval already answered",
            "the approval was already used or replaced",
            "send the request again if the action is still needed");
        Add(table, ["approval_authorization_failed"],
            "Approval not authorized",
            "the approval was recorded but the execution authority did not authorize it",
            "retry the approval; if it repeats, restart the backend");
        Add(table, ["broker_gateway_unavailable"],
            "Execution authority unavailable",
            "the approval needs the broker gateway, which is not configured",
            "start the broker, then retry the approval");

        // Budgets and context.
        Add(table, ["policy_denied"],
            "Blocked by policy",
            "the permission preset denied this tool call",
            "switch preset or rephrase the request, then retry");
        Add(table, ["model_step_budget_exhausted"],
            "Model step budget used up",
            "the run reached its 128 model steps for this budget window",
            "send /continue to open a fresh budget window, or stop the run");
        Add(table, ["tool_call_budget_exhausted"],
            "Tool call budget used up",
            "the run reached its 256 tool calls for this budget window",
            "send /continue to open a fresh budget window, or stop the run");
        Add(table, ["context_limit_reached"],
            "Context limit reached",
            "the conversation is over the model's context window even after compaction",
            "run /compact or start a fresh session, then retry");
        Add(table, ["commit_conflict"],
            "Run changed during the step",
            "another writer changed the run before this step could commit",
            "retry the step; the run was left unchanged");

        // Workspace, tool execution and recovery.
        Add(table, ["workspace_changed"],
            "Workspace changed",
            "the file changed on disk since it was read, so the write was refused",
            "re-read the file and retry the change");
        Add(table, ["process_timed_out"],
            "Command timed out",
            "the command ran past its timeout and was stopped",
            "raise the timeout or split the command, then retry");
        Add(table, ["git_timed_out"],
            "Git timed out",
            "a git command ran past its timeout and was stopped",
            "retry the git action, or run git in the terminal yourself");
        Add(table, ["invalid_terminal_write_set"],
            "Invalid terminal write set",
            "the tool call's declared write_set was rejected by the host",
            "send the request again; if it repeats, switch model with /model");
        Add(table, ["permission_profile_changed"],
            "Permission profile changed",
            "the run's saved permission profile no longer matches the current one",
            "send the request again to start a run under the current preset");
        Add(table, ["recovery_pre_dispatch"],
            "Run interrupted before any tool ran",
            "the host stopped before dispatching a tool, so nothing was changed",
            "send the request again");
        Add(table, ["recovery_read_only_incomplete"],
            "Read-only check incomplete",
            "the host stopped during a read-only step, so its result is unknown",
            "send the request again; no workspace change was left behind");

        // Web tools.
        Add(table, ["web_response_too_large"],
            "Web response too large",
            "the fetched page exceeded the size the host will read",
            "retry with a smaller page or a different source");
        Add(table, ["web_search_parse_failed"],
            "Search reply unreadable",
            "the search provider returned a body the host could not parse",
            "retry the search; if it repeats, check the search endpoint setting");
        Add(table, ["web_redirect_limit"],
            "Too many redirects",
            "the page redirected more times than the host will follow",
            "retry with the final URL or a different source");
        Add(table, ["web_redirect_invalid"],
            "Invalid redirect",
            "the page redirected to a URL the host will not follow",
            "retry with a direct URL");
        Add(table, ["web_content_type_not_supported"],
            "Page type not supported",
            "the URL returned a content type that is not readable text",
            "fetch an HTML or text page instead");
        Add(table, ["web_json_invalid"],
            "Search reply not JSON",
            "the search endpoint returned a body that is not valid JSON",
            "check the search endpoint setting, then retry");
        Add(table, ["web_encoding_invalid"],
            "Page encoding invalid",
            "the page bytes are not valid text in the declared encoding",
            "retry with a different source");
        Add(table, ["web_uri_not_allowed"],
            "URL not allowed",
            "the URL is outside the host's web policy",
            "use an allowed http or https URL, then retry");
        Add(table, ["web_private_host"],
            "Private host blocked",
            "the URL points at a private or local address, which the web policy blocks",
            "use a public host, then retry");
        Add(table, ["web_connect_failed"],
            "Web connection failed",
            "the host could not be reached",
            "check the network or the URL, then retry");
        Add(table, ["web_disabled"],
            "Web access disabled",
            "web search and fetch are disabled in the settings",
            "enable web access, then retry");
        Add(table, ["web_uri_invalid"],
            "Invalid URL",
            "the URL could not be parsed",
            "fix the URL, then retry");
        Add(table, ["web_search_endpoint_not_allowed"],
            "Search endpoint not allowed",
            "the configured search endpoint is not in the allowed list",
            "check the search endpoint setting, then retry");

        // Stores, sessions, presets and commands.
        Add(table, ["tool_failed"],
            "Tool failed",
            "a tool call returned a failure",
            "fetch the tool output with 'agent output <run-id> <payload-id>' and retry");
        Add(table, ["harness_store_unavailable", "agent_store_unavailable"],
            "Agent database unavailable",
            "the local database is locked or unavailable",
            "check permissions or restart Ru");
        Add(table, ["policy_snapshot_unavailable"],
            "Policy unavailable",
            "the permission snapshot could not be loaded",
            "policy snapshot unavailable · restart the backend, then retry");
        Add(table, ["workspace_membership_expired"],
            "Workspace lease expired",
            "the workspace lease expired mid-turn",
            "the workspace lease expired · retry the turn");
        Add(table, ["session_create_failed"],
            "Session creation failed",
            "the host could not create a chat session",
            "check the backend is running and try again");
        Add(table, ["session_not_found", "invalid_session_id"],
            "Session expired",
            "the chat session is no longer valid",
            "send a message to create a new session");
        Add(table, ["invalid_workspace"],
            "Invalid workspace",
            "the workspace folder is invalid or missing",
            "open a valid project folder and try again");
        Add(table, ["invalid_preset"],
            "Invalid preset",
            "the preset must be plan, standard, or full_access",
            "pick a valid preset, then retry");
        Add(table, ["preset_mismatch", "preset_version_conflict"],
            "Preset changed",
            "the permission preset changed mid-run",
            "select a preset and try again");
        Add(table, ["compact_failed"],
            "Compaction failed",
            "the context compaction step failed",
            "retry; if it repeats, start a fresh session");
        Add(table, ["command_invalid", "command_unknown"],
            "Unknown command",
            "that slash command was not recognized",
            "type /help for the command list");

        // Client/backend protocol and frame bounds.
        Add(table, ["trust_failed"],
            "Workspace trust failed",
            "the trust challenge was missing, already resolved, or did not match",
            "send the request again and answer the workspace trust prompt");
        Add(table, ["full_access_confirmation_required"],
            "Full access needs confirmation",
            "the full_access preset requires an explicit confirmation",
            "re-run with --confirm-full-access, or pick another preset");
        Add(table, ["output_frame_exceeds_limit"],
            "Output too large to stream",
            "the payload exceeded the 1 MiB RPC frame cap",
            "fetch the output with the CLI 'agent output' command");
        Add(table, ["unsupported_protocol"],
            "Protocol mismatch",
            "the client and backend speak different bridge protocol versions",
            "update Ru so both sides match, then retry");
        Add(table, ["unsupported_protocol_version"],
            "Protocol version unsupported",
            "the event stream uses a protocol version this build does not understand",
            "update Ru, then retry");

        return table;
    }

    private static void Add(
        Dictionary<string, FailureCopy> table,
        string[] codes,
        string title,
        string cause,
        string fix)
    {
        var copy = new FailureCopy(title, cause, fix);
        foreach (var code in codes)
        {
            table.Add(code, copy);
        }
    }
}
