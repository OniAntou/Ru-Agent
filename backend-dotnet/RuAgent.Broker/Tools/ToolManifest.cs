using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;

namespace RuAgent.Broker.Tools;

public sealed record ToolManifest(
    string Id,
    string Version,
    string Adapter,
    RiskClass Risk,
    IReadOnlySet<string> Scopes,
    JsonObject InputSchema,
    JsonObject OutputSchema,
    int TimeoutMs,
    int MaxOutputBytes,
    string TrustClass,
    bool NetworkAllowed = false,
    bool WorkspaceRequired = false,
    bool RequiresApproval = false);
