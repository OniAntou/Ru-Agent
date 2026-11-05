using System.Text.Json.Nodes;
using RuAgent.Protocol;
using RuAgent.Runtime.Adapters.Models;

namespace RuAgent.Runtime.Model;

public sealed partial class ModelStepService
{
    /// <summary>
    /// The single authoritative provider-code mapping. Retryability is a
    /// property of the row, not of the exception's flag: adapter call sites set
    /// <see cref="ModelProviderException.Retryable"/> for the codes that pass
    /// through unchanged (<c>http_error</c>, <c>redirect_rejected</c>, <c>vision_unsupported</c>,
    /// <c>empty_model_reply</c>, and unknown codes), while a listed code always
    /// carries the retryability decided here.
    /// </summary>
    private static ModelStepException MapProviderException(ModelProviderException error) => error.Code switch
    {
        "timeout" => new ModelStepException("provider_timeout", retryable: true, error),
        "auth_failed" or "forbidden" => new ModelStepException("provider_auth", retryable: false, error),
        "rate_limited" => new ModelStepException("provider_rate_limited", retryable: true,
            innerException: error, retryAfter: error.RetryAfter),
        "quota_exhausted" => new ModelStepException("provider_quota_exhausted", retryable: false, error),
        "server_error" => new ModelStepException("provider_server_error", retryable: true,
            innerException: error, retryAfter: error.RetryAfter),
        "transport_error" => new ModelStepException("provider_transport_error", retryable: true, error),
        // Split on purpose: an unreadable chunk is a per-response accident and
        // the next request is a fresh draw, so the Host's bounded retry is the
        // right response. A response that is simply too large is a ceiling --
        // retrying spends the attempt budget to reach the same ceiling.
        "malformed_payload" => new ModelStepException("protocol_failure", retryable: true, error),
        "provider_continuation_too_large" => new ModelStepException(error.Code, false, error),
        "response_too_large" => new ModelStepException("protocol_failure", retryable: false, error),
        "http_error" or "redirect_rejected" or "vision_unsupported" or "empty_model_reply"
            or "provider_web_search_unavailable" or "provider_search_paused"
            or "model_tools_unsupported" => new ModelStepException(error.Code, error.Retryable, error),
        _ => new ModelStepException("generation_failed", error.Retryable, error),
    };

    internal const string ProviderWebSearchSetting = RuAgent.Runtime.Adapters.Models.ModelRequest.ProviderWebSearchSetting;

    private static JsonObject BuildWireSettings(ModelGenerationSettings settings)
    {
        var wire = new JsonObject();
        if (settings.ModelTools is not null)
        {
            wire["model_tools"] = settings.ModelTools.DeepClone();
        }

        if (settings.MaxOutputTokens is not null)
        {
            wire["max_tokens"] = settings.MaxOutputTokens.Value;
        }

        if (settings.ProviderWebSearch)
        {
            wire[ProviderWebSearchSetting] = true;
        }

        return wire;
    }

    private static JsonObject BuildRequestSettings(
        JsonObject untrustedSettings,
        JsonObject? protectedRequestOptions)
    {
        var settings = (JsonObject)untrustedSettings.DeepClone();
        foreach (var field in UntrustedProviderOptionFields)
        {
            settings.Remove(field);
        }

        if (protectedRequestOptions is not null)
        {
            foreach (var property in protectedRequestOptions)
            {
                settings[property.Key] = property.Value?.DeepClone();
            }
        }

        return settings;
    }

    private static readonly IReadOnlySet<string> UntrustedProviderOptionFields =
        new HashSet<string>(
            ["reasoning", "reasoning_effort", "reasoning_model", "kilo_reasoning_enabled", "api_variant", "max_tokens"],
            StringComparer.Ordinal);
}
