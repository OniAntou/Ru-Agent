using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Configuration;
using RuAgent.Protocol;
using RuAgent.Protocol.Tools;

namespace RuAgent.Broker.Tools;

public sealed class ToolCatalog
{
    // Manifest defaults shared by every builtin tool. Per-tool values come from the descriptor table.
    private const int ManifestTimeoutMs = 30_000;
    private const int DefaultMaxOutputBytes = 4 * 1024 * 1024;
    private const string ManifestTrustClass = "builtin";

    // Bound ids come from the descriptor table. Host-intercepted tools such as ask_user are excluded:
    // the Host consumes them before gateway dispatch, so the Broker must never receive them.
    private static readonly IReadOnlyList<string> BoundIds =
        AgentToolDescriptorTable.BrokerBound
            .Select(row => row.Id)
            .ToArray();

    public ToolCatalog(
        IReadOnlyDictionary<string, ToolExecutor> executors,
        WebOptions? webOptions = null)
    {
        ArgumentNullException.ThrowIfNull(executors);
        webOptions ??= new WebOptions();
        var manifests = Manifests(webOptions).ToArray();
        var known = BoundIds.ToHashSet(StringComparer.Ordinal);
        if (executors.Keys.Any(id => !known.Contains(id)))
        {
            throw new InvalidOperationException("unknown_tool_executor");
        }

        if (manifests.Any(manifest => !executors.TryGetValue(manifest.Id, out var executor) || executor is null))
        {
            throw new InvalidOperationException("incomplete_tool_binding");
        }

        // The binding relation must hold in both directions. Without this check the
        // catalog could grow past the manifests and the model would be offered a tool
        // the execution authority has no binding for.
        var boundIds = manifests.Select(manifest => manifest.Id).ToHashSet(StringComparer.Ordinal);
        if (BoundIds.Any(id => !boundIds.Contains(id)))
        {
            throw new InvalidOperationException("incomplete_tool_catalog");
        }

        foreach (var manifest in manifests)
        {
            var definition = AgentToolDefinitionCatalog.Find(manifest.Id, manifest.Version);
            if (definition is null
                || !string.Equals(
                    AgentToolDefinitionCatalog.ComputeSchemaDigest(definition.InputSchema),
                    AgentToolDefinitionCatalog.ComputeSchemaDigest(manifest.InputSchema),
                    StringComparison.Ordinal))
            {
                throw new AgentProtocolException("tool_contract_mismatch");
            }
        }

        Registry = new ToolBindingRegistry();
        foreach (var manifest in manifests)
        {
            var definition = AgentToolDefinitionCatalog.Find(manifest.Id, manifest.Version)
                ?? throw new AgentProtocolException("tool_contract_mismatch");
            Registry.Register(new ToolBinding(
                manifest,
                executors[manifest.Id],
                ModelEnabled: true,
                Availability: "executable"));
        }
    }

    public ToolBindingRegistry Registry { get; }

    public bool RequiresApprovalFor(string id)
    {
        var manifest = Registry.Manifests.List().FirstOrDefault(item => item.Id == id)
            ?? throw new KeyNotFoundException("tool_not_found");
        return manifest.Risk is RiskClass.External or RiskClass.Destructive
            || manifest.RequiresApproval;
    }

    /// <summary>One manifest per Broker-bound descriptor row, in table order.</summary>
    internal static IEnumerable<ToolManifest> Manifests(WebOptions webOptions)
    {
        foreach (var row in AgentToolDescriptorTable.BrokerBound)
        {
            yield return Manifest(row);
        }
    }

    private static ToolManifest Manifest(AgentToolDescriptor row) => new(
        row.Id,
        row.Version,
        ToolDescriptorMapping.ToAdapterName(row.Adapter),
        ToolDescriptorMapping.ToRiskClass(row.Risk),
        new HashSet<string>(row.AllScopes, StringComparer.Ordinal),
        AgentToolDefinitionCatalog.Find(row.Id, row.Version)?.InputSchema
            ?? throw new AgentProtocolException("tool_contract_not_found"),
        new JsonObject { ["type"] = "object" },
        ManifestTimeoutMs,
        row.MaxOutputBytes ?? DefaultMaxOutputBytes,
        ManifestTrustClass,
        WorkspaceRequired: row.WorkspaceRequired,
        RequiresApproval: row.RequiresApproval);
}
