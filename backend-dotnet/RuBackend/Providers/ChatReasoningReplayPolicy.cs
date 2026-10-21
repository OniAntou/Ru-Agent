using System.Text.Json.Nodes;

namespace RuBackend.Providers;

/// <summary>Replay only a documented endpoint format, captured in new durable selections.</summary>
internal static class ChatReasoningReplayPolicy
{
    internal const string PinField = "_ru_chat_reasoning_content_v1";

    internal static bool Supports(ProviderSelection selection) => selection.Protocol == "chat_completions"
        && selection.CompletionUri.Scheme == "https" && selection.CompletionUri.Port == 443
        && selection.CompletionUri.AbsolutePath is "/chat/completions" or "/v1/chat/completions"
        && string.Equals(selection.CompletionUri.Host, "api.deepseek.com", StringComparison.OrdinalIgnoreCase);

    internal static bool TakePinnedCapability(JsonObject options, ProviderSelection selection)
    {
        if (!options.Remove(PinField, out var marker)) return false;
        if (marker is not JsonValue value || !value.TryGetValue<bool>(out var enabled) || !enabled || !Supports(selection))
            throw new ProviderCatalogException("provider_selection_invalid");
        return true;
    }
}
