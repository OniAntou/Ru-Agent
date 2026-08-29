using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools;

namespace RuAgent.Broker.Skills;

public sealed class SkillRegistry
{
    private static readonly Regex IdPattern = new("^[a-z][a-z0-9]*(?:\\.[a-z0-9][a-z0-9_-]*)+$", RegexOptions.Compiled);
    private static readonly Regex VersionPattern = new("^(0|[1-9]\\d*)\\.(0|[1-9]\\d*)\\.(0|[1-9]\\d*)$", RegexOptions.Compiled);
    private static readonly IReadOnlySet<string> SchemaKeywords = new HashSet<string>(
        [
            "required", "additionalProperties", "properties", "type", "enum", "const",
            "minimum", "maximum", "items", "maxItems", "minItems", "maxLength", "minLength",
        ],
        StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly Dictionary<(string Id, string Version), SkillManifest> _skills = [];

    public void Register(SkillManifest skill, ToolRegistry tools)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ArgumentNullException.ThrowIfNull(tools);
        Validate(skill, tools);
        lock (_gate)
        {
            if (!_skills.TryAdd((skill.Id, skill.Version), skill))
            {
                throw new InvalidOperationException("duplicate_skill");
            }
        }
    }

    public SkillManifest Resolve(string id, string version) => Get(id, version);

    public SkillManifest Get(string id, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        lock (_gate)
        {
            return _skills.TryGetValue((id, version), out var skill)
                ? skill
                : throw new KeyNotFoundException("skill_not_found");
        }
    }

    public IReadOnlyList<SkillManifest> List()
    {
        lock (_gate)
        {
            return _skills.Values
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .ThenBy(item => item.Version, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public IReadOnlyList<SkillManifest> ListGranted(PolicyGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var policy = new ToolPolicyEngine();
        return List()
            .Where(skill => policy.EvaluateSkill(grant, skill.Id, skill.RequiredScopes).Allowed)
            .ToArray();
    }

    private static void Validate(SkillManifest skill, ToolRegistry tools)
    {
        if (!IdPattern.IsMatch(skill.Id)
            || !VersionPattern.IsMatch(skill.Version)
            || skill.InputSchema is null
            || skill.OutputSchema is null
            || skill.Steps is null
            || skill.Steps.Count == 0
            || skill.Steps.Count > 32
            || skill.MaxSteps is < 1 or > 32
            || skill.TimeoutMs is < 1 or > 300_000
            || skill.TrustClass is not ("builtin" or "signed")
            || skill.Instructions is null
            || skill.Instructions.Length > SkillPackageLoader.DefaultMaxInstructionsBytes)
        {
            throw new ArgumentException("invalid_skill_manifest");
        }

        ValidateSchema(skill.InputSchema);
        ValidateSchema(skill.OutputSchema);

        if (skill.RequiredToolIds is null
            || skill.RequiredScopes is null
            || !skill.RequiredScopes.Contains(ToolScope.SkillExecute)
            || !skill.RequiredScopes.All(ToolScope.Known.Contains))
        {
            throw new ArgumentException(
                skill.RequiredScopes is not null && !skill.RequiredScopes.Contains(ToolScope.SkillExecute)
                    ? "skill_execute_scope_missing"
                    : "unknown_skill_scope");
        }

        foreach (var toolId in skill.RequiredToolIds)
        {
            if (string.IsNullOrWhiteSpace(toolId))
            {
                throw new ArgumentException("invalid_skill_tool");
            }

            if (!tools.List().Any(tool => string.Equals(tool.Id, toolId, StringComparison.Ordinal)))
            {
                throw new ArgumentException("skill_tool_not_registered");
            }
        }

        foreach (var step in skill.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Name) || !Enum.IsDefined(step.Kind))
            {
                throw new ArgumentException("invalid_skill_step");
            }

            switch (step.Kind)
            {
                case SkillStepKind.CallTool:
                    ValidateCallTool(step, skill, tools);
                    break;
                case SkillStepKind.ValidateResult:
                    if (!SkillStepEvaluator.IsKnownPredicate(step.Name)
                        || step.ToolId is not null
                        || step.TrueIndex is not null
                        || step.FalseIndex is not null)
                    {
                        throw new ArgumentException(
                            SkillStepEvaluator.IsKnownPredicate(step.Name)
                                ? "invalid_skill_step"
                                : "unknown_skill_predicate");
                    }

                    break;
                case SkillStepKind.Branch:
                    if (!SkillStepEvaluator.IsKnownPredicate(step.BranchOn ?? step.Name)
                        || step.TrueIndex is null
                        || step.FalseIndex is null
                        || step.TrueIndex.Value < 0
                        || step.TrueIndex.Value >= skill.Steps.Count
                        || step.FalseIndex.Value < 0
                        || step.FalseIndex.Value >= skill.Steps.Count)
                    {
                        throw new ArgumentException("invalid_skill_branch");
                    }

                    break;
                case SkillStepKind.NamedTransform:
                    if (!SkillNamedTransformRegistry.IsKnown(step.Transform ?? step.Name)
                        || step.ToolId is not null
                        || step.TrueIndex is not null
                        || step.FalseIndex is not null)
                    {
                        throw new ArgumentException(
                            SkillNamedTransformRegistry.IsKnown(step.Transform ?? step.Name)
                                ? "invalid_skill_step"
                                : "unknown_skill_transform");
                    }

                    break;
                default:
                    throw new ArgumentException("invalid_skill_step");
            }
        }
    }

    private static void ValidateCallTool(SkillStep step, SkillManifest skill, ToolRegistry tools)
    {
        if (string.IsNullOrWhiteSpace(step.ToolId)
            || !skill.RequiredToolIds.Contains(step.ToolId)
            || (step.Arguments is not null && step.Arguments.Count > 128))
        {
            throw new ArgumentException("undeclared_skill_tool");
        }

        var candidates = tools.List()
            .Where(tool => string.Equals(tool.Id, step.ToolId, StringComparison.Ordinal))
            .ToArray();
        var manifest = step.ToolVersion is null
            ? candidates.FirstOrDefault()
            : candidates.FirstOrDefault(tool => string.Equals(tool.Version, step.ToolVersion, StringComparison.Ordinal));
        if (manifest is null)
        {
            throw new ArgumentException("skill_tool_not_registered");
        }

        if (!manifest.Scopes.All(skill.RequiredScopes.Contains))
        {
            throw new ArgumentException("skill_scope_missing");
        }

        if (step.Arguments is not null)
        {
            try
            {
                ToolSchemaValidator.Validate(manifest.InputSchema, step.Arguments);
            }
            catch (ToolSchemaException error) when (error.Code == "invalid_arguments")
            {
                // A declarative argument may contain a bounded $input placeholder. Validate the
                // bound arguments at execution time after the placeholder is resolved.
            }
            catch (ToolSchemaException error)
            {
                throw new ArgumentException("invalid_skill_arguments", error);
            }
        }
    }

    private static void ValidateSchema(JsonObject schema)
    {
        if (schema.Count == 0 || schema.Any(pair => !SchemaKeywords.Contains(pair.Key)))
        {
            throw new ArgumentException("invalid_skill_schema");
        }

        if (schema["type"] is not JsonValue typeNode
            || !typeNode.TryGetValue<string>(out var type)
            || type is not ("object" or "array" or "string" or "integer" or "number" or "boolean" or "null"))
        {
            throw new ArgumentException("invalid_skill_schema");
        }

        if (schema["required"] is JsonNode requiredNode)
        {
            if (requiredNode is not JsonArray required
                || required.Any(item => item is not JsonValue value
                    || !value.TryGetValue<string>(out var name)
                    || string.IsNullOrWhiteSpace(name)))
            {
                throw new ArgumentException("invalid_skill_schema");
            }
        }

        if (schema["properties"] is JsonNode propertiesNode)
        {
            if (propertiesNode is not JsonObject properties)
            {
                throw new ArgumentException("invalid_skill_schema");
            }

            foreach (var property in properties)
            {
                if (property.Value is not JsonObject child)
                {
                    throw new ArgumentException("invalid_skill_schema");
                }

                ValidateSchema(child);
            }
        }

        if (schema["items"] is JsonNode itemsNode)
        {
            if (itemsNode is not JsonObject items)
            {
                throw new ArgumentException("invalid_skill_schema");
            }

            ValidateSchema(items);
        }

        if (schema["additionalProperties"] is JsonNode additional)
        {
            if (additional is not JsonValue value || !value.TryGetValue<bool>(out _))
            {
                throw new ArgumentException("invalid_skill_schema");
            }
        }
    }
}
