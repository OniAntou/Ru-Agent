using System.Collections.Concurrent;
using System.Net;
using RuBackend.Business.Settings;

namespace RuBackend.Tests.Fakes;

internal sealed class EphemeralTestCredentialStore : ICredentialStore
{
    private readonly ConcurrentDictionary<(string, string), string> _values = new();
    public Task<string?> GetAsync(string serviceName, string accountName, CancellationToken cancellationToken) =>
        Task.FromResult(_values.GetValueOrDefault((serviceName, accountName)));
    public Task SetAsync(string serviceName, string accountName, string secret, CancellationToken cancellationToken)
    { _values[(serviceName, accountName)] = secret; return Task.CompletedTask; }
    public Task DeleteAsync(string serviceName, string accountName, CancellationToken cancellationToken)
    { _values.TryRemove((serviceName, accountName), out _); return Task.CompletedTask; }
}

internal sealed class SyntheticCatalogHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":[{"id":"deepseek-v4-flash-free"},{"id":"gpt-5.6-luna"}]}"""),
        });
}
