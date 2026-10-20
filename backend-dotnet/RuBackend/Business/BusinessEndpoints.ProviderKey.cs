using System.Text.Json.Nodes;
using RuBackend.Business.Settings;

namespace RuBackend.Business;

public static partial class BusinessEndpoints
{
    private static Task<IResult> SaveValidatedProviderKeyAsync(JsonObject payload,
        ISettingsTransitionService transition, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        // A separate route makes old Hosts fail closed with 404 instead of
        // silently treating validation as an ordinary settings write.
        if (payload.Count != 2 || !payload.ContainsKey("models_dev_provider_id") || !payload.ContainsKey("models_dev_api_key"))
            return Task.FromResult<IResult>(Results.BadRequest(new { status = "error", code = "invalid_payload" }));
        payload["validate_models_dev_key"] = true;
        return SaveSettingsAsync(payload, transition, loggers, cancellationToken);
    }
}
