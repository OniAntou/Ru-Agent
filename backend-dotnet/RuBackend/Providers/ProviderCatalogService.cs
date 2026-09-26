using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RuBackend.Business.Settings;

namespace RuBackend.Providers;

/// <summary>One Models.dev provider entry: generic chat_completions base URL.</summary>
public sealed record ModelsDevProvider(string Id, string Name, string BaseUrl, IReadOnlyList<string> Models);

/// <summary>Bounded Host-owned catalog cache. No provider/key/protocol fallback.</summary>
public sealed class ProviderCatalogService(HttpClient http, ICredentialStore credentials, TimeProvider clock, string? modelsDevCacheFile = null)
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private const int MaximumModelsDevBytes = 8 * 1024 * 1024;
    private const string ModelsDevUrl = "https://models.dev/api.json";
    private static readonly TimeSpan ModelsDevCacheTtl = TimeSpan.FromHours(24);

    // The live catalog holds 200+ providers. A smaller payload means a truncated
    // or proxied response: served in-memory for this call only, and only cached
    // when it still carries a credible provider count. A two-entry response can
    // always be produced by a proxy that whitelists exactly what the caller asks
    // for, so it must never overwrite 24h of good data.
    private const int MinimumModelsDevProviders = 50;
    private readonly string _modelsDevCacheFile = modelsDevCacheFile
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ru", "models-dev.json");
    private readonly SemaphoreSlim _modelsDevGate = new(1, 1);
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

    private async Task<ProviderSelection> ResolveGenericAsync(SettingsSnapshot settings, string genericBackend, CancellationToken ct)
    {
        var catalog = await GetModelsDevAsync(ct).ConfigureAwait(false);
        return ProviderSelection.From(settings with { Backend = genericBackend }, catalog);
    }

    /// <summary>
    /// Models.dev provider list (75+ providers). Fresh cache under 24h wins;
    /// offline falls back to the stale cache; no cache fails closed to empty.
    /// </summary>
    public async Task<IReadOnlyList<ModelsDevProvider>> GetModelsDevAsync(CancellationToken ct)
    {
        // Single-flight: concurrent callers share one fetch instead of
        // hammering models.dev once each.
        await _modelsDevGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await GetModelsDevCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _modelsDevGate.Release();
        }
    }

    private async Task<IReadOnlyList<ModelsDevProvider>> GetModelsDevCoreAsync(CancellationToken ct)
    {
        var hasCache = TryReadModelsDevCache(out var cachedProviders, out var cacheFresh);
        if (hasCache && cacheFresh)
        {
            return cachedProviders;
        }

        IReadOnlyList<ModelsDevProvider>? fetched = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await http.GetAsync(ModelsDevUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumModelsDevBytes)
            {
                throw new ProviderCatalogException("catalog_too_large");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (memory.Length > MaximumModelsDevBytes - read)
                {
                    throw new ProviderCatalogException("catalog_too_large");
                }

                memory.Write(buffer, 0, read);
            }

            var body = new UTF8Encoding(false, true).GetString(memory.ToArray());
            ct.ThrowIfCancellationRequested();
            fetched = ParseModelsDevProviders(body);
            if (fetched.Count < MinimumModelsDevProviders)
            {
                throw new ProviderCatalogException("catalog_suspicious");
            }

            // A cached list is trusted on read, so a suspicious fetch must not
            // overwrite it: serve the small payload in-memory and keep the good
            // cache (or fail closed to the fresh payload when there is none).
            WriteModelsDevCacheBestEffort(body);
            return fetched;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is ProviderCatalogException or HttpRequestException
            or OperationCanceledException or DecoderFallbackException or IOException or JsonException)
        {
            // A suspiciously small catalog is served in-memory only: keep the
            // stale cache when one exists instead of letting the small payload
            // overwrite 24h of good data on the next successful-shaped fetch.
            if (error is ProviderCatalogException suspicious
                && string.Equals(suspicious.Code, "catalog_suspicious", StringComparison.Ordinal)
                && hasCache)
            {
                return cachedProviders;
            }

            if (hasCache)
            {
                return cachedProviders;
            }

            return fetched ?? [];
        }
    }

    private static IReadOnlyList<ModelsDevProvider> ParseModelsDevProviders(string body)
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
                result.Add(new ModelsDevProvider(property.Name, name, uri.AbsoluteUri.TrimEnd('/'), ReadModelIds(property.Value)));
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

    private bool TryReadModelsDevCache(out IReadOnlyList<ModelsDevProvider> providers, out bool fresh)
    {
        providers = [];
        fresh = false;
        string text;
        try
        {
            if (!File.Exists(_modelsDevCacheFile))
            {
                return false;
            }

            text = File.ReadAllText(_modelsDevCacheFile);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("fetched_at", out var fetchedAt)
                || fetchedAt.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(fetchedAt.GetString(), out var fetched))
            {
                return false;
            }

            if (!root.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            providers = ParseModelsDevProviders(payload.GetRawText());
            if (providers.Count < MinimumModelsDevProviders)
            {
                DeleteModelsDevCacheBestEffort();
                providers = [];
                return false;
            }

            fresh = clock.GetUtcNow() - fetched < ModelsDevCacheTtl;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void DeleteModelsDevCacheBestEffort()
    {
        try
        {
            File.Delete(_modelsDevCacheFile);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException or NotSupportedException)
        {
        }
    }

    private void WriteModelsDevCacheBestEffort(string body)
    {
        try
        {
            var directory = Path.GetDirectoryName(_modelsDevCacheFile);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_modelsDevCacheFile, $$"""{"fetched_at":"{{clock.GetUtcNow():O}}","payload":{{body}}}""");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException or NotSupportedException)
        {
        }
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
