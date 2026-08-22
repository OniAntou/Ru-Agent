namespace RuAgent.Broker.Tools;

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
            return _bindings.Values
                .OrderBy(item => item.Manifest.Id, StringComparer.Ordinal)
                .ThenBy(item => item.Manifest.Version, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public IReadOnlyList<AgentToolCapability> ListCapabilities()
    {
        lock (_gate)
        {
            return _bindings.Values
                .OrderBy(item => item.Manifest.Id, StringComparer.Ordinal)
                .ThenBy(item => item.Manifest.Version, StringComparer.Ordinal)
                .Select(binding => new AgentToolCapability(
                    binding.Manifest.Id,
                    binding.Manifest.Version,
                    new HashSet<string>(binding.Manifest.Scopes, StringComparer.Ordinal),
                    binding.Manifest.Risk.ToString().ToLowerInvariant(),
                    binding.Availability,
                    binding.ModelEnabled))
                .ToArray();
        }
    }
}
