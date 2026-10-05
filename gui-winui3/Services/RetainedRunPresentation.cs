using RuApp.Models;

namespace RuApp.Services;

public static class RetainedRunPresentation
{
    public static int? PublishedContextWindow(AgentRunModel? run, string? activeSessionId)
    {
        if (run is null
            || !string.Equals(run.SessionId, activeSessionId, StringComparison.Ordinal)
            || run.ContextWindowTokens is null or <= 0 or > int.MaxValue)
        {
            return null;
        }

        return (int)run.ContextWindowTokens.Value;
    }

    public static string Identity(AgentRunModel run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var model = run.ProviderModel ?? "model unknown";
        var modelLabel = string.IsNullOrWhiteSpace(run.ProviderModelLabel) ? model : run.ProviderModelLabel;
        var provider = string.IsNullOrWhiteSpace(run.ProviderBackend)
            ? "provider unknown"
            : run.ProviderBackend;
        var protocol = string.IsNullOrWhiteSpace(run.ProviderProtocol)
            ? "protocol unknown"
            : run.ProviderProtocol;
        var mode = string.IsNullOrWhiteSpace(run.ProviderCallingMode)
            ? "mode unknown"
            : run.ProviderCallingMode;

        var context = ModelContextSpecs.FormatPublishedContext(
            run.ContextWindowTokens is > 0 and <= int.MaxValue ? (int)run.ContextWindowTokens.Value : null) ?? "unknown";
        var source = string.IsNullOrWhiteSpace(run.ContextWindowSource) ? "source unknown" : run.ContextWindowSource;
        var version = string.IsNullOrWhiteSpace(run.ContextWindowVersion) ? "version unknown" : run.ContextWindowVersion;
        var reserve = run.OutputTokenReserve?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";

        return $"Run pin: {modelLabel} [{model}] · {provider} · {protocol}/{mode} · window {context} ({source}/{version}) · output reserve {reserve}";
    }
}
