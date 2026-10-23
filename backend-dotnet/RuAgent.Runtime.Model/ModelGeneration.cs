using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;

namespace RuAgent.Runtime.Model;

/// <summary>
/// Memory and shape bounds for the typed model generation request. Provider token limits remain enforced by exact request preflight.
/// </summary>
public static class ModelGenerationBounds
{
    public const int MaxPromptChars = ModelInputLimits.MaxPromptChars;
    public const int MaxModelMessages = ModelInputLimits.MaxMessages;
    public const int MaxModelMessagesBytes = ModelInputLimits.MaxHistoryBytes;
    public const int MaxTextChars = 1_000_000;
    public const int MaxToolArgumentsBytes = 64 * 1024;
}

/// <summary>
/// The typed form of the per-step <c>settings</c> the Host used to send in the
/// start frame: the advertised model tools and the wire <c>max_tokens</c> value.
/// </summary>
public sealed record ModelGenerationSettings
{
    private const int MaxModelTools = RuAgent.Protocol.AgentToolDefinitionCatalog.MaximumModelTools;

    public ModelGenerationSettings(JsonArray? modelTools = null, int? maxOutputTokens = null)
    {
        if (modelTools is not null)
        {
            if (modelTools.Count > MaxModelTools)
            {
                throw new ArgumentException("Model tools exceed the generation cap.", nameof(modelTools));
            }

            if (modelTools.Any(tool => tool is not JsonObject))
            {
                throw new ArgumentException("Model tools must be JSON objects.", nameof(modelTools));
            }
        }

        if (maxOutputTokens is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputTokens));
        }

        ModelTools = modelTools is null ? null : (JsonArray)modelTools.DeepClone();
        MaxOutputTokens = maxOutputTokens;
    }

    public JsonArray? ModelTools { get; }

    /// <summary>
    /// The wire <c>max_tokens</c> value, mapped for parity and validated on the
    /// way in. The step service strips it before dispatch: only
    /// <see cref="ProviderBootstrapData.MaximumOutputTokens"/> reaches a provider.
    /// </summary>
    public int? MaxOutputTokens { get; }
}

/// <summary>
/// The typed per-step model generation input. It maps the worker v2
/// <c>agent.step.start</c> payload field for field: prompt, model messages,
/// system prompt context, settings, provider bootstrap and images.
/// </summary>
public sealed record ModelGenerationRequest
{
    public ModelGenerationRequest(
        string prompt,
        IReadOnlyList<ModelTurn> modelMessages,
        SystemPromptContext systemPromptContext,
        ModelGenerationSettings settings,
        ProviderBootstrapData providerBootstrap,
        IReadOnlyList<ModelImage>? images = null,
        IReadOnlyList<JsonObject>? responsesInputItems = null,
        IReadOnlyList<JsonObject>? anthropicMessages = null,
        bool promptInHistory = false,
        string? pendingRuntimeSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(modelMessages);
        ArgumentNullException.ThrowIfNull(systemPromptContext);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(providerBootstrap);

        var copiedImages = images?.ToArray() ?? Array.Empty<ModelImage>();
        if (copiedImages.Length > 4)
        {
            throw new ArgumentException("Too many images. Maximum is 4.", nameof(images));
        }

        if (copiedImages.Any(image => image is null))
        {
            throw new ArgumentException("Images contains null entry.", nameof(images));
        }

        ValidatePrompt(prompt, copiedImages.Length > 0);
        ValidateModelMessages(modelMessages);
        // Projected histories embed tool image data, which has its own bound (ADR 0026).
        var historyImageChars = modelMessages.SelectMany(message => message.Images)
            .Sum(image => (long)image.Base64Data.Length);
        if (responsesInputItems is { Count: > ModelGenerationBounds.MaxModelMessages }
            || responsesInputItems?.Any(item => item is null) == true
            || (responsesInputItems is { Count: > 0 }
                ? ModelRequest.MeasureResponsesInputBytes(responsesInputItems) - historyImageChars
                : ModelRequest.MeasureModelMessagesBytes(modelMessages)) > ModelGenerationBounds.MaxModelMessagesBytes)
            throw new ArgumentException("Responses input history exceeds the generation cap.", nameof(responsesInputItems));
        if (anthropicMessages is { Count: > ModelGenerationBounds.MaxModelMessages }
            || anthropicMessages?.Any(message => message is null) == true
            || ModelRequest.MeasureJsonArrayBytes(anthropicMessages) - historyImageChars > ModelGenerationBounds.MaxModelMessagesBytes)
            throw new ArgumentException("Anthropic input history exceeds the generation cap.", nameof(anthropicMessages));

        Prompt = prompt;
        PromptInHistory = promptInHistory;
        PendingRuntimeSnapshot = pendingRuntimeSnapshot;
        ModelMessages = Array.AsReadOnly(modelMessages.ToArray());
        SystemPromptContext = systemPromptContext;
        Settings = settings;
        ProviderBootstrap = providerBootstrap;
        Images = Array.AsReadOnly(copiedImages);
        ResponsesInputItems = Array.AsReadOnly(responsesInputItems?.Select(item => (JsonObject)item.DeepClone()).ToArray() ?? Array.Empty<JsonObject>());
        AnthropicMessages = Array.AsReadOnly(anthropicMessages?.Select(item => (JsonObject)item.DeepClone()).ToArray() ?? Array.Empty<JsonObject>());
    }

    public string Prompt { get; }

    public IReadOnlyList<ModelTurn> ModelMessages { get; }

    public SystemPromptContext SystemPromptContext { get; }

    public ModelGenerationSettings Settings { get; }

    public ProviderBootstrapData ProviderBootstrap { get; }

    public IReadOnlyList<ModelImage> Images { get; }

    public IReadOnlyList<JsonObject> ResponsesInputItems { get; }

    public IReadOnlyList<JsonObject> AnthropicMessages { get; }

    public bool PromptInHistory { get; }
    public string? PendingRuntimeSnapshot { get; }

    private static void ValidatePrompt(string prompt, bool hasImages)
    {
        if (prompt is null || prompt.Contains('\0') || prompt.Length > ModelGenerationBounds.MaxPromptChars)
        {
            throw new ArgumentException("Prompt is invalid.", nameof(prompt));
        }

        if (!hasImages && string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Prompt is required unless an image is attached.", nameof(prompt));
        }
    }

    private static void ValidateModelMessages(IReadOnlyList<ModelTurn> modelMessages)
    {
        if (modelMessages.Count > ModelGenerationBounds.MaxModelMessages)
        {
            throw new ArgumentException("Model messages exceed the generation cap.", nameof(modelMessages));
        }

        if (modelMessages.Any(message => message is null))
        {
            throw new ArgumentException("Model messages cannot contain null.", nameof(modelMessages));
        }

        if (ModelRequest.MeasureModelMessagesBytes(modelMessages) > ModelGenerationBounds.MaxModelMessagesBytes)
        {
            throw new ArgumentException("Model messages exceed the generation byte cap.", nameof(modelMessages));
        }
    }
}
