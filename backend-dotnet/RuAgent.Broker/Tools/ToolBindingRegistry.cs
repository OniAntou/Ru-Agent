namespace RuAgent.Broker.Tools;

using RuAgent.Broker.Policy;
using RuAgent.Protocol;

public sealed class ToolBindingRegistry
{
    private readonly object _gate = new();
    private readonly ToolRegistry _manifests = new();
    private readonly Dictionary<(string Id, string Version), ToolBinding> _bindings = [];

    public ToolRegistry Manifests => _manifests;

    public void Register(ToolBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(binding.Manifest);
        if (binding.Executor is null)
        {
            throw new ArgumentException("missing_tool_executor", nameof(binding));
        }

        var key = (binding.Manifest.Id, binding.Manifest.Version);
        lock (_gate)
        {
            if (_bindings.ContainsKey(key))
            {
                throw new InvalidOperationException("duplicate_tool");
            }

            _manifests.Register(binding.Manifest);
            _bindings.Add(key, binding);
        }
    }

    public ToolBinding Get(string id, string version)
    {
        lock (_gate)
        {
            return _bindings.TryGetValue((id, version), out var binding)
                ? binding
                : throw new KeyNotFoundException("tool_not_found");
        }
    }

    public IReadOnlyList<ToolBinding> List()
    {
        lock (_gate)
        {
            return OrderedBindings().ToArray();
        }
    }

    public IEnumerable<ToolBinding> ModelCapabilities(
        CapabilityProfile profile,
        IReadOnlySet<string>? additionalScopes = null)
    {
        var scopes = EffectiveScopes(profile, additionalScopes);
        lock (_gate)
        {
            return OrderedBindings()
                .Where(binding => binding.Manifest.Scopes.All(scopes.Contains))
                .ToArray();
        }
    }

    public IReadOnlyList<AgentToolCapability> ListCapabilities()
    {
        lock (_gate)
        {
            return OrderedBindings().Select(ToCapability).ToArray();
        }
    }

    public IReadOnlyList<AgentToolCapability> ListCapabilities(
        CapabilityProfile profile,
        IReadOnlySet<string>? additionalScopes = null)
    {
        lock (_gate)
        {
            var scopes = EffectiveScopes(profile, additionalScopes);
            return OrderedBindings()
                .Where(binding => binding.Manifest.Scopes.All(scopes.Contains))
                .Select(ToCapability)
                .ToArray();
        }
    }

    private IEnumerable<ToolBinding> OrderedBindings() =>
        _bindings.Values
            .OrderBy(item => item.Manifest.Id, StringComparer.Ordinal)
            .ThenBy(item => item.Manifest.Version, StringComparer.Ordinal);

    private static IReadOnlySet<string> EffectiveScopes(
        CapabilityProfile profile,
        IReadOnlySet<string>? additionalScopes)
    {
        var scopes = new HashSet<string>(profile.Scopes(), StringComparer.Ordinal);
        if (additionalScopes is not null)
        {
            scopes.UnionWith(additionalScopes);
        }

        return scopes;
    }

    private static AgentToolCapability ToCapability(ToolBinding binding) =>
        new(
            binding.Manifest.Id,
            binding.Manifest.Version,
            new HashSet<string>(binding.Manifest.Scopes, StringComparer.Ordinal),
            binding.Manifest.Risk.ToString().ToLowerInvariant(),
            binding.Availability,
            binding.ModelEnabled);
}
