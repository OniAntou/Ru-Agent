using System.Net;
using System.Security.Cryptography;
using System.Text;
using RuBackend.Business.Settings;

namespace RuBackend.Providers;

/// <summary>Named custom providers (ADR 0034): the configured list and Host-owned discovery.</summary>
public sealed partial class ProviderCatalogService
{
    /// <summary>
    /// A custom provider's catalog is its configured model list, read from the
    /// settings in hand: no request, no cache, and nothing a server can add or
    /// take away between the editor and a run.
    /// </summary>
    private async Task<ProviderCatalogSnapshot> GetCustomProviderAsync(
        ProviderSelection selection,
        SettingsSnapshot settings,
        CancellationToken ct)
    {
        var definition = CustomProviders.Find(settings, selection.Backend)
            ?? throw new ProviderCatalogException("provider_selection_invalid");
        var credential = await ReadCredentialAsync(selection, ct).ConfigureAwait(false);
        var models = definition.Models
            .Select(model => new CatalogModel(model.Id, true, definition.Protocol,
                new CatalogReasoning(CatalogReasoningStates.Unknown, [])) { ContextLength = model.ContextWindow })
            .ToArray();
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            selection.Backend + "\n" + definition.BaseUrl + "\n" + definition.Protocol + "\n"
            + string.Join("\n", definition.Models.Select(model => model.Id)))))[..32];
        return new ProviderCatalogSnapshot("ok", selection.Backend, identity, selection.Model,
            ProviderReasoningPreferences.Read(settings, selection), models.Select(model => model.Id).ToArray(),
            models, false, null, clock.GetUtcNow())
            { CredentialRevision = Revision(credential) };
    }

    /// <summary>
    /// Asks the provider which models it serves (<c>GET {base}/models</c>) with the
    /// stored key. The answer is returned for the editor to pick from and is never
    /// stored, so it doubles as the connection test.
    /// </summary>
    public async Task<ProviderCatalogSnapshot> DiscoverCustomModelsAsync(
        string backend,
        SettingsSnapshot settings,
        CancellationToken ct)
    {
        var normalized = CustomProviders.NormalizeBackend(backend)
            ?? throw new ProviderCatalogException("provider_not_active");
        ProviderSelection selection;
        try
        {
            selection = ProviderSelection.From(settings, normalized);
        }
        catch (ArgumentException)
        {
            throw new ProviderCatalogException("provider_not_active");
        }

        var credential = await ReadCredentialAsync(selection, ct).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var request = ProviderCatalogRequestFactory.Create(selection, credential);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ProviderCatalogException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "catalog_unauthorized",
                    HttpStatusCode.TooManyRequests => "catalog_rate_limited",
                    _ => "catalog_http_error",
                });
            }

            if (response.Content.Headers.ContentLength > MaximumBytes)
                throw new ProviderCatalogException("catalog_too_large");
            var bytes = await ReadBoundedAsync(response, timeout.Token).ConfigureAwait(false);
            var models = ProviderCatalogParser.ParseDiscovery(new UTF8Encoding(false, true).GetString(bytes));
            return new ProviderCatalogSnapshot("ok", selection.Backend, "discovery", selection.Model, "default",
                models.Select(model => model.Id).ToArray(), models, false, null, clock.GetUtcNow())
                { CredentialRevision = Revision(credential) };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is ProviderCatalogException or HttpRequestException
            or OperationCanceledException or DecoderFallbackException or IOException)
        {
            var code = error is ProviderCatalogException known ? known.Code
                : error is OperationCanceledException ? "catalog_timeout" : "catalog_unavailable";
            var now = clock.GetUtcNow();
            return new ProviderCatalogSnapshot("error", selection.Backend, "discovery", selection.Model, "default",
                [], [], false, code, null) { CredentialRevision = Revision(credential), ErrorAtUtc = now };
        }
    }

    private async Task<string> ReadCredentialAsync(ProviderSelection selection, CancellationToken ct) =>
        selection.Account is null ? "" : await credentials.GetAsync("Ru", selection.Account, ct).ConfigureAwait(false) ?? "";

    private static string Revision(string credential) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (memory.Length > MaximumBytes - read)
                throw new ProviderCatalogException("catalog_too_large");
            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }
}
