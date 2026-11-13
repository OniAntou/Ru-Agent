namespace RuBackend.Harness.Subagents;

/// <summary>
/// Host-side control failures that are decided before or inside a single
/// transaction and therefore cannot have committed the requested effect. They
/// must reach the model as a failed (verified) tool result, not as an unknown
/// outcome that pauses the root run.
/// </summary>
internal static class SubagentControlFailures
{
    private static readonly HashSet<string> DeterministicCodes = new(StringComparer.Ordinal)
    {
        "invalid_subagent_arguments",
        "invalid_subagent_control",
        "subagent_arguments_invalid",
        "subagent_attempt_not_queued",
        "subagent_brief_invalid",
        "subagent_canceled",
        "subagent_context_invalid",
        "subagent_context_too_large",
        "subagent_control_binding_invalid",
        "subagent_identity_limit",
        "subagent_not_found",
        "subagent_outbox_denied",
        "subagent_preparation_incomplete",
        "subagent_profile_invalid",
        "subagent_profile_not_found",
        "subagent_profile_role_mismatch",
        "subagent_role_denied",
        "subagent_root_inactive",
        "subagent_root_not_found",
        "idempotency_conflict",
        "session_deleted",
        "workspace_not_found",
    };

    public static bool IsDeterministic(string? code) =>
        code is not null && DeterministicCodes.Contains(code);
}
