using System.Text.Json.Nodes;

namespace RuAgent.Protocol.Tools;

/// <summary>
/// Risk class of a tool. Members match the Broker's RiskClass one for one; the Broker maps them
/// with an exhaustive switch, so a new member here cannot compile without a mapping there.
/// </summary>
public enum AgentToolRisk
{
    Read,
    Reversible,
    External,
    Destructive,
}

/// <summary>
/// Adapter identity of a tool's Broker binding. Every member except <see cref="HostIntercepted"/>
/// names one trusted adapter. <see cref="HostIntercepted"/> marks a tool the Host consumes before
/// gateway dispatch (ask_user): it has no manifest and no binding.
/// </summary>
public enum AgentToolAdapter
{
    Builtin,
    Desktop,
    Terminal,
    Web,
    Connector,
    /// <summary>The configured language server of code_intelligence (ADR 0037).</summary>
    LanguageServer,
    HostIntercepted,
}

/// <summary>
/// How a call may change state. The Broker gateway and the hook gateway both classify through
/// this one value, so the two can no longer disagree about the same tool.
/// </summary>
public enum AgentToolAccess
{
    ReadOnly,
    Mutation,
    Terminal,
}

/// <summary>
/// The single declarative description of one model-visible tool. The model definition, the Broker
/// manifest, the adapter allowlist, the session grant and the access class are all derived from
/// this row, so adding a tool means adding one row to <see cref="AgentToolDescriptorTable"/>.
/// </summary>
public sealed record AgentToolDescriptor
{
    /// <summary>Position in the model-visible order. Must equal the row's index in the table.</summary>
    public required int Ordinal { get; init; }

    public required string Id { get; init; }

    public required string Version { get; init; }

    public required string Description { get; init; }

    /// <summary>The primary scope: the manifest's required scope and the model-facing definition scope.</summary>
    public required string Scope { get; init; }

    /// <summary>Additional scopes the manifest requires. Empty for most tools.</summary>
    public required IReadOnlyList<string> ExtraScopes { get; init; }

    public required AgentToolRisk Risk { get; init; }

    public required bool RequiresApproval { get; init; }

    public required AgentToolAdapter Adapter { get; init; }

    public required bool WorkspaceRequired { get; init; }

    public required AgentToolAccess Access { get; init; }

    /// <summary>
    /// The operation family this tool's calls are evaluated under by policy rules that name a family (for example
    /// "read", "write", "edit", "bash"). Null when the tool has no family. Explicit on every row, so no row takes
    /// a family by default. Read it through <see cref="AgentToolDescriptorTable.Find"/>; the legacy rule-name
    /// aliases that point at families live in <see cref="AgentToolPolicyAliases"/>.
    /// </summary>
    public required string? PolicyFamily { get; init; }

    /// <summary>Output bound in bytes. Null means the Broker's default output bound.</summary>
    public int? MaxOutputBytes { get; init; }

    public required Func<JsonObject> InputSchema { get; init; }

    public Func<JsonObject>? ExampleArguments { get; init; }

    /// <summary>Every scope the Broker manifest requires: the primary scope followed by the extras.</summary>
    public IEnumerable<string> AllScopes => [Scope, .. ExtraScopes];

    /// <summary>Whether the Broker binds this tool. Host-intercepted tools have no manifest.</summary>
    public bool IsBrokerBound => Adapter is not AgentToolAdapter.HostIntercepted;

    internal AgentToolDefinition ToDefinition() =>
        new(Ordinal, Id, Version, Description, Scope, InputSchema, ExampleArguments);
}
