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
        if (!IsSingleProviderKey(payload))
            return Task.FromResult<IResult>(Results.BadRequest(new { status = "error", code = "invalid_payload" }));
        payload[SettingsTransitionService.ValidateKeyField] = true;
        return SaveSettingsAsync(payload, transition, loggers, cancellationToken);
    }

    /// <summary>
    /// Exactly one key and nothing else: a Models.dev or named custom provider's id + key
    /// pair, or the OpenAI, Anthropic or Custom OpenAI key field. The route can never
    /// change a selection or an endpoint.
    /// </summary>
    private static bool IsSingleProviderKey(JsonObject payload) => payload.Count switch
    {
        2 => (payload.ContainsKey("models_dev_provider_id") && payload.ContainsKey("models_dev_api_key"))
            || (payload.ContainsKey("custom_provider_id") && payload.ContainsKey("custom_provider_api_key")),
        1 => payload.ContainsKey("openai_api_key") || payload.ContainsKey("anthropic_api_key")
            || payload.ContainsKey("custom_api_key"),
        _ => false,
    };
}
