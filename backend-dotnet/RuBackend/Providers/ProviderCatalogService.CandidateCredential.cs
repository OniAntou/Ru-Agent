using System.Net;
using System.Net.Http.Headers;
using RuBackend.Business.Settings;

namespace RuBackend.Providers;

/// <summary>What checking a candidate key before it is stored established.</summary>
public enum CandidateKeyVerdict
{
    /// <summary>The provider accepted the key on a request that needs one.</summary>
    Accepted,

    /// <summary>
    /// The provider answered, but nothing it offers before generation needs a key, so
    /// the key could not be told apart from a made-up one. It is stored and checked
    /// by the first model request.
    /// </summary>
    Unchecked,

    /// <summary>The provider refused the key (HTTP 401/403).</summary>
    Rejected,

    /// <summary>No verdict: timeout, rate limit, outage or an unreadable answer.</summary>
    Unavailable,
}

public sealed partial class ProviderCatalogService
{
    private static readonly TimeSpan KeyCheckTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Decides whether a candidate key is good before anything is stored.
    /// </summary>
    /// <remarks>
    /// A successful catalog read was the old proof, and for some providers it proves
    /// nothing: OpenRouter and Kilo list their models to anyone, and Cloudflare's list
    /// comes from Models.dev without the key ever being sent, so a mistyped key was
    /// reported as saved and replaced a working one. A provider with a known
    /// authenticated endpoint is asked there; any other Models.dev provider whose list
    /// also answers an unauthenticated request is reported as <see cref="CandidateKeyVerdict.Unchecked"/>
    /// instead of accepted. The unauthenticated request carries no credential at all.
    /// </remarks>
    public async Task<CandidateKeyVerdict> VerifyCandidateCredentialAsync(
        string alias, string key, SettingsSnapshot settings, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(settings);
        var account = ProviderSelection.CredentialAccountForAlias(alias)
            ?? throw new ArgumentException("Provider does not use credentials.");
        var selection = ProviderSelection.NormalizeModelsDevBackend(alias) is { } generic
            ? await ResolveGenericAsync(settings, generic, ct).ConfigureAwait(false)
            : ProviderSelection.From(settings, alias);

        if (selection.Alias == "custom" || selection.IsCustomProvider)
            return await VerifyCustomEndpointKeyAsync(selection, key, ct).ConfigureAwait(false);

        if (ProviderOverrides.KeyCheckUri(selection) is { } checkUri)
        {
            // The check request differs from a working one only by its credential, so 401,
            // and the 400 Cloudflare answers a malformed token with ("Authentication
            // failed"), mean the key is not a key. 403 can be a real key that lacks only the
            // permission this check reads (a Cloudflare token scoped to inference), so it
            // is stored unchecked rather than refused for good.
            return await SendKeyCheckAsync(checkUri, key, ct).ConfigureAwait(false) switch
            {
                HttpStatusCode status when (int)status is >= 200 and < 300 => CandidateKeyVerdict.Accepted,
                HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest => CandidateKeyVerdict.Rejected,
                HttpStatusCode.Forbidden => CandidateKeyVerdict.Unchecked,
                _ => CandidateKeyVerdict.Unavailable,
            };
        }

        // The normal provider-specific catalog path, but an isolated cache and a
        // read-only candidate credential: the candidate never reaches the vault, and a
        // rejected probe cannot poison the signed-in provider's live catalog.
        var probe = new ProviderCatalogService(http, new CandidateCredential(account, key), clock, _modelsDevSource.CacheFile);
        var snapshot = await probe.GetAsync(selection, settings, refresh: true, ct).ConfigureAwait(false);
        if (snapshot.Status != "ok")
        {
            return snapshot.ErrorCode == "catalog_unauthorized"
                ? CandidateKeyVerdict.Rejected
                : CandidateKeyVerdict.Unavailable;
        }

        // OpenAI and Anthropic list models only for a valid key. A Models.dev provider
        // proves the same only when its list refuses an unauthenticated request.
        if (!selection.IsModelsDev) return CandidateKeyVerdict.Accepted;
        // A list served from Models.dev never carried the key; ask the generation route instead.
        if (ProviderRoutes.UsesModelsDevCatalog(selection.Alias, selection.Protocol))
            return await ProbeGenerationRouteAsync(selection, key, ct).ConfigureAwait(false);
        return await SendKeyCheckAsync(selection.ModelsUri, key: null, ct).ConfigureAwait(false)
            is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? CandidateKeyVerdict.Accepted
            : CandidateKeyVerdict.Unchecked;
    }

    /// <summary>
    /// A key for a server the user runs or chose: the legacy Custom OpenAI slot or a
    /// named custom provider (ADR 0034).
    /// </summary>
    /// <remarks>
    /// Such a server is often local and offline while it is being set up, may not serve
    /// <c>/models</c> at all, or may need no key. Refusing the key for any of that would
    /// make the provider impossible to configure, so only a 401 refuses it; everything
    /// short of a proof saves it unchecked. The settings transition has already refused
    /// a key bound for plain HTTP to another computer before this runs.
    /// </remarks>
    private async Task<CandidateKeyVerdict> VerifyCustomEndpointKeyAsync(
        ProviderSelection selection, string key, CancellationToken ct)
    {
        HttpStatusCode? keyed;
        using (var request = ProviderCatalogRequestFactory.Create(selection, key))
            keyed = await SendKeyCheckAsync(request, ct).ConfigureAwait(false);
        if (keyed == HttpStatusCode.Unauthorized) return CandidateKeyVerdict.Rejected;
        if (keyed is not { } status || (int)status is < 200 or >= 300) return CandidateKeyVerdict.Unchecked;

        HttpStatusCode? anonymous;
        using (var request = ProviderCatalogRequestFactory.Create(selection, string.Empty))
            anonymous = await SendKeyCheckAsync(request, ct).ConfigureAwait(false);
        return anonymous is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? CandidateKeyVerdict.Accepted
            : CandidateKeyVerdict.Unchecked;
    }

    /// <summary>
    /// Checks a key against the generation route with an empty request body, which no
    /// provider can run, so nothing is generated or billed.
    /// </summary>
    /// <remarks>
    /// The verdict leans on one observation: when the same URL refuses the body without a
    /// key (401/403), the provider authenticates before it reads the body, so a keyed
    /// answer that complains about the body (400, 404, 405, 415, 422) means the key got
    /// through, and a keyed 401 means it did not. A route that reads the body first, or
    /// answers anything else, leaves the key unchecked.
    /// </remarks>
    private async Task<CandidateKeyVerdict> ProbeGenerationRouteAsync(
        ProviderSelection selection, string key, CancellationToken ct)
    {
        var anonymous = await SendAndDisposeAsync(EmptyGenerationRequest(selection, key: null), ct).ConfigureAwait(false);
        if (anonymous is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)) return CandidateKeyVerdict.Unchecked;
        return await SendAndDisposeAsync(EmptyGenerationRequest(selection, key), ct).ConfigureAwait(false) switch
        {
            HttpStatusCode.Unauthorized => CandidateKeyVerdict.Rejected,
            HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed
                or HttpStatusCode.UnsupportedMediaType or HttpStatusCode.UnprocessableEntity => CandidateKeyVerdict.Accepted,
            HttpStatusCode status when (int)status is >= 200 and < 300 => CandidateKeyVerdict.Accepted,
            _ => CandidateKeyVerdict.Unchecked,
        };
    }

    private static HttpRequestMessage EmptyGenerationRequest(ProviderSelection selection, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, selection.CompletionUri)
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        if (key is null) return request;
        if (selection.Protocol == RuAgent.Runtime.Adapters.Bootstrap.ProviderProtocols.AnthropicMessages)
        {
            request.Headers.TryAddWithoutValidation("x-api-key", key);
            request.Headers.TryAddWithoutValidation("anthropic-version", ProviderCatalogRequestFactory.AnthropicVersion);
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return request;
    }

    /// <summary>
    /// One bounded GET; returns the status, or null when no answer arrived. Nothing
    /// from the body is read or kept. A null <paramref name="key"/> sends no credential.
    /// </summary>
    private Task<HttpStatusCode?> SendKeyCheckAsync(Uri uri, string? key, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (key is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return SendAndDisposeAsync(request, ct);
    }

    private async Task<HttpStatusCode?> SendAndDisposeAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request) return await SendKeyCheckAsync(request, ct).ConfigureAwait(false);
    }

    private async Task<HttpStatusCode?> SendKeyCheckAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(KeyCheckTimeout);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            return response.StatusCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private sealed class CandidateCredential(string account, string key) : ICredentialStore
    {
        public Task<string?> GetAsync(string serviceName, string accountName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(serviceName == "Ru" && accountName == account ? key : null);
        }
        public Task SetAsync(string serviceName, string accountName, string secret, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Candidate credentials are read-only.");
        public Task DeleteAsync(string serviceName, string accountName, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Candidate credentials are read-only.");
    }
}
