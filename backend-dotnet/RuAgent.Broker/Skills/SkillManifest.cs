using System.Text.Json.Nodes;

namespace RuAgent.Broker.Skills;

public enum SkillStepKind
{
    CallTool,
    ValidateResult,
    Branch,
    NamedTransform,
}

public sealed record SkillStep(
    SkillStepKind Kind,
    string Name,
    string? ToolId = null,
    JsonObject? Arguments = null,
    string? BranchOn = null,
    int? TrueIndex = null,
    int? FalseIndex = null,
    string? Transform = null,
    string? ToolVersion = null);

public sealed record SkillManifest(
    string Id,
    string Version,
    JsonObject InputSchema,
    JsonObject OutputSchema,
    IReadOnlySet<string> RequiredToolIds,
    IReadOnlySet<string> RequiredScopes,
    IReadOnlyList<SkillStep> Steps,
    bool RequiresApproval,
    int MaxSteps,
    int TimeoutMs,
    string TrustClass,
    string Instructions);
