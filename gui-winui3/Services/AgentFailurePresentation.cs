namespace RuApp.Services;

internal static class AgentFailurePresentation
{
    internal static bool ShouldKeepVisible(string? runStatus, string? safeStatus) =>
        string.Equals(runStatus, "failed", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(Render(safeStatus));

    internal static string Render(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)
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
            return "Ru repeated the same action instead of answering, so the run was stopped. Please rephrase your request, or type Continue to resume.";
        }

        if (string.Equals(trimmed, "invalid_selection", StringComparison.Ordinal))
        {
            return "Ru produced an action the host could not run, so the run was stopped. Please rephrase your request, or type Continue to resume.";
        }

        if (string.Equals(trimmed, "empty_model_reply", StringComparison.Ordinal))
        {
            return "The model returned an empty reply. Please try again, or select another model.";
        }

        if (trimmed.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Exception", StringComparison.OrdinalIgnoreCase))
        {
            return "Coding Agent encountered an error. Open Tools → Activity Log to check status.";
        }

        string normalized = trimmed.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 256 ? normalized : normalized[..256] + "…";
    }
}
