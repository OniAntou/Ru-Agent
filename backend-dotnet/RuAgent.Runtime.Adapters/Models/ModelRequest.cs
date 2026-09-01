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
        int? maximumOutputTokens = null)
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

        Model = model;
        SystemPrompt = systemPrompt;
        Prompt = prompt;
        Turns = turns.ToArray();
        Settings = (JsonObject)settings.DeepClone();
        Endpoint = endpoint;
        Credential = credential ?? throw new ArgumentNullException(nameof(credential));
        MaximumOutputTokens = maximumOutputTokens;
    }

    public string Model { get; }

    public string SystemPrompt { get; }

    public string Prompt { get; }

    public IReadOnlyList<ModelTurn> Turns { get; }

    public JsonObject Settings { get; }

    public Uri Endpoint { get; }

    public string Credential { get; }

    public int? MaximumOutputTokens { get; }
}
