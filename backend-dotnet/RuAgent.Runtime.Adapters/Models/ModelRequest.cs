using System.Text.Json.Nodes;

namespace RuAgent.Runtime.Adapters.Models;

public sealed record ModelRequest
{
    public ModelRequest(
        string model,
        string systemPrompt,
        string prompt,
        IReadOnlyList<ModelTurn> turns,
        JsonObject settings,
        Uri endpoint,
        string credential,
        int? maximumOutputTokens = null,
        IReadOnlyList<ModelImage>? images = null)
    {
        if (string.IsNullOrWhiteSpace(model) || model.Length > 256 || model.Contains('\0'))
        {
            throw new ArgumentException("Model is invalid.", nameof(model));
        }

        if (systemPrompt is null || systemPrompt.Length > 1_000_000 || systemPrompt.Contains('\0'))
        {
            throw new ArgumentException("System prompt exceeds the adapter cap.", nameof(systemPrompt));
        }

        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 16_000 || prompt.Contains('\0'))
        {
            throw new ArgumentException("Prompt is invalid.", nameof(prompt));
        }

        ArgumentNullException.ThrowIfNull(turns);
        if (turns.Count > 32 || turns.Any(turn => turn is null))
        {
            throw new ArgumentException("Model turns exceed the adapter cap.", nameof(turns));
        }

        ArgumentNullException.ThrowIfNull(settings);
        if (settings.ToJsonString().Length > 128 * 1024)
        {
            throw new ArgumentException("Model settings exceed the adapter cap.", nameof(settings));
        }

        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new ArgumentException("Provider endpoint must be HTTP(S) without user info.", nameof(endpoint));
        }
        if (maximumOutputTokens is < 1 or > 128000) throw new ArgumentException("Maximum output tokens is invalid.", nameof(maximumOutputTokens));

        if (images is not null)
        {
            if (images.Count > 4)
            {
                throw new ArgumentException("Too many images. Maximum is 4.", nameof(images));
            }

            if (images.Any(img => img is null))
            {
                throw new ArgumentException("Images contains null entry.", nameof(images));
            }

            long totalDecoded = 0;
            foreach (var img in images)
            {
                // Estimate decoded bytes: ceil(Base64Data.Length * 3 / 4)
                // Account for padding via formula (len * 3 + 3) / 4 to get upper bound
                var len = (long)img.Base64Data.Length;
                var decoded = (len * 3 + 3) / 4;
                totalDecoded += decoded;
                if (totalDecoded > 20L * 1024 * 1024)
                {
                    throw new ArgumentException("Total decoded images exceed 20MB.", nameof(images));
                }
            }
        }

        Model = model;
        SystemPrompt = systemPrompt;
        Prompt = prompt;
        Turns = turns.ToArray();
        Settings = (JsonObject)settings.DeepClone();
        Endpoint = endpoint;
        Credential = credential ?? throw new ArgumentNullException(nameof(credential));
        MaximumOutputTokens = maximumOutputTokens;
        Images = images is null ? Array.Empty<ModelImage>() : images.ToArray();
    }

    public string Model { get; }

    public string SystemPrompt { get; }

    public string Prompt { get; }

    public IReadOnlyList<ModelTurn> Turns { get; }

    public JsonObject Settings { get; }

    public Uri Endpoint { get; }

    public string Credential { get; }

    public int? MaximumOutputTokens { get; }

    public IReadOnlyList<ModelImage> Images { get; }
}
