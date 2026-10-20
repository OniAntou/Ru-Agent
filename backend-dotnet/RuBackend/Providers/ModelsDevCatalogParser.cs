using System.Text.Json;

namespace RuBackend.Providers;

/// <summary>Reads only published Models.dev declarations, without network or cache state.</summary>
internal static class ModelsDevCatalogParser
{
    /// <summary>
    /// The same seven values ProviderRequestOptionsValidator accepts, so a level
    /// that reaches the picker can also be sent to the provider.
    /// </summary>
    private static readonly HashSet<string> AllowedEffortLevels =
        new(["none", "minimal", "low", "medium", "high", "xhigh", "max"], StringComparer.Ordinal);
    internal static IReadOnlyList<ModelsDevProvider> Parse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var result = new List<ModelsDevProvider>();
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object
                && property.Value.TryGetProperty("api", out var api)
                && api.ValueKind == JsonValueKind.String
                && api.GetString() is string url
                && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)
                && uri.UserInfo.Length == 0
                && uri.Query.Length == 0
                && uri.Fragment.Length == 0)
            {
                var name = property.Name;
                if (property.Value.TryGetProperty("name", out var rawName)
                    && rawName.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(rawName.GetString()))
                {
                    name = rawName.GetString()!.Trim();
                }

                // Keep the full api URL including its path prefix
                // (…/api/v1, …/zen/v1): catalog and chat URLs are appended
                // to it, so authority-only would 404 most providers.
                // The models map is kept for override providers whose own
                // list endpoint is not OpenAI-shaped (Cloudflare answers 405).
                result.Add(new ModelsDevProvider(
                    property.Name,
                    name,
                    uri.AbsoluteUri.TrimEnd('/'),
                    ReadModelIds(property.Value),
                    ReadModelReasoning(property.Value),
                    ReadModelCosts(property.Value),
                    ReadModelVision(property.Value),
                    ReadModelContexts(property.Value)));
            }
        }

        return result;
    }

    /// <summary>
    /// Model ids from a Models.dev provider entry. Bounded and validated like
    /// fetched catalog ids: overlong or control-char ids are dropped, never
    /// throw, so one bad entry cannot poison the whole list.
    /// </summary>
    private static IReadOnlyList<string> ReadModelIds(JsonElement entry)
    {
        var ids = new List<string>();
        if (entry.ValueKind == JsonValueKind.Object
            && entry.TryGetProperty("models", out var models)
            && models.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var model in models.EnumerateObject())
            {
                var id = model.Name?.Trim() ?? string.Empty;
                if (id.Length is 0 or > 256 || id.Any(char.IsControl)) continue;
                if (seen.Add(id)) ids.Add(id);
                if (ids.Count >= 5000) break;
            }
        }

        return ids;
    }

    /// <summary>
    /// Published prices for a Models.dev provider's own models, keyed by model id.
    /// </summary>
    /// <remarks>
    /// Models.dev quotes US dollars per million tokens, flat. Two published fields are
    /// deliberately ignored: <c>tiers</c> and <c>context_over_200k</c> both raise the
    /// price once a request passes a context threshold, and this service has no way to
    /// know which side of that threshold a run fell on. Reading the base rate and
    /// labelling it as the cost would understate a long request by a factor the caller
    /// cannot see, so the base rate is what ships and the tiers stay unread.
    ///
    /// A model with no <c>cost</c> object is absent rather than zero: models.dev
    /// publishes <c>{input:0, output:0}</c> for a model that is genuinely free, and a
    /// free model must not read as an unknown one. A negative, non-finite or
    /// non-numeric rate is dropped for the same reason a bad id is.
    /// </remarks>
    private static IReadOnlyDictionary<string, CatalogCost> ReadModelCosts(JsonElement entry)
    {
        var costs = new Dictionary<string, CatalogCost>(StringComparer.OrdinalIgnoreCase);

        if (entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Object)
        {
            return costs;
        }

        foreach (var model in models.EnumerateObject())
        {
            var id = model.Name?.Trim() ?? string.Empty;
            if (id.Length is 0 or > 256 || id.Any(char.IsControl)) continue;

            if (ReadCost(model.Value) is { } cost)
            {
                costs[id] = cost;
            }

            if (costs.Count >= 5000) break;
        }

        return costs;
    }

    /// <summary>
    /// Published context windows for a Models.dev provider's own models, keyed by model
    /// id. Only a positive <c>limit.context</c> is kept; a missing or malformed value
    /// stays unpublished rather than guessed.
    /// </summary>
    private static IReadOnlyDictionary<string, int> ReadModelContexts(JsonElement entry)
    {
        var contexts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Object)
        {
            return contexts;
        }

        foreach (var model in models.EnumerateObject())
        {
            var id = model.Name?.Trim() ?? string.Empty;
            if (id.Length is 0 or > 256 || id.Any(char.IsControl)) continue;

            if (model.Value.ValueKind == JsonValueKind.Object
                && model.Value.TryGetProperty("limit", out var limit)
                && limit.ValueKind == JsonValueKind.Object
                && limit.TryGetProperty("context", out var context)
                && context.ValueKind == JsonValueKind.Number
                && context.TryGetInt64(out var value)
                && value is > 0 and <= 100_000_000)
            {
                contexts[id] = (int)value;
            }

            if (contexts.Count >= 5000) break;
        }

        return contexts;
    }

    /// <summary>
    /// Whether a Models.dev model declares image input, keyed by model id.
    /// <para>
    /// Models.dev publishes this two ways: an explicit <c>attachment: true</c> flag, and
    /// <c>modalities.input</c> containing <c>image</c>. Either is a declaration by the
    /// provider, so either is used. Only a model that appears here is treated as vision-
    /// capable; an undeclared model stays unknown rather than being guessed from its name,
    /// which is how a gateway model like <c>dots-studio/dots-3-note-preview:free</c> used
    /// to be rejected as non-vision despite declaring <c>["text","image"]</c>.
    /// </para>
    /// </summary>
    private static IReadOnlyDictionary<string, bool> ReadModelVision(JsonElement entry)
    {
        var vision = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        if (entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Object)
        {
            return vision;
        }

        foreach (var model in models.EnumerateObject())
        {
            var id = model.Name?.Trim() ?? string.Empty;
            if (id.Length is 0 or > 256 || id.Any(char.IsControl)) continue;

            if (DeclaresImageInput(model.Value))
            {
                vision[id] = true;
            }

            if (vision.Count >= 5000) break;
        }

        return vision;
    }

    private static bool DeclaresImageInput(JsonElement model)
    {
        if (model.ValueKind != JsonValueKind.Object) return false;

        if (model.TryGetProperty("attachment", out var attachment)
            && attachment.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        return model.TryGetProperty("modalities", out var modalities)
            && modalities.ValueKind == JsonValueKind.Object
            && modalities.TryGetProperty("input", out var input)
            && input.ValueKind == JsonValueKind.Array
            && input.EnumerateArray().Any(item =>
                item.ValueKind == JsonValueKind.String
                && string.Equals(item.GetString(), "image", StringComparison.OrdinalIgnoreCase));
    }

    private static CatalogCost? ReadCost(JsonElement model)
    {
        if (model.ValueKind != JsonValueKind.Object
            || !model.TryGetProperty("cost", out var node)
            || node.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Both of the main rates are required: a partial quote cannot be turned into a
        // total, and inventing the missing half would be a number nobody can audit.
        if (Rate(node, "input") is not { } input || Rate(node, "output") is not { } output)
        {
            return null;
        }

        return new CatalogCost(input, output, Rate(node, "cache_read"), Rate(node, "cache_write"));
    }

    /// <summary>
    /// One published rate, or null when it is absent or not a usable price. Zero is a
    /// usable price and comes back as zero.
    /// </summary>
    private static double? Rate(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var rate)
        && double.IsFinite(rate)
        && rate >= 0
            ? rate
            : null;

    /// <summary>
    /// Reasoning effort levels a Models.dev provider published for its own models.
    /// </summary>
    /// <remarks>
    /// A provider's own <c>GET {base}/models</c> often carries no reasoning metadata at
    /// all: OpenRouter returns bare ids, so the picker collapsed to a single
    /// "default" entry and nothing was selectable. Models.dev does publish it, as
    /// reasoning_options, and that is the provider's own declaration rather than
    /// anything inferred from a model name.
    ///
    /// Only an explicit effort list is taken. A toggle-only entry, an empty list
    /// (models.dev publishes that for deepseek-r1) and a missing field all leave the
    /// model at "unknown" with no options, so nothing is offered that was never
    /// declared. Values are filtered to the same seven the model step accepts, so the
    /// picker cannot offer an effort the runtime would reject.
    /// </remarks>
    private static IReadOnlyDictionary<string, CatalogReasoning> ReadModelReasoning(JsonElement entry)
    {
        var reasoning = new Dictionary<string, CatalogReasoning>(StringComparer.OrdinalIgnoreCase);

        if (entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Object)
        {
            return reasoning;
        }

        foreach (var model in models.EnumerateObject())
        {
            var id = model.Name?.Trim() ?? string.Empty;
            if (id.Length is 0 or > 256 || id.Any(char.IsControl)) continue;

            var options = ReadEffortLevels(model.Value);
            reasoning[id] = options.Count > 0
                ? new CatalogReasoning(CatalogReasoningStates.Levels, options)
                : new CatalogReasoning(CatalogReasoningStates.Unknown, []);

            if (reasoning.Count >= 5000) break;
        }

        return reasoning;
    }

    private static IReadOnlyList<string> ReadEffortLevels(JsonElement model)
    {
        if (model.ValueKind != JsonValueKind.Object
            || !model.TryGetProperty("reasoning_options", out var declared)
            || declared.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var levels = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var option in declared.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object
                || !option.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || !string.Equals(type.GetString(), "effort", StringComparison.Ordinal))
            {
                continue;
            }

            if (!option.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } effort) continue;
                if (effort.Length is 0 or > 32 || effort.Any(char.IsControl)) continue;
                if (!AllowedEffortLevels.Contains(effort)) continue;
                if (seen.Add(effort)) levels.Add(effort);
                if (levels.Count >= 16) return levels;
            }
        }

        return levels;
    }

}
