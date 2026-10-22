using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RuBackend.Business.Settings;

namespace RuBackend.Providers;

/// <summary>Bounded Host-owned catalog cache. No provider/key/protocol fallback.</summary>
public sealed partial class ProviderCatalogService(HttpClient http, ICredentialStore credentials, TimeProvider clock, string? modelsDevCacheFile = null)
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private readonly ModelsDevCatalogSource _modelsDevSource = new(http, clock, modelsDevCacheFile);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _credentialRevisions = new(StringComparer.Ordinal);

    private sealed class Entry
    {
        public string Identity { get; } = Guid.NewGuid().ToString("N");
        public SemaphoreSlim RefreshGate { get; } = new(1, 1);
        public IReadOnlyList<CatalogModel> Models { get; set; } = [];
        public DateTimeOffset? Success { get; set; }
        public DateTimeOffset Attempt { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset RetryAfter { get; set; } = DateTimeOffset.MinValue;
        public string? Error { get; set; }

        /// <summary>When <see cref="Error"/> was recorded, so a cached failure can say so.</summary>
        public DateTimeOffset? ErrorAt { get; set; }
    }

    public async Task<ProviderCatalogSnapshot> GetAsync(SettingsSnapshot settings, bool refresh, CancellationToken ct)
    {
        var selection = await ResolveSelectionAsync(settings, ct).ConfigureAwait(false);
        return await GetAsyncCore(selection, settings, refresh, ct).ConfigureAwait(false);
    }

    public Task<ProviderCatalogSnapshot> GetAsync(
        ProviderSelection selection,
        SettingsSnapshot settings,
        bool refresh,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(settings);
        return GetAsyncCore(selection, settings, refresh, ct);
    }

    public async Task<ProviderCatalogSnapshot> GetAsync(string alias, SettingsSnapshot settings, bool refresh, CancellationToken ct)
    {
        if (ProviderSelection.NormalizeModelsDevBackend(alias) is { } generic)
        {
            var genericSelection = await ResolveGenericAsync(settings, generic, ct).ConfigureAwait(false);
            return await GetAsyncCore(genericSelection, settings, refresh, ct).ConfigureAwait(false);
        }

        return await GetAsyncCore(ProviderSelection.From(settings, alias), settings, refresh, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the effective selection for the stored backend, fetching the
    /// Models.dev list first when a generic backend is configured.
    /// </summary>
    public async Task<ProviderSelection> ResolveSelectionAsync(SettingsSnapshot settings, CancellationToken ct)
    {
        if (ProviderSelection.NormalizeModelsDevBackend(settings.Backend) is { } generic)
        {
            return await ResolveGenericAsync(settings, generic, ct).ConfigureAwait(false);
        }

        return ProviderSelection.From(settings);
    }

    /// <summary>Projects the saved choice without fetching a provider catalog.</summary>
    public string? ReadSavedReasoningEffort(SettingsSnapshot settings)
    {
        if (ProviderSelection.NormalizeModelsDevBackend(settings.Backend) is not { } generic)
            return ProviderReasoningPreferences.Read(settings);
        if (!_modelsDevSource.TryReadCache(out var providers, out _)) return null;
        try
        {
            var selection = ProviderSelection.From(settings with { Backend = generic }, providers);
            return ProviderReasoningPreferences.Read(settings, selection);
        }
        catch (Exception error) when (error is ArgumentException or UnknownProviderAliasException)
        {
            return null;
        }
    }

    private async Task<ProviderSelection> ResolveGenericAsync(SettingsSnapshot settings, string genericBackend, CancellationToken ct)
    {
        var catalog = await GetModelsDevAsync(ct).ConfigureAwait(false);
        return ProviderSelection.From(settings with { Backend = genericBackend }, catalog);
    }

    /// <summary>Returns the public Models.dev metadata, with bounded fetch and offline cache.</summary>
    public Task<IReadOnlyList<ModelsDevProvider>> GetModelsDevAsync(CancellationToken ct) =>
        _modelsDevSource.GetAsync(ct);

    /// <summary>
    /// What the provider's catalog declares about a model's image input, or null when
    /// the catalog publishes no such declaration for it. A null is not a "no": it means
    /// the caller should fall back to whatever weaker signal it has, and only an
    /// explicit false (the provider declared text-only) is a rejection.
    /// </summary>
    public async Task<bool?> DeclaresVisionAsync(
        string backend,
        string model,
        CancellationToken ct)
    {
        const string prefix = "models.dev:";
        if (!backend.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || backend.Length <= prefix.Length
            || string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var providers = await GetModelsDevAsync(ct).ConfigureAwait(false);
        var provider = providers.FirstOrDefault(p =>
            string.Equals(p.Id, backend[prefix.Length..], StringComparison.OrdinalIgnoreCase));
        if (provider?.Vision is null || !provider.Vision.TryGetValue(model, out var declared))
        {
            return null;
        }

        return declared;
    }

    /// <summary>
    /// Fills in reasoning levels a Models.dev provider published but its own
    /// <c>/models</c> response did not carry.
    /// </summary>
    /// <remarks>
    /// Only models the provider left at "unknown" are touched. A level the provider
    /// declared itself wins outright, and a model with no published levels keeps
    /// "unknown" so the picker offers nothing for it.
    ///
    /// Both halves of a model are filled together: <see cref="CatalogModel.Reasoning"/>,
    /// which the picker reads, and <see cref="CatalogModel.WireOptions"/>, which
    /// <see cref="ProviderReasoningPreferences.Resolve"/> requires before it will store
    /// a choice and <see cref="AgentProviderSnapshotFactory"/> requires before it will
    /// send one. Filling only the first leaves the level visible in the dropdown and
    /// rejected the moment it is picked, which is what happened: the save came back
    /// "Invalid settings payload" and the button snapped back to "default".
    ///
    /// A Models.dev catalog that cannot be read leaves the models exactly as
    /// parsed: the levels and the prices are additions, never requirements, and there
    /// is no fallback that would invent them.
    /// </remarks>
    private async Task<IReadOnlyList<CatalogModel>> ApplyModelsDevMetadataAsync(
        ProviderSelection selection,
        IReadOnlyList<CatalogModel> models,
        CancellationToken ct)
    {
        if (!selection.IsModelsDev || selection.Backend.Length <= "models.dev:".Length) return models;

        string providerId = selection.Backend["models.dev:".Length..];
        IReadOnlyList<ModelsDevProvider> providers;
        try
        {
            providers = await GetModelsDevAsync(ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is ProviderCatalogException or HttpRequestException
            or OperationCanceledException or IOException)
        {
            return models;
        }

        var provider = providers
            .FirstOrDefault(entry => string.Equals(entry.Id, providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null) return models;

        var reasoning = provider.Reasoning;
        var costs = provider.Costs;
        var contexts = provider.Contexts;
        var outputLimits = provider.OutputLimits;
        if ((reasoning is null || reasoning.Count == 0)
            && (costs is null || costs.Count == 0)
            && (contexts is null || contexts.Count == 0)
            && (outputLimits is null || outputLimits.Count == 0))
        {
            return models;
        }

        var rewritten = new List<CatalogModel>(models.Count);
        for (int i = 0; i < models.Count; i++)
        {
            var model = models[i];
            if (string.Equals(model.Reasoning.State, CatalogReasoningStates.Unknown, StringComparison.Ordinal)
                && model.Supported
                && reasoning is not null
                && reasoning.TryGetValue(model.Id, out var published)
                && published.Options.Count > 0)
            {
                model = model with
                {
                    Reasoning = published,
                    WireOptions = BuildWireOptions(published.Options),
                };
            }

            // The price is attached whatever the model is supported for. A model the
            // host cannot call is never billed, and a price attached only to the
            // supported ones would make the missing case indistinguishable from a model
            // nobody has heard of.
            if (model.Cost is null && costs is not null && costs.TryGetValue(model.Id, out var cost))
            {
                model = model with { Cost = cost };
            }

            if (model.ContextLength is null
                && contexts is not null
                && contexts.TryGetValue(model.Id, out var contextLength))
            {
                model = model with { ContextLength = contextLength };
            }

            if (model.MaxOutputTokens is null
                && outputLimits is not null
                && outputLimits.TryGetValue(model.Id, out var outputLimit))
            {
                model = model with { MaxOutputTokens = outputLimit };
            }

            rewritten.Add(model);
        }

        return rewritten;
    }

    /// <summary>
    /// The Chat Completions body each published level maps to.
    /// </summary>
    /// <remarks>
    /// Same shape ProviderCatalogParser produces from a provider's own
    /// <c>reasoning_efforts</c>, so Resolve and the bootstrap snapshot treat a
    /// Models.dev level exactly like a provider-declared one. Ordinal keys, matching
    /// the parser, so the level stored in settings is the key that is looked up.
    /// </remarks>
    private static IReadOnlyDictionary<string, JsonObject> BuildWireOptions(IReadOnlyList<string> levels)
    {
        var options = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var level in levels)
        {
            options[level] = new JsonObject { ["reasoning_effort"] = level };
        }

        return options;
    }

    private async Task<ProviderCatalogSnapshot> GetAsyncCore(
        ProviderSelection selection,
        SettingsSnapshot settings,
        bool refresh,
        CancellationToken ct)
    {
        // Reading only this account also invalidates cached metadata on key changes.
        var credential = selection.Account is null ? "" : await credentials.GetAsync("Ru", selection.Account, ct).ConfigureAwait(false) ?? "";
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));
        var scope = selection.Backend + "\n" + selection.BaseUri.AbsoluteUri;
        Entry entry;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_credentialRevisions.TryGetValue(scope, out var previous) && previous != revision)
                _entries.Remove(scope);
            _credentialRevisions[scope] = revision;
            if (!_entries.TryGetValue(scope, out var cachedEntry))
            {
                if (_entries.Count >= 16)
                {
                    var oldest = _entries.MinBy(pair => pair.Value.Attempt).Key;
                    _entries.Remove(oldest);
                    _credentialRevisions.Remove(oldest);
                }
                _entries[scope] = entry = new Entry();
            }
            else
            {
                entry = cachedEntry;
            }
        }
        finally { _gate.Release(); }

        if (selection.RequiresCredential && string.IsNullOrWhiteSpace(credential))
        {
            // A signed-out provider fails closed with an empty catalog instead of
            // fetching anonymously: the picker must not offer a model that cannot run.
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                entry.Error = "catalog_unauthorized";
                entry.ErrorAt = clock.GetUtcNow();
                return BuildSnapshot(selection, settings, entry, revision);
            }
            finally { _gate.Release(); }
        }

        await RefreshIfNeededAsync(entry, scope, selection, credential, refresh, ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return BuildSnapshot(selection, settings, entry, revision);
        }
        finally { _gate.Release(); }
    }

    private async Task RefreshIfNeededAsync(
        Entry entry,
        string scope,
        ProviderSelection selection,
        string credential,
        bool refresh,
        CancellationToken ct)
    {
        await entry.RefreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            DateTimeOffset previousAttempt;
            DateTimeOffset attempt;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!IsCurrentEntry(scope, entry))
                    return;

                var now = clock.GetUtcNow();
                var fresh = entry.Success.HasValue && now - entry.Success.Value < TimeSpan.FromMinutes(5);
                if (now < entry.RetryAfter
                    || now - entry.Attempt < TimeSpan.FromSeconds(30)
                    || (!refresh && fresh && entry.Error is null))
                {
                    return;
                }

                previousAttempt = entry.Attempt;
                attempt = now;
                entry.Attempt = now;
            }
            finally { _gate.Release(); }

            DateTimeOffset? retryAfter = null;
            try
            {
                if (await TryServeOverrideCatalogAsync(entry, scope, selection, attempt, ct).ConfigureAwait(false))
                {
                    return;
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                using var request = ProviderCatalogRequestFactory.Create(selection, credential);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        var retry = response.Headers.RetryAfter?.Delta
                            ?? (response.Headers.RetryAfter?.Date - attempt) ?? TimeSpan.FromSeconds(30);
                        retryAfter = attempt + TimeSpan.FromSeconds(Math.Clamp(retry.TotalSeconds, 30, 3600));
                    }

                    throw new ProviderCatalogException(response.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "catalog_unauthorized",
                        HttpStatusCode.TooManyRequests => "catalog_rate_limited",
                        _ => "catalog_http_error",
                    });
                }

                if (response.Content.Headers.ContentLength > MaximumBytes)
                    throw new ProviderCatalogException("catalog_too_large");

                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var memory = new MemoryStream();
                var buffer = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (memory.Length > MaximumBytes - read)
                        throw new ProviderCatalogException("catalog_too_large");
                    memory.Write(buffer, 0, read);
                }

                var models = ProviderCatalogParser.Parse(selection.Backend, new UTF8Encoding(false, true).GetString(memory.ToArray()));
                models = await ApplyModelsDevMetadataAsync(selection, models, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (IsCurrentEntry(scope, entry) && entry.Attempt == attempt)
                    {
                        entry.Models = models;
                        entry.Success = clock.GetUtcNow();
                        entry.Error = null;
                        entry.ErrorAt = null;
                        entry.RetryAfter = DateTimeOffset.MinValue;
                    }
                }
                finally { _gate.Release(); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The caller token is already canceled; cleanup must still
                // restore the cache's retry state before propagating cancel.
                await RestoreAttemptAsync(scope, entry, attempt, previousAttempt, CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }
            catch (Exception error) when (error is ProviderCatalogException or HttpRequestException
                or OperationCanceledException or DecoderFallbackException or IOException)
            {
                // Cache error bookkeeping must complete even when the caller has
                // just canceled; otherwise a canceled attempt can leave a stale
                // in-flight timestamp that throttles the next request.
                await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (IsCurrentEntry(scope, entry) && entry.Attempt == attempt)
                    {
                        if (retryAfter.HasValue)
                            entry.RetryAfter = retryAfter.Value;
                        entry.Error = error is ProviderCatalogException known ? known.Code
                            : error is OperationCanceledException ? "catalog_timeout" : "catalog_unavailable";
                        entry.ErrorAt = clock.GetUtcNow();
                    }
                }
                finally { _gate.Release(); }
            }
        }
        finally { entry.RefreshGate.Release(); }
    }

    /// <summary>
    /// Serves an override provider's catalog from the Models.dev model list
    /// instead of a <c>GET {base}/models</c> fetch. Returns false for generic
    /// providers so the normal HTTP path runs. Unknown ids throw
    /// <c>catalog_unavailable</c>: the entry is never filled with guesses.
    /// </summary>
    private async Task<bool> TryServeOverrideCatalogAsync(
        Entry entry,
        string scope,
        ProviderSelection selection,
        DateTimeOffset attempt,
        CancellationToken ct)
    {
        const string prefix = "models.dev:";
        if (!selection.IsModelsDev
            || selection.Backend.Length <= prefix.Length
            || !ProviderOverrides.UsesModelsDevCatalog(selection.Backend[prefix.Length..]))
        {
            return false;
        }

        var id = selection.Backend[prefix.Length..];
        var catalog = await GetModelsDevAsync(ct).ConfigureAwait(false);
        var provider = catalog.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new ProviderCatalogException("catalog_unavailable");
        var models = provider.Models
            .Select(modelId => new CatalogModel(modelId, true, "chat_completions", new CatalogReasoning("unknown", [])))
            .ToArray();
        ct.ThrowIfCancellationRequested();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsCurrentEntry(scope, entry) && entry.Attempt == attempt)
            {
                entry.Models = models;
                entry.Success = clock.GetUtcNow();
                entry.Error = null;
                entry.ErrorAt = null;
                entry.RetryAfter = DateTimeOffset.MinValue;
            }
        }
        finally { _gate.Release(); }

        return true;
    }

    private async Task RestoreAttemptAsync(
        string scope,
        Entry entry,
        DateTimeOffset attempt,
        DateTimeOffset previousAttempt,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsCurrentEntry(scope, entry) && entry.Attempt == attempt)
                entry.Attempt = previousAttempt;
        }
        finally { _gate.Release(); }
    }

    private bool IsCurrentEntry(string scope, Entry entry) =>
        _entries.TryGetValue(scope, out var current) && ReferenceEquals(current, entry);

    private ProviderCatalogSnapshot BuildSnapshot(
        ProviderSelection selection,
        SettingsSnapshot settings,
        Entry entry,
        string credentialRevision)
    {
        var now = clock.GetUtcNow();
        var supportedIds = entry.Models.Where(m => m.Supported).Select(m => m.Id).ToArray();
        // The canonical Backend, not the Alias: callers key their own grouped state by the
        // value they asked for, and for a Models.dev provider that is "models.dev:<id>"
        // while the alias is the bare id. Reporting the alias made every such response
        // look like it answered for a different provider, so the client discarded it and
        // the picker stayed empty while the fetch itself succeeded.
        return new(entry.Error is null && entry.Success.HasValue ? "ok" : "error",
            selection.Backend, entry.Identity, selection.Model, ProviderReasoningPreferences.Read(settings, selection),
            supportedIds, entry.Models,
            entry.Success.HasValue && (entry.Error is not null || now - entry.Success.Value >= TimeSpan.FromMinutes(5)),
            entry.Error ?? (entry.Success.HasValue ? null : "catalog_unavailable"), entry.Success)
            { CredentialRevision = credentialRevision, ErrorAtUtc = entry.ErrorAt };
    }
}
