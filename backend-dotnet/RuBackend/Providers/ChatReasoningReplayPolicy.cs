using System.Text.Json.Nodes;

namespace RuBackend.Providers;

/// <summary>
/// Replay only a documented endpoint format, captured in new durable selections. Two sources
/// document it: the first-party DeepSeek endpoint, and a Models.dev entry that declares
/// <c>interleaved.field = "reasoning_content"</c> for the selected model. Each has its own pin,
/// so a run keeps the basis it was pinned with.
/// </summary>
internal static class ChatReasoningReplayPolicy
{
    internal const string PinField = "_ru_chat_reasoning_content_v1";
    internal const string CatalogPinField = "_ru_chat_reasoning_content_catalog_v1";

    internal static bool Supports(ProviderSelection selection) => selection.Protocol == "chat_completions"
        && selection.CompletionUri.Scheme == "https" && selection.CompletionUri.Port == 443
        && selection.CompletionUri.AbsolutePath is "/chat/completions" or "/v1/chat/completions"
        && string.Equals(selection.CompletionUri.Host, "api.deepseek.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>A catalog declaration applies to HTTPS Chat Completions only.</summary>
    internal static bool SupportsDeclared(ProviderSelection selection) => selection.Protocol == "chat_completions"
        && selection.CompletionUri.Scheme == "https";

    internal static async Task<bool> DeclaredByCatalogAsync(
        ProviderCatalogService catalog, ProviderSelection selection, CancellationToken cancellationToken)
    {
        if (!SupportsDeclared(selection)) return false;
        try
        {
            return await catalog.DeclaresReasoningContentReplayAsync(selection.Backend, selection.Model, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // An unreadable catalog declares nothing: the run keeps the plain wire format.
            return false;
        }
    }

    internal static bool TakePinnedCapability(JsonObject options, ProviderSelection selection)
    {
        var endpoint = TakePin(options, PinField, Supports(selection));
        var declared = TakePin(options, CatalogPinField, SupportsDeclared(selection));
        return endpoint || declared;
    }

    private static bool TakePin(JsonObject options, string field, bool supported)
    {
        if (!options.Remove(field, out var marker)) return false;
        if (marker is not JsonValue value || !value.TryGetValue<bool>(out var enabled) || !enabled || !supported)
            throw new ProviderCatalogException("provider_selection_invalid");
        return true;
    }
}
