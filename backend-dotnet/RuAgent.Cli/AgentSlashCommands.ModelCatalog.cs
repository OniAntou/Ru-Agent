using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Client;
using RuAgent.Cli.Tui;

namespace RuAgent.Cli;

/// <summary>
/// Grouped model selection across every provider the operator has connected. The active
/// backend comes first, then fixed providers with a stored key and every signed-in models.dev
/// provider. One pick carries both the provider and the model, so a model of another provider
/// no longer needs a separate <c>/provider</c> switch first.
/// </summary>
/// <remarks>
/// The fan-out is bounded: at most <see cref="MaxGroupedModelProviders"/> catalogs are fetched,
/// <see cref="MaxGroupedCatalogFetches"/> at a time. A catalog that fails keeps its provider
/// heading with an explicit reason instead of disappearing, and the single-provider path keeps
/// the historic <c>/model</c> behavior unchanged.
/// </remarks>
public sealed partial class AgentSlashCommands
{
    /// <summary>Upper bound on providers in one grouped picker: one catalog fetch each.</summary>
    private const int MaxGroupedModelProviders = 8;

    /// <summary>Provider catalogs fetched concurrently; the Host bounds its own provider calls.</summary>
    private const int MaxGroupedCatalogFetches = 4;

    private const int MaximumModelsDevIdChars = 64;

    private sealed record ModelGroupRequest(string Backend, string WireChoice, string Display, bool IsCurrent);

    private sealed record ModelCatalogReading(
        IReadOnlyList<Tui.TuiSurfaces.ModelGroup> Groups,
        IReadOnlyDictionary<string, bool> KeyStates);

    /// <summary>
    /// Reads the active catalog plus every other provider the operator signed in. Discovery
    /// calls are best-effort: an unavailable models.dev list leaves the active provider only,
    /// which is exactly the old single-provider behavior.
    /// </summary>
    private async Task<ModelCatalogReading> ReadModelCatalogAsync(
        JsonElement settings,
        CancellationToken cancellationToken)
    {
        var keyStates = new Dictionary<string, bool>(ReadKeyStates(settings), StringComparer.OrdinalIgnoreCase);
        var signedIn = await ReadSignedInModelsDevIdsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var id in signedIn)
        {
            keyStates[ProviderCredential.ModelsDevPrefix + id] = true;
        }

        var currentBackend = ReadBackend(settings)?.Trim();
        if (string.IsNullOrWhiteSpace(currentBackend))
        {
            // No active backend yet: keep the historic single-catalog fallback (the Host
            // resolves its configured default), because a grouped pick would name a provider
            // the run did not choose. Discovery still fed the key states above.
            return new ModelCatalogReading(
                Array.Empty<Tui.TuiSurfaces.ModelGroup>(),
                keyStates);
        }

        var names = await ReadModelsDevNamesAsync(cancellationToken).ConfigureAwait(false);
        var requests = new List<ModelGroupRequest>(MaxGroupedModelProviders)
        {
            new(currentBackend, WireChoiceForBackend(currentBackend), DisplayForBackend(currentBackend, names), IsCurrent: true),
        };

        // Fixed providers count only when their credential is stored; the custom endpoint is
        // reachable without one, so a configured URL is enough there.
        AddFixedProviderWhenSignedIn(requests, settings, "OpenAI", "openai", "OpenAI");
        AddFixedProviderWhenSignedIn(requests, settings, "Anthropic", "anthropic", "Anthropic");
        if (HasStoredKey(settings, "has_custom_api_key") || HasCustomEndpoint(settings)
            || string.Equals(currentBackend, "Custom OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            AddGroupRequest(requests, new ModelGroupRequest("Custom OpenAI", "custom", "Custom", IsCurrent: false));
        }

        foreach (var id in signedIn)
        {
            var wire = ProviderCredential.ModelsDevPrefix + id;
            AddGroupRequest(requests, new ModelGroupRequest(
                wire,
                wire,
                names.TryGetValue(id, out var name) ? name : wire,
                IsCurrent: false));
        }

        var groups = await FetchModelGroupsAsync(requests, cancellationToken).ConfigureAwait(false);
        return new ModelCatalogReading(groups, keyStates);
    }

    /// <summary>models.dev ids with a stored key, filtered to the slug the Host accepts.</summary>
    private async Task<IReadOnlyList<string>> ReadSignedInModelsDevIdsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> ids;
        try
        {
            ids = await _settingsClient.GetModelsDevKeysAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Array.Empty<string>();
        }

        var accepted = new List<string>(ids.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in ids)
        {
            var id = raw?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(id)
                || id.Length > MaximumModelsDevIdChars
                || id.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_'))
                || !seen.Add(id))
            {
                continue;
            }

            accepted.Add(id);
        }

        return accepted;
    }

    /// <summary>The picker across providers, or the same catalog as a panel when none is wired.</summary>
    private async Task ShowGroupedModelChoicesAsync(
        JsonElement settings,
        IReadOnlyList<Tui.TuiSurfaces.ModelGroup> groups,
        IReadOnlyDictionary<string, bool> keyStates,
        CancellationToken cancellationToken)
    {
        var backend = ReadBackend(settings);
        var currentModel = ReadModel(settings, backend);
        var (items, targets) = Tui.TuiSurfaces.GroupedModelChoices(currentModel, groups, keyStates);
        var enabled = items.Count(item => item.Enabled);
        if (Picker is not null && enabled > 1)
        {
            var picked = await Picker(
                    // Minimal surface: the provider headings carry the context, so the card
                    // needs neither a footer caption nor a row ratio.
                    new Tui.ChoiceList("model", items, showCounter: false, hideFooter: true),
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (picked is null)
            {
                return;
            }

            if (TryResolveGroupedChoice(picked, targets) is { } target)
            {
                if (await SaveProviderModelSelectionAsync(target.WireChoice, target.Model, cancellationToken).ConfigureAwait(false))
                {
                    await WriteModelSelectedAsync(target.Model, DisplayForTarget(groups, target)).ConfigureAwait(false);
                }

                return;
            }

            // A hand-written picker value keeps the historic meaning: a model on the active
            // backend. This only happens for pickers that answer with raw ids.
            await SetModelAsync(settings, backend, picked, cancellationToken).ConfigureAwait(false);
            return;
        }

        WriteGroupedModelPanel(settings, groups, keyStates);
        await _humanOut.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// After <c>/provider</c> switched the backend: offer the new provider's catalog so the
    /// previously saved model (which usually belongs to the old provider) is never left
    /// pinned. One model is chosen without a picker; a missing catalog is a named notice,
    /// not a failed provider switch.
    /// </summary>
    private async Task PromptModelForBackendAsync(
        string wireChoice,
        string displayName,
        CancellationToken cancellationToken)
    {
        if (Json)
        {
            return;
        }

        JsonElement settings;
        try
        {
            settings = await _settingsClient.GetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await WriteModelChoiceLaterAsync(displayName).ConfigureAwait(false);
            return;
        }

        IReadOnlyDictionary<string, bool> keyStates;
        try
        {
            keyStates = await ReadAllKeyStatesAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            keyStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }

        IReadOnlyList<(string Id, bool Supported)> catalog;
        try
        {
            catalog = await _settingsClient.GetModelsAsync(wireChoice, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await WriteModelChoiceLaterAsync(displayName).ConfigureAwait(false);
            return;
        }

        if (catalog.Count == 0)
        {
            await WriteModelChoiceLaterAsync(displayName).ConfigureAwait(false);
            return;
        }

        var backend = ReadBackend(settings);
        var savedModel = backend is null ? null : ReadModel(settings, backend);
        if (!string.IsNullOrWhiteSpace(savedModel)
            && catalog.Any(model => model.Supported
                && string.Equals(model.Id.Trim(), savedModel.Trim(), StringComparison.Ordinal)))
        {
            // The model already stored for this provider is in its catalog; switching to it
            // was the whole intent, so do not interrupt with a picker that has nothing to fix.
            return;
        }

        var choices = Tui.TuiSurfaces.ModelCatalogChoices(wireChoice, null, catalog, keyStates);
        if (choices.Count == 0)
        {
            await WriteModelChoiceLaterAsync(displayName).ConfigureAwait(false);
            return;
        }

        if (Picker is null)
        {
            WriteModelPanel(settings, wireChoice, choices);
            await _humanOut.FlushAsync().ConfigureAwait(false);
            return;
        }

        var enabled = choices.Count(item => item.Enabled);
        if (enabled <= 1)
        {
            var only = choices.FirstOrDefault(item => item.Enabled);
            if (only is null)
            {
                WriteModelPanel(settings, wireChoice, choices);
                await _humanOut.FlushAsync().ConfigureAwait(false);
                return;
            }

            if (await SaveProviderModelSelectionAsync(wireChoice, only.Value, cancellationToken).ConfigureAwait(false))
            {
                await WriteModelSelectedAsync(only.Value, displayName).ConfigureAwait(false);
            }

            return;
        }

        var picked = await Picker(
                new Tui.ChoiceList("model", choices, footer: displayName, showRowCount: true),
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (picked is null)
        {
            await WriteModelChoiceLaterAsync(displayName).ConfigureAwait(false);
            return;
        }

        if (await SaveProviderModelSelectionAsync(wireChoice, picked, cancellationToken).ConfigureAwait(false))
        {
            await WriteModelSelectedAsync(picked, displayName).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadModelsDevNamesAsync(CancellationToken cancellationToken)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var providers = await _settingsClient.GetModelsDevAsync(cancellationToken).ConfigureAwait(false);
            foreach (var (id, name) in providers)
            {
                var key = id?.Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrWhiteSpace(name))
                {
                    names[key] = name.Trim();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // The names are presentation only; ids still label their groups.
        }

        return names;
    }

    private async Task<IReadOnlyList<Tui.TuiSurfaces.ModelGroup>> FetchModelGroupsAsync(
        IReadOnlyList<ModelGroupRequest> requests,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            return Array.Empty<Tui.TuiSurfaces.ModelGroup>();
        }

        using var gate = new SemaphoreSlim(Math.Min(MaxGroupedCatalogFetches, requests.Count));
        var tasks = requests
            .Select(request => FetchModelGroupAsync(request, gate, cancellationToken))
            .ToArray();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<Tui.TuiSurfaces.ModelGroup> FetchModelGroupAsync(
        ModelGroupRequest request,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var models = await _settingsClient.GetModelsAsync(request.WireChoice, cancellationToken).ConfigureAwait(false);
            return new Tui.TuiSurfaces.ModelGroup(
                request.Backend,
                request.WireChoice,
                request.Display,
                models,
                Error: models.Count == 0 ? "model list empty" : null,
                IsCurrent: request.IsCurrent);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return new Tui.TuiSurfaces.ModelGroup(
                request.Backend,
                request.WireChoice,
                request.Display,
                Array.Empty<(string, bool)>(),
                Error: CliErrorText.ShortMessage(error.Message),
                IsCurrent: request.IsCurrent);
        }
        finally
        {
            gate.Release();
        }
    }

    private void WriteGroupedModelPanel(
        JsonElement settings,
        IReadOnlyList<Tui.TuiSurfaces.ModelGroup> groups,
        IReadOnlyDictionary<string, bool> keyStates)
    {
        var currentModel = ReadModel(settings, ReadBackend(settings));
        var sections = new List<Tui.PanelSection>(groups.Count);
        foreach (var group in groups)
        {
            var title = group.IsCurrent ? group.Display + " · current" : group.Display;
            if (group.Error is not null && group.Models.Count == 0)
            {
                sections.Add(new Tui.PanelSection(title,
                    [new Tui.PanelRow("catalog", "unavailable (" + group.Error + ")")]));
                continue;
            }

            var rows = Tui.TuiSurfaces.OrderedGroupModels(currentModel, group)
                .Select(model =>
                {
                    var isCurrent = group.IsCurrent
                        && !string.IsNullOrWhiteSpace(currentModel)
                        && string.Equals(model.Id, currentModel.Trim(), StringComparison.Ordinal);
                    var detail = Tui.TuiSurfaces.GroupModelDetail(
                        model.Id, model.Supported, isCurrent, group.WireChoice, keyStates);
                    return new Tui.PanelRow(model.Id, detail);
                })
                .ToList();
            sections.Add(new Tui.PanelSection(title, rows));
        }

        if (sections.Count == 0)
        {
            sections.Add(new Tui.PanelSection(null, [new Tui.PanelRow("models", "none")]));
        }

        foreach (var line in Tui.TuiPanel.Compose(PanelWidth, "model", sections, "/model <name>", framed: !FramelessReports))
        {
            _humanOut.WriteLine(line);
        }
    }

    private void AddFixedProviderWhenSignedIn(
        List<ModelGroupRequest> requests,
        JsonElement settings,
        string backend,
        string wireChoice,
        string display)
    {
        if (HasStoredKey(settings, "has_" + wireChoice + "_api_key"))
        {
            AddGroupRequest(requests, new ModelGroupRequest(backend, wireChoice, display, IsCurrent: false));
        }
    }

    private static void AddGroupRequest(List<ModelGroupRequest> requests, ModelGroupRequest request)
    {
        if (requests.Count >= MaxGroupedModelProviders)
        {
            return;
        }

        if (requests.Any(existing => string.Equals(existing.WireChoice, request.WireChoice, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        requests.Add(request);
    }

    private static bool HasStoredKey(JsonElement settings, string field) =>
        settings.ValueKind == JsonValueKind.Object
        && settings.TryGetProperty(field, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static bool HasCustomEndpoint(JsonElement settings) =>
        settings.ValueKind == JsonValueKind.Object
        && settings.TryGetProperty("custom_url", out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString());

    private static string WireChoiceForBackend(string backend) =>
        backend.Trim() switch
        {
            var value when string.Equals(value, "OpenAI", StringComparison.OrdinalIgnoreCase) => "openai",
            var value when string.Equals(value, "Anthropic", StringComparison.OrdinalIgnoreCase) => "anthropic",
            var value when string.Equals(value, "Custom OpenAI", StringComparison.OrdinalIgnoreCase) => "custom",
            var value when value.StartsWith(ProviderCredential.ModelsDevPrefix, StringComparison.OrdinalIgnoreCase)
                => ProviderCredential.ModelsDevPrefix
                    + value[ProviderCredential.ModelsDevPrefix.Length..].Trim().ToLowerInvariant(),
            var value => value,
        };

    private static string DisplayForBackend(string backend, IReadOnlyDictionary<string, string> names) =>
        backend.Trim() switch
        {
            var value when string.Equals(value, "OpenAI", StringComparison.OrdinalIgnoreCase) => "OpenAI",
            var value when string.Equals(value, "Anthropic", StringComparison.OrdinalIgnoreCase) => "Anthropic",
            var value when string.Equals(value, "Custom OpenAI", StringComparison.OrdinalIgnoreCase) => "Custom",
            var value when value.StartsWith(ProviderCredential.ModelsDevPrefix, StringComparison.OrdinalIgnoreCase)
                => names.TryGetValue(value[ProviderCredential.ModelsDevPrefix.Length..].Trim(), out var name)
                    ? name
                    : value,
            var value => value,
        };

    private static string DisplayForTarget(
        IReadOnlyList<Tui.TuiSurfaces.ModelGroup> groups,
        Tui.TuiSurfaces.ModelChoiceTarget target)
    {
        foreach (var group in groups)
        {
            if (string.Equals(group.WireChoice, target.WireChoice, StringComparison.OrdinalIgnoreCase))
            {
                return group.Display;
            }
        }

        return target.Backend;
    }

    private static Tui.TuiSurfaces.ModelChoiceTarget? TryResolveGroupedChoice(
        string value,
        IReadOnlyList<Tui.TuiSurfaces.ModelChoiceTarget> targets)
    {
        if (value.Length < 2
            || value[0] != '#'
            || !int.TryParse(
                value.AsSpan(1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var index)
            || index < 0
            || index >= targets.Count)
        {
            return null;
        }

        return targets[index];
    }

    /// <summary>
    /// Saves a provider and its model in one transition. The Host expands
    /// <c>selected_model</c> into the provider's own model field, so the shared models.dev
    /// field and the fixed-provider fields cannot disagree.
    /// </summary>
    private async Task<bool> SaveProviderModelSelectionAsync(
        string wireChoice,
        string model,
        CancellationToken cancellationToken)
    {
        try
        {
            await _settingsClient.UpdateAsync(
                new JsonObject
                {
                    ["backend_choice"] = wireChoice,
                    ["selected_model"] = model,
                },
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AgentApiException error)
        {
            await _err.WriteLineAsync(CliErrorText.Format(error)).ConfigureAwait(false);
            await _err.FlushAsync().ConfigureAwait(false);
            return false;
        }
        catch (Exception)
        {
            await _err.WriteLineAsync(
                "model_save_failed: could not confirm the model was saved; check the logs before trying again.").ConfigureAwait(false);
            await _err.FlushAsync().ConfigureAwait(false);
            return false;
        }
    }

    private async Task WriteModelSelectedAsync(string model, string providerDisplay)
    {
        await _humanOut.WriteLineAsync(Tui.TuiPalette.Ok(
            $"✓ model: {model} ({ChatDisplayText.Sanitize(providerDisplay, 64)})")).ConfigureAwait(false);
        await _humanOut.FlushAsync().ConfigureAwait(false);
    }

    private async Task WriteModelChoiceLaterAsync(string displayName)
    {
        await _humanOut.WriteLineAsync(Tui.TuiPalette.DimPaint(
            $"  model not chosen for {ChatDisplayText.Sanitize(displayName, 64)} · run /model to choose one")).ConfigureAwait(false);
        await _humanOut.FlushAsync().ConfigureAwait(false);
    }
}

