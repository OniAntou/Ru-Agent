using System.Text.Json.Nodes;
using RuAgent.Runtime.Core.Contracts;

namespace RuAgent.Runtime.Adapters.Devices;

public sealed record VtubeStudioAction(
    string Code,
    string Kind,
    string Label,
    string Identifier);

public sealed record VtubeStudioCatalog(
    VtsCatalog Catalog,
    IReadOnlyDictionary<string, VtubeStudioAction> Actions)
{
    public static bool IsLoaded(JsonObject model)
    {
        var data = model["data"] as JsonObject;
        return data?["modelLoaded"] is JsonValue loaded
            && loaded.TryGetValue<bool>(out var isLoaded)
            && isLoaded;
    }

    public static VtubeStudioCatalog Parse(JsonObject model, JsonObject hotkeys, JsonObject expressions)
    {
        var modelData = RequireObject(model["data"]);
        if (!IsLoaded(model))
        {
            throw new VtubeStudioProviderException("vts_catalog_unavailable", retryable: true);
        }

        var modelId = RequiredText(modelData, "modelID", 256);
        var modelName = RequiredText(modelData, "modelName", 256);
        var hotkeyItems = ReadItems(RequireObject(hotkeys["data"])["availableHotkeys"], "hotkeyID", 512);
        var expressionItems = ReadItems(RequireObject(expressions["data"])["expressions"], "expressionFile", 512);

        var actions = new Dictionary<string, VtubeStudioAction>(StringComparer.Ordinal);
        var hotkeyCodes = new List<string>();
        foreach (var item in hotkeyItems
            .Where(item => item.Identifier.Length > 0)
            .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Identifier, StringComparer.Ordinal))
        {
            var code = $"vts_{hotkeyCodes.Count + 1:00}";
            hotkeyCodes.Add(code);
            actions.Add(code, new VtubeStudioAction(code, "hotkey", item.Label, item.Identifier));
        }

        var expressionCodes = new List<string>();
        foreach (var item in expressionItems
            .Where(item => item.Identifier.EndsWith(".exp3.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Identifier, StringComparer.Ordinal))
        {
            var code = $"expr_{expressionCodes.Count + 1:00}";
            expressionCodes.Add(code);
            actions.Add(code, new VtubeStudioAction(code, "expression", item.Label, item.Identifier));
        }

        return new VtubeStudioCatalog(
            new VtsCatalog(modelId, modelName, hotkeyCodes, expressionCodes),
            actions);
    }

    private static IReadOnlyList<CatalogItem> ReadItems(JsonNode? node, string identifierKey, int maxItems)
    {
        if (node is not JsonArray array || array.Count > maxItems)
        {
            throw new VtubeStudioProviderException("malformed_response", retryable: false);
        }

        var items = new List<CatalogItem>();
        foreach (var child in array)
        {
            if (child is not JsonObject item)
            {
                continue;
            }

            string? identifier;
            try
            {
                identifier = OptionalText(item, identifierKey, 256);
            }
            catch (VtubeStudioProviderException)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(identifier))
            {
                continue;
            }

            string? label;
            try
            {
                label = OptionalText(item, "name", 256);
            }
            catch (VtubeStudioProviderException)
            {
                label = null;
            }

            label ??= identifier;
            items.Add(new CatalogItem(label, identifier));
        }

        return items;
    }

    private static JsonObject RequireObject(JsonNode? node) => node as JsonObject
        ?? throw new VtubeStudioProviderException("malformed_response", retryable: false);

    private static string RequiredText(JsonObject obj, string key, int max)
    {
        var value = OptionalText(obj, key, max);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new VtubeStudioProviderException("malformed_response", retryable: false);
    }

    private static string? OptionalText(JsonObject obj, string key, int max)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        if (node is not JsonValue value
            || !value.TryGetValue<string>(out var text)
            || text.Length > max
            || text.Any(char.IsControl))
        {
            throw new VtubeStudioProviderException("malformed_response", retryable: false);
        }

        return text.Trim();
    }

    private sealed record CatalogItem(string Label, string Identifier);
}
