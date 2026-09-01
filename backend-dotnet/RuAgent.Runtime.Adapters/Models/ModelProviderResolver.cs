using RuAgent.Runtime.Adapters.Bootstrap;

namespace RuAgent.Runtime.Adapters.Models;

public sealed class ModelProviderResolver : IModelProviderResolver
{
    private readonly Dictionary<string, IModelProvider> _providers;

    public ModelProviderResolver(IReadOnlyDictionary<string, IModelProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        if (!providers.ContainsKey(ProviderProtocols.ChatCompletions) || !providers.ContainsKey(ProviderProtocols.OpenAiResponses) || !providers.ContainsKey(ProviderProtocols.AnthropicMessages))
            throw new ArgumentException("All three protocols must be registered.", nameof(providers));
        _providers = new Dictionary<string, IModelProvider>(providers, StringComparer.Ordinal);
    }

    public IModelProvider Resolve(string protocol)
    {
        ProviderProtocols.Require(protocol);
        if (_providers.TryGetValue(protocol, out var provider)) return provider;
        throw new ArgumentException("Unknown provider protocol.", nameof(protocol));
    }
}
