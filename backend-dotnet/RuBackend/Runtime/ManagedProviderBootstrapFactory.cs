using RuAgent.Runtime.Adapters.Bootstrap;

namespace RuBackend.Runtime;

public sealed class ManagedProviderBootstrapFactory
{
    public Task<string> CreateAsync(
        string sessionRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionRoot);
        cancellationToken.ThrowIfCancellationRequested();

        var data = new ProviderBootstrapData(
            "per-step",
            ProviderProtocols.ChatCompletions,
            new Uri("http://127.0.0.1/"),
            "per-step-required",
            string.Empty);
        return Task.FromResult(ProtectedProviderBootstrap.Write(sessionRoot, data));
    }
}
