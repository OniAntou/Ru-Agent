using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using RuBackend.Business.Settings;

namespace RuBackend.Providers;

/// <summary>Bounded Host-owned catalog cache. No provider/key/protocol fallback.</summary>
public sealed class ProviderCatalogService(HttpClient http, ICredentialStore credentials, TimeProvider clock)
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _credentialRevisions = new(StringComparer.Ordinal);

    private sealed class Entry
    {
        public string Identity { get; } = Guid.NewGuid().ToString("N");
        public IReadOnlyList<CatalogModel> Models { get; set; } = [];
        public DateTimeOffset? Success { get; set; }
        public DateTimeOffset Attempt { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset RetryAfter { get; set; } = DateTimeOffset.MinValue;
        public string? Error { get; set; }
    }

    public Task<ProviderCatalogSnapshot> GetAsync(SettingsSnapshot settings, bool refresh, CancellationToken ct) =>
        GetAsyncCore(ProviderSelection.From(settings), settings, refresh, ct);

    public Task<ProviderCatalogSnapshot> GetAsync(string alias, SettingsSnapshot settings, bool refresh, CancellationToken ct) =>
        GetAsyncCore(ProviderSelection.From(settings, alias), settings, refresh, ct);

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
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_credentialRevisions.TryGetValue(scope, out var previous) && previous != revision)
                _entries.Remove(scope);
            _credentialRevisions[scope] = revision;
            if (!_entries.TryGetValue(scope, out var entry))
            {
                if (_entries.Count >= 16)
                {
                    var oldest = _entries.MinBy(pair => pair.Value.Attempt).Key;
                    _entries.Remove(oldest);
                    _credentialRevisions.Remove(oldest);
                }
                _entries[scope] = entry = new Entry();
            }
            if (selection.Alias is "openai" or "anthropic" && string.IsNullOrWhiteSpace(credential))
            {
                entry.Error = "catalog_unauthorized";
                var earlySupported = entry.Models.Where(m => m.Supported).Select(m => m.Id).ToArray();
                return new(entry.Error is null && entry.Success.HasValue ? "ok" : "error",
                    selection.Alias, entry.Identity, selection.Model, ProviderReasoningPreferences.Read(settings),
                    earlySupported, entry.Models,
                    entry.Success.HasValue && (entry.Error is not null || clock.GetUtcNow() - entry.Success.Value >= TimeSpan.FromMinutes(5)),
                    entry.Error ?? (entry.Success.HasValue ? null : "catalog_unavailable"), entry.Success)
                    { CredentialRevision = revision };
            }
            var now = clock.GetUtcNow();
            var fresh = entry.Success.HasValue && now - entry.Success.Value < TimeSpan.FromMinutes(5);
            if (now >= entry.RetryAfter && now - entry.Attempt >= TimeSpan.FromSeconds(30)
                && (refresh || !fresh || entry.Error is not null))
            {
                var previousAttempt = entry.Attempt;
                entry.Attempt = now;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    using var request = ProviderCatalogRequestFactory.Create(selection, credential);
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        if (response.StatusCode == HttpStatusCode.TooManyRequests)
                        {
                            var retry = response.Headers.RetryAfter?.Delta
                                ?? (response.Headers.RetryAfter?.Date - now) ?? TimeSpan.FromSeconds(30);
                            entry.RetryAfter = now + TimeSpan.FromSeconds(Math.Clamp(retry.TotalSeconds, 30, 3600));
                        }
                        throw new ProviderCatalogException(response.StatusCode switch
                        {
                            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "catalog_unauthorized",
                            HttpStatusCode.TooManyRequests => "catalog_rate_limited",
                            _ => "catalog_http_error",
                        });
                    }
                    if (response.Content.Headers.ContentLength > MaximumBytes) throw new ProviderCatalogException("catalog_too_large");
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    using var memory = new MemoryStream();
                    var buffer = new byte[8192];
                    int read;
                    while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                    {
                        if (memory.Length + read > MaximumBytes) throw new ProviderCatalogException("catalog_too_large");
                        memory.Write(buffer, 0, read);
                    }
                    var json = new UTF8Encoding(false, true).GetString(memory.ToArray());
                    var models = ProviderCatalogParser.Parse(selection.Backend, json);
                    ct.ThrowIfCancellationRequested();
                    entry.Models = models;
                    entry.Success = clock.GetUtcNow();
                    entry.Error = null;
                    entry.RetryAfter = DateTimeOffset.MinValue;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    entry.Attempt = previousAttempt;
                    throw;
                }
                catch (Exception error) when (error is ProviderCatalogException or HttpRequestException
                    or OperationCanceledException or DecoderFallbackException or IOException)
                {
                    entry.Error = error is ProviderCatalogException known ? known.Code
                        : error is OperationCanceledException ? "catalog_timeout" : "catalog_unavailable";
                }
            }
            var supportedIds = entry.Models.Where(m => m.Supported).Select(m => m.Id).ToArray();
            return new(entry.Error is null && entry.Success.HasValue ? "ok" : "error",
                selection.Alias, entry.Identity, selection.Model, ProviderReasoningPreferences.Read(settings),
                supportedIds, entry.Models,
                entry.Success.HasValue && (entry.Error is not null || clock.GetUtcNow() - entry.Success.Value >= TimeSpan.FromMinutes(5)),
                entry.Error ?? (entry.Success.HasValue ? null : "catalog_unavailable"), entry.Success)
                { CredentialRevision = revision };
        }
        finally { _gate.Release(); }
    }
}
