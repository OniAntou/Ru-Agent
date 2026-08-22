using System.Text.RegularExpressions;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools;

namespace RuAgent.Broker.Skills;

public sealed class SkillRegistry
{
    private static readonly Regex IdPattern = new("^[a-z][a-z0-9]*(?:\\.[a-z0-9][a-z0-9_-]*)+$", RegexOptions.Compiled);
    private readonly Dictionary<(string Id, string Version), SkillManifest> _skills = [];

    public void Register(SkillManifest skill, ToolRegistry tools)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ArgumentNullException.ThrowIfNull(tools);
        Validate(skill, tools);
        if (!_skills.TryAdd((skill.Id, skill.Version), skill))
        {
            throw new InvalidOperationException("duplicate_skill");
        }
    }

    public SkillManifest Get(string id, string version) =>
        _skills.TryGetValue((id, version), out var skill)
            ? skill
            : throw new KeyNotFoundException("skill_not_found");

    public IReadOnlyList<SkillManifest> List() =>
        _skills.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ThenBy(item => item.Version, StringComparer.Ordinal).ToArray();

    private static void Validate(SkillManifest skill, ToolRegistry tools)
    {
        if (!IdPattern.IsMatch(skill.Id)
            || !VersionPattern(skill.Version)
            || skill.InputSchema is null
            || skill.OutputSchema is null
            || skill.Steps.Count == 0
            || skill.Steps.Count > 32
            || skill.MaxSteps is < 1 or > 32
            || skill.TimeoutMs is < 1 or > 300_000
            || skill.TrustClass is not ("builtin" or "signed"))
        {
            throw new ArgumentException("invalid_skill_manifest");
        }

        if (!skill.RequiredScopes.All(ToolScope.Known.Contains))
        {
            throw new ArgumentException("unknown_skill_scope");
        }

        foreach (var step in skill.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Name) || !Enum.IsDefined(step.Kind))
            {
                throw new ArgumentException("invalid_skill_step");
            }

            if (step.Kind == SkillStepKind.CallTool)
            {
                if (string.IsNullOrWhiteSpace(step.ToolId) || !skill.RequiredToolIds.Contains(step.ToolId))
                {
                    throw new ArgumentException("undeclared_skill_tool");
                }

                _ = tools.List().FirstOrDefault(tool => tool.Id == step.ToolId)
                    ?? throw new ArgumentException("skill_tool_not_registered");
            }
        }
    }

    private static bool VersionPattern(string value) =>
        Regex.IsMatch(value, "^(0|[1-9]\\d*)\\.(0|[1-9]\\d*)\\.(0|[1-9]\\d*)$");
}
