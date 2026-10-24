using System.Text.Json.Nodes;
using RuAgent.Runtime.Adapters.Models;

namespace RuAgent.Runtime.Adapters.Bootstrap;

public enum ProviderToolCallingMode
{
    Native,
    TextSelection,
}

public sealed record ProviderBootstrapData
{
    public ProviderBootstrapData(
        string backend,
        string protocol,
        Uri endpoint,
        string model,
        string credential,
        JsonObject? requestOptions = null,
        int? maximumOutputTokens = null,
        ProviderToolCallingMode? toolCallingMode = null,
        bool allowNativeResponses = false,
        bool allowNativeAnthropic = false,
        bool useMaxCompletionTokens = false,
        long? contextWindowTokens = null,
        int? outputTokenReserve = null,
        JsonObject? reducedEffortRequestOptions = null,
        bool replayChatReasoning = false)
    {
        Backend = Require(backend, 128, nameof(backend));
        Protocol = ProviderProtocols.Require(protocol);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(endpoint.UserInfo)
            || endpoint.ToString().Length > 2048)
        {
            throw new ArgumentException("Provider endpoint is invalid.", nameof(endpoint));
        }

        Model = Require(model, 256, nameof(model));
        Credential = Require(credential, 4096, nameof(credential), allowEmpty: true);
        Endpoint = endpoint;
        RequestOptions = ProviderRequestOptionsValidator.ValidateAndClone(protocol, requestOptions, nameof(requestOptions));
        SelectedReasoningEffort = ReadEffort(RequestOptions);
        ReducedEffortRequestOptions = reducedEffortRequestOptions is null
            ? null
            : ProviderRequestOptionsValidator.ValidateAndClone(protocol, reducedEffortRequestOptions, nameof(reducedEffortRequestOptions));
        MaximumOutputTokens = ValidateOutputCap(protocol, maximumOutputTokens);
        if (useMaxCompletionTokens && protocol != ProviderProtocols.ChatCompletions)
            throw new ArgumentException("The selected output-cap field is invalid for this protocol.", nameof(useMaxCompletionTokens));
        UseMaxCompletionTokens = useMaxCompletionTokens;
        if (replayChatReasoning && Protocol != ProviderProtocols.ChatCompletions)
            throw new ArgumentException("Chat reasoning replay requires Chat Completions.", nameof(replayChatReasoning));
        ReplayChatReasoning = replayChatReasoning;
        if (contextWindowTokens is <= 0 or > 10_000_000)
            throw new ArgumentException("Context window metadata is invalid.", nameof(contextWindowTokens));
        ContextWindowTokens = contextWindowTokens;
        OutputTokenReserve = outputTokenReserve ?? maximumOutputTokens ?? 8192;
        if (OutputTokenReserve is < 1 or > 128_000
            || maximumOutputTokens is not null && maximumOutputTokens != OutputTokenReserve)
            throw new ArgumentException("Output cap must match the pinned reserve.", nameof(outputTokenReserve));
        var protocolMode = ModeForProtocol(Protocol);
        var isSupportedNativeMode = toolCallingMode == ProviderToolCallingMode.Native
            && ((allowNativeResponses && Protocol == ProviderProtocols.OpenAiResponses)
                || (allowNativeAnthropic && Protocol == ProviderProtocols.AnthropicMessages));
        if (toolCallingMode is { } selectedMode && selectedMode != protocolMode && !isSupportedNativeMode)
        {
            throw new ArgumentException("Tool calling mode does not match the provider protocol.", nameof(toolCallingMode));
        }
        ToolCallingMode = toolCallingMode ?? protocolMode;
    }

    public string Backend { get; }

    public string Protocol { get; }

    public Uri Endpoint { get; }

    public string Model { get; }

    public string Credential { get; }

    public JsonObject? RequestOptions { get; private set; }

    /// <summary>The user-selected effort, retained when an individual repair uses reduced effort.</summary>
    public string SelectedReasoningEffort { get; }

    /// <summary>
    /// Request options for the lowest reasoning effort the model offers, when that differs from
    /// <see cref="RequestOptions"/>. Used after a reply was cut off by the output limit.
    /// </summary>
    public JsonObject? ReducedEffortRequestOptions { get; private set; }

    /// <summary>A copy that sends the reduced-effort options; this instance when there are none.</summary>
    public ProviderBootstrapData WithReducedEffort()
    {
        if (ReducedEffortRequestOptions is not { } reduced) return this;
        var copy = (ProviderBootstrapData)MemberwiseClone();
        copy.RequestOptions = (JsonObject)reduced.DeepClone();
        copy.ReducedEffortRequestOptions = null;
        return copy;
    }

    public int? MaximumOutputTokens { get; }

    public bool UseMaxCompletionTokens { get; }
    public bool ReplayChatReasoning { get; }

    public long? ContextWindowTokens { get; }

    public int OutputTokenReserve { get; }

    public ProviderToolCallingMode ToolCallingMode { get; }

    public static ProviderToolCallingMode ModeForProtocol(string protocol) => protocol switch
    {
        ProviderProtocols.ChatCompletions => ProviderToolCallingMode.Native,
        ProviderProtocols.OpenAiResponses or ProviderProtocols.AnthropicMessages => ProviderToolCallingMode.TextSelection,
        _ => throw new ArgumentException("Provider protocol is unsupported.", nameof(protocol)),
    };

    private static int? ValidateOutputCap(string protocol, int? value)
    {
        if (value is null)
        {
            if (protocol == ProviderProtocols.AnthropicMessages)
                throw new ArgumentException("Anthropic requires an output cap.", nameof(value));
            return null;
        }
        if (value is < 1 or > 128000) throw new ArgumentException("Output cap is invalid.", nameof(value));
        if (protocol == ProviderProtocols.AnthropicMessages && value > AnthropicMessagesModelProvider.MaximumSupportedOutputTokens)
            throw new ArgumentException("Anthropic output cap exceeds the adapter limit.", nameof(value));
        return value;
    }

    private static string ReadEffort(JsonObject? options)
    {
        var value = options?["reasoning_effort"]?.GetValue<string>()
            ?? options?["reasoning"]?["effort"]?.GetValue<string>();
        return value is "none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max" ? value : "unknown";
    }

    private static string Require(string? value, int maximum, string parameterName, bool allowEmpty = false)
    {
        if (value is null || value.Length > maximum || value.Contains('\0')
            || value.Any(char.IsControl) || (!allowEmpty && string.IsNullOrWhiteSpace(value)))
        {
            throw new ArgumentException("Provider bootstrap value is invalid.", parameterName);
        }

        return value;
    }
}
