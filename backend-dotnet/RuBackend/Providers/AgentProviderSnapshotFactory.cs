using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;
using RuBackend.Business.Settings;

namespace RuBackend.Providers;

/// <summary>Pins the provider choice for a run and hands the snapshot to the in-process step runner.</summary>
public sealed class AgentProviderSnapshotFactory(
    ISettingsStore settings,
    ICredentialStore credentials,
    ProviderCatalogService catalog,
    SqliteAgentRunProviderSelectionStore selectionStore,
    Func<RunId, CancellationToken, Task<bool>> isTerminal) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<RunId, ProviderBootstrapData> _runs = [];
    private bool _disposed;

    public async Task<ProviderBootstrapData> GetAsync(RunId runId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runs.TryGetValue(runId, out var cached)) return cached;
            await RemoveTerminalSnapshotsAsync(ct).ConfigureAwait(false);
            if (_runs.Count >= 64) throw new ProviderCatalogException("provider_snapshot_capacity");

            var state = await selectionStore.GetAsync(runId, ct).ConfigureAwait(false);
            if (state is null) throw new ProviderCatalogException("provider_selection_missing");
            var current = await settings.ReadAsync(ct).ConfigureAwait(false);
            var snapshot = state.Selection is null
                ? await PinCurrentSelectionAsync(runId, current, ct).ConfigureAwait(false)
                : await BuildPinnedSnapshotAsync(state.Selection, current, ct).ConfigureAwait(false);
            _runs.Add(runId, snapshot);
            ct.ThrowIfCancellationRequested();
            return snapshot;
        }
        finally { _gate.Release(); }
    }

    private async Task<ProviderBootstrapData> PinCurrentSelectionAsync(
        RunId runId, SettingsSnapshot current, CancellationToken ct)
    {
        var proposed = await DescribeSelectionAsync(current, ct).ConfigureAwait(false);
        var stored = await selectionStore.PinAsync(runId, proposed, ct).ConfigureAwait(false);
        var pin = stored.Selection ?? throw new ProviderCatalogException("provider_selection_invalid");
        return await BuildPinnedSnapshotAsync(pin, current, ct).ConfigureAwait(false);
    }

    public async Task<AgentRunProviderSelection> DescribeSelectionAsync(
        SettingsSnapshot current, CancellationToken ct, bool requireCatalogModel = false)
    {
        var selected = await catalog.ResolveSelectionAsync(current, ct).ConfigureAwait(false);
        var key = await ReadCredentialAsync(selected, ct).ConfigureAwait(false);
        var models = await catalog.GetAsync(selected, current, false, ct).ConfigureAwait(false);
        ValidateCredentialRevision(key, models);
        var options = ProviderReasoningPreferences.Resolve(
            models,
            selected.Model,
            ProviderReasoningPreferences.Read(current, selected));
        var maximumOutput = ResolveMaximumOutput(selected);
        var modelMetadata = models.Catalog.FirstOrDefault(model =>
            string.Equals(model.Id, selected.Model, StringComparison.OrdinalIgnoreCase));
        var contextWindowTokens = modelMetadata?.ContextLength;
        var contextWindowSource = contextWindowTokens is null
            ? "unknown"
            : selected.Backend.StartsWith("models.dev:", StringComparison.OrdinalIgnoreCase)
                ? "models.dev"
                : "provider_catalog";
        var contextWindowVersion = contextWindowTokens is null
            ? "unknown-v1"
            : models.LastSuccessUtc?.ToString("O") ?? "catalog-v1";
        // New runs always use native tool calling: every supported protocol defines it
        // (chat completions through function calls, Responses through function-call items,
        // Messages through tool-use blocks). The capability registry used to gate the two
        // block protocols to an exact declared model, which left every other model on a
        // weaker one-object selection protocol; a model that cannot call tools now fails
        // at the provider with a bounded, named error instead of being quietly downgraded.
        var toolCallingMode = ProviderToolCallingMode.Native;
        var outputTokenReserve = maximumOutput ?? AgentRunProviderSelection.DefaultOutputTokenReserve;
        var proposed = new AgentRunProviderSelection(
            selected.Backend,
            selected.Alias,
            selected.Protocol,
            selected.BaseUri.AbsoluteUri,
            selected.Model,
            selected.Account,
            options.ToJsonString(),
            maximumOutput,
            selected.Model,
            AgentRunCallingModeCodec.ToStoredValue(toolCallingMode),
            contextWindowTokens,
            contextWindowSource,
            contextWindowVersion,
            outputTokenReserve);
        if (requireCatalogModel && modelMetadata is null)
            throw new ProviderCatalogException("provider_model_unavailable");
        return proposed;
    }

    private async Task<ProviderBootstrapData> BuildPinnedSnapshotAsync(
        AgentRunProviderSelection pin,
        SettingsSnapshot current,
        CancellationToken ct)
    {
        var selected = ReadSelection(pin);
        var key = await ReadCredentialAsync(selected, ct).ConfigureAwait(false);
        var models = await catalog.GetAsync(selected, current, false, ct).ConfigureAwait(false);
        ValidateCredentialRevision(key, models);
        _ = ProviderReasoningPreferences.Resolve(models, selected.Model, "default");
        JsonObject options;
        try
        {
            options = JsonNode.Parse(pin.RequestOptionsJson) as JsonObject
                ?? throw new ProviderCatalogException("provider_selection_invalid");
        }
        catch (System.Text.Json.JsonException)
        {
            throw new ProviderCatalogException("provider_selection_invalid");
        }

        if (!AgentRunCallingModeCodec.TryParseStoredValue(pin.CallingMode, out var toolCallingMode))
        {
            throw new ProviderCatalogException("provider_selection_invalid");
        }
        return CreateSnapshot(selected, key, options, pin.MaximumOutputTokens ?? pin.OutputTokenReserve, toolCallingMode,
            allowNativeResponses: true,
            allowNativeAnthropic: true,
            useMaxCompletionTokens: selected.Alias == "openai"
                && selected.Protocol == ProviderProtocols.ChatCompletions,
            contextWindowTokens: pin.ContextWindowTokens,
            outputTokenReserve: pin.OutputTokenReserve);
    }

    private async Task<string> ReadCredentialAsync(ProviderSelection selected, CancellationToken ct)
    {
        var key = selected.Account is null
            ? ""
            : await credentials.GetAsync("Ru", selected.Account, ct).ConfigureAwait(false) ?? "";
        if (selected.RequiresCredential && string.IsNullOrWhiteSpace(key))
            throw new ProviderCatalogException("catalog_unauthorized");
        if ((selected.Alias is "openai" or "anthropic") && string.IsNullOrWhiteSpace(selected.Model))
            throw new ProviderCatalogException("model_unavailable");
        return key;
    }

    private static ProviderSelection ReadSelection(AgentRunProviderSelection pin)
    {
        var isSupportedProvider = pin.Backend switch
        {
            "Custom OpenAI" => pin.Alias == "custom"
                && pin.Protocol == ProviderProtocols.ChatCompletions,
            "OpenAI" => pin.Alias == "openai"
                && pin.Protocol == ProviderProtocols.OpenAiResponses,
            "Anthropic" => pin.Alias == "anthropic"
                && pin.Protocol == ProviderProtocols.AnthropicMessages,
            _ => ProviderSelection.NormalizeModelsDevBackend(pin.Backend) is { } generic
                && pin.Alias == generic["models.dev:".Length..]
                && pin.Protocol == ProviderProtocols.ChatCompletions,
        };
        if (!Uri.TryCreate(pin.BaseUri, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(baseUri.UserInfo)
            || baseUri.Query.Length != 0
            || baseUri.Fragment.Length != 0
            || string.IsNullOrWhiteSpace(pin.Model)
            || !string.Equals(pin.ModelLabel, pin.Model, StringComparison.Ordinal)
            || !isSupportedProvider)
        {
            throw new ProviderCatalogException("provider_selection_invalid");
        }

        try
        {
            var expectedSelection = ResolveExpectedSelection(pin);
            if (pin.Alias is "openai" or "anthropic"
                && !string.Equals(baseUri.AbsoluteUri, expectedSelection.BaseUri.AbsoluteUri, StringComparison.Ordinal))
            {
                throw new ProviderCatalogException("provider_selection_invalid");
            }

            if (!(pin.Alias == "custom" && pin.Account is null)
                && !string.Equals(pin.Account, expectedSelection.Account, StringComparison.Ordinal))
            {
                throw new ProviderCatalogException("provider_selection_invalid");
            }

            return new ProviderSelection(
                pin.Backend,
                pin.Alias,
                pin.Protocol,
                baseUri,
                pin.Model,
                pin.Account);
        }
        catch (ArgumentException)
        {
            throw new ProviderCatalogException("provider_selection_invalid");
        }
    }

    private static ProviderSelection ResolveExpectedSelection(AgentRunProviderSelection pin)
    {
        var settings = new SettingsSnapshot(
            new JsonObject(),
            pin.Backend,
            pin.Model,
            pin.Model,
            pin.BaseUri,
            pin.Model,
            pin.Model,
            CloudflareAccountId: "pin-validation");
        if (ProviderSelection.NormalizeModelsDevBackend(pin.Backend) is not null)
        {
            var provider = new ModelsDevProvider(pin.Alias, pin.Alias, pin.BaseUri, []);
            return ProviderSelection.From(settings, [provider]);
        }

        return ProviderSelection.From(settings);
    }

    private static void ValidateCredentialRevision(string key, ProviderCatalogSnapshot models)
    {
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        if (revision != models.CredentialRevision)
            throw new ProviderCatalogException("provider_configuration_changed");
    }

    private static int? ResolveMaximumOutput(ProviderSelection selected)
    {
        if (selected.Alias is not ("openai" or "anthropic")) return null;
        if (!ProviderCapabilityRegistry.TryGet(selected.Alias, selected.Model, out var capability))
            throw new ProviderCatalogException("model_unavailable");
        return selected.Protocol == ProviderProtocols.AnthropicMessages
            ? Math.Min(capability.DocumentedMaxOutputTokens!.Value, AnthropicMessagesModelProvider.MaximumSupportedOutputTokens)
            : null;
    }

    private static ProviderBootstrapData CreateSnapshot(
        ProviderSelection selected,
        string key,
        JsonObject options,
        int? maximumOutput,
        ProviderToolCallingMode toolCallingMode,
        bool allowNativeResponses = false,
        bool allowNativeAnthropic = false,
        bool useMaxCompletionTokens = false,
        long? contextWindowTokens = null,
        int? outputTokenReserve = null)
    {
        try
        {
            return new ProviderBootstrapData(
                selected.Backend,
                selected.Protocol,
                selected.CompletionUri,
                selected.Model,
                key,
                options,
                maximumOutput,
                toolCallingMode,
                allowNativeResponses,
                allowNativeAnthropic,
                useMaxCompletionTokens,
                contextWindowTokens,
                outputTokenReserve);
        }
        catch (ArgumentException)
        {
            throw new ProviderCatalogException("provider_selection_invalid");
        }
    }

    private async Task RemoveTerminalSnapshotsAsync(CancellationToken ct)
    {
        foreach (var id in _runs.Keys.ToArray())
        {
            if (await isTerminal(id, ct).ConfigureAwait(false)) _runs.Remove(id);
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try { _disposed = true; _runs.Clear(); }
        finally { _gate.Release(); }
    }
}
