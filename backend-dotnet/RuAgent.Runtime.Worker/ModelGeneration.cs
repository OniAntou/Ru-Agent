using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;

namespace RuAgent.Runtime.Worker;

/// <summary>
/// Memory and shape bounds for the typed model generation request. Values are
/// unchanged from the retired worker wire contract.
/// </summary>
public static class ModelGenerationBounds
{
    public const int MaxPromptChars = 16_000;
    public const int MaxModelMessages = 256;
    public const int MaxModelMessagesBytes = 512 * 1024;
    public const int MaxTextChars = 1_000_000;
    public const int MaxToolArgumentsBytes = 64 * 1024;
    public const int MaxPromptContextBytes = 512 * 1024;
    public const int MaxPromptReceiptBlocks = 32;
    public const int MaxFrameBytes = 5 * 1024 * 1024;
}

/// <summary>
/// The typed form of the per-step <c>settings</c> the Host used to send in the
/// start frame: the advertised model tools and the wire <c>max_tokens</c> value.
/// </summary>
public sealed record ModelGenerationSettings
{
    private const int MaxModelTools = 16;

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
        IReadOnlyList<ModelImage>? images = null)
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

        Prompt = prompt;
        ModelMessages = Array.AsReadOnly(modelMessages.ToArray());
        SystemPromptContext = systemPromptContext;
        Settings = settings;
        ProviderBootstrap = providerBootstrap;
        Images = Array.AsReadOnly(copiedImages);
    }

    public string Prompt { get; }

    public IReadOnlyList<ModelTurn> ModelMessages { get; }

    public SystemPromptContext SystemPromptContext { get; }

    public ModelGenerationSettings Settings { get; }

    public ProviderBootstrapData ProviderBootstrap { get; }

    public IReadOnlyList<ModelImage> Images { get; }

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
