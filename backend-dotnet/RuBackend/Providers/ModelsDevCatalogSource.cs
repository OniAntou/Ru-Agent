using System.Text;
using System.Text.Json;

namespace RuBackend.Providers;

/// <summary>Owns the public metadata fetch and its durable/parsed caches; never reads credentials.</summary>
internal sealed class ModelsDevCatalogSource(HttpClient http, TimeProvider clock, string? modelsDevCacheFile)
{
    private const int MaximumModelsDevBytes = 8 * 1024 * 1024;
    private const string ModelsDevUrl = "https://models.dev/api.json";
    private static readonly TimeSpan ModelsDevCacheTtl = TimeSpan.FromHours(24);

    // The live catalog holds 200+ providers. A smaller payload means a truncated
    // or proxied response: served in-memory for this call only, and only cached
    // when it still carries a credible provider count. A two-entry response can
    // always be produced by a proxy that whitelists exactly what the caller asks
    // for, so it must never overwrite 24h of good data.
    private const int MinimumModelsDevProviders = 50;

    internal string CacheFile { get; } = modelsDevCacheFile
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ru", "models-dev.json");
    private readonly SemaphoreSlim _modelsDevGate = new(1, 1);
    // Parsed Models.dev list, keyed by the cache file's size + mtime. The file
    // is megabytes; without this every /models for a models.dev backend reads
    // and re-parses it twice (once to resolve the selection, once to fill
    // reasoning levels), so a poll fan-out parses gigabytes a day for data
    // that changes at most daily.
    private readonly object _modelsDevParseLock = new();
    private string? _modelsDevParsedKey;
    private IReadOnlyList<ModelsDevProvider>? _modelsDevParsed;
    private DateTimeOffset _modelsDevParsedAt;

    /// <summary>
    /// Models.dev provider list (75+ providers). Fresh cache under 24h wins;
    /// offline falls back to the stale cache; no cache fails closed to empty.
    /// </summary>
    internal async Task<IReadOnlyList<ModelsDevProvider>> GetAsync(CancellationToken ct)
    {
        // Fast path: a fresh cache file is served from the parsed in-memory
        // copy with no gate and no file read. Only the fresh case shortcuts;
        // a stale or missing file still goes through the gate so refresh,
        // stale-fallback and fail-closed semantics stay exactly as before.
        if (TryReadCache(out var cached, out var fresh) && fresh)
        {
            return cached;
        }

        // Single-flight: concurrent callers share one fetch instead of
        // hammering models.dev once each.
        await _modelsDevGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (TryReadCache(out cached, out fresh) && fresh)
            {
                return cached;
            }

            return await GetModelsDevCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _modelsDevGate.Release();
        }
    }

    private async Task<IReadOnlyList<ModelsDevProvider>> GetModelsDevCoreAsync(CancellationToken ct)
    {
        var hasCache = TryReadCache(out var cachedProviders, out var cacheFresh);
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
            fetched = ModelsDevCatalogParser.Parse(body);
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

    internal bool TryReadCache(out IReadOnlyList<ModelsDevProvider> providers, out bool fresh)
    {
        providers = [];
        fresh = false;
        string key;
        try
        {
            var info = new FileInfo(CacheFile);
            if (!info.Exists)
            {
                return false;
            }

            key = info.Length + ":" + info.LastWriteTimeUtc.Ticks;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }

        lock (_modelsDevParseLock)
        {
            if (_modelsDevParsed is not null && string.Equals(_modelsDevParsedKey, key, StringComparison.Ordinal))
            {
                providers = _modelsDevParsed;
                fresh = clock.GetUtcNow() - _modelsDevParsedAt < ModelsDevCacheTtl;
                return true;
            }
        }

        string text;
        try
        {
            text = File.ReadAllText(CacheFile);
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

            providers = ModelsDevCatalogParser.Parse(payload.GetRawText());
            if (providers.Count < MinimumModelsDevProviders)
            {
                DeleteModelsDevCacheBestEffort();
                providers = [];
                return false;
            }

            fresh = clock.GetUtcNow() - fetched < ModelsDevCacheTtl;
            lock (_modelsDevParseLock)
            {
                _modelsDevParsedKey = key;
                _modelsDevParsed = providers;
                _modelsDevParsedAt = fetched;
            }

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
            File.Delete(CacheFile);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException or NotSupportedException)
        {
        }
    }

    private void WriteModelsDevCacheBestEffort(string body)
    {
        try
        {
            var directory = Path.GetDirectoryName(CacheFile);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(CacheFile, $$"""{"fetched_at":"{{clock.GetUtcNow():O}}","payload":{{body}}}""");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException or NotSupportedException)
        {
        }
    }

}
