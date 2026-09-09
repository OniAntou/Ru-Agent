using System.Text.RegularExpressions;
using RuAgent.Broker.Policy;

namespace RuAgent.Broker.Tools;

public sealed class ToolRegistry
{
    private static readonly Regex IdPattern = new("^[a-z][a-z0-9_]*(?:\\.[a-z0-9][a-z0-9_-]*)*$", RegexOptions.Compiled);
    private static readonly Regex VersionPattern = new("^(0|[1-9]\\d*)\\.(0|[1-9]\\d*)\\.(0|[1-9]\\d*)$", RegexOptions.Compiled);
    private static readonly IReadOnlySet<string> TrustedAdapters = new HashSet<string>(
        ["builtin", "desktop", "terminal", "web", "connector"],
        StringComparer.Ordinal);
    private readonly Dictionary<(string Id, string Version), ToolManifest> _manifests = [];

    public void Register(ToolManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Validate(manifest);
        var key = (manifest.Id, manifest.Version);
        if (!_manifests.TryAdd(key, manifest))
        {
            throw new InvalidOperationException("duplicate_tool");
        }
    }

    public ToolManifest Get(string id, string version) =>
        _manifests.TryGetValue((id, version), out var manifest)
            ? manifest
            : throw new KeyNotFoundException("tool_not_found");

    public IReadOnlyList<ToolManifest> List() =>
        _manifests.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ThenBy(item => item.Version, StringComparer.Ordinal).ToArray();

    private static void Validate(ToolManifest manifest)
    {
        if (!IdPattern.IsMatch(manifest.Id) || !VersionPattern.IsMatch(manifest.Version))
        {
            throw new ArgumentException("invalid_tool_identity");
        }

        if (!TrustedAdapters.Contains(manifest.Adapter)
            || !Enum.IsDefined(manifest.Risk)
            || manifest.Scopes.Count == 0
            || !manifest.Scopes.All(ToolScope.Known.Contains)
            || manifest.InputSchema is null
            || manifest.OutputSchema is null
            || manifest.TimeoutMs is < 1 or > 300_000
            || manifest.MaxOutputBytes is < 1 or > 4 * 1024 * 1024
            || manifest.TrustClass is not ("builtin" or "allowlisted"))
        {
            throw new ArgumentException("invalid_tool_manifest");
        }

        if (manifest.NetworkAllowed && !manifest.Scopes.Contains(ToolScope.TerminalNetwork)
            && manifest.Adapter == "terminal")
        {
            throw new ArgumentException("network_scope_missing");
        }

        if (manifest.WorkspaceRequired
            && !manifest.Scopes.Contains(ToolScope.WorkspaceRead)
            && !manifest.Scopes.Contains(ToolScope.WorkspaceWrite))
        {
            throw new ArgumentException("workspace_scope_missing");
        }
    }
}
