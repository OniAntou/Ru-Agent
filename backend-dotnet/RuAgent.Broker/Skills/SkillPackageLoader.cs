using System.Text.Json.Nodes;

namespace RuAgent.Broker.Skills;

public sealed class SkillPackageLoader
{
    private static readonly IReadOnlySet<string> ExecutableExtensions = new HashSet<string>(
        [".exe", ".dll", ".bat", ".cmd", ".com", ".ps1", ".vbs", ".js", ".msi"],
        StringComparer.OrdinalIgnoreCase);

    public SkillManifest Load(string packageDirectory)
    {
        var root = Path.GetFullPath(packageDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(root);
        }

        if (Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Any(path => ExecutableExtensions.Contains(Path.GetExtension(path))))
        {
            throw new InvalidDataException("executable_skill_content");
        }

        var manifestPath = Path.Combine(root, "skill.json");
        var instructionsPath = Path.Combine(root, "instructions.md");
        if (!File.Exists(manifestPath) || !File.Exists(instructionsPath))
        {
            throw new InvalidDataException("skill_package_incomplete");
        }

        if (new FileInfo(manifestPath).Length > 64 * 1024
            || new FileInfo(instructionsPath).Length > 128 * 1024)
        {
            throw new InvalidDataException("skill_package_too_large");
        }

        var json = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject
            ?? throw new InvalidDataException("invalid_skill_json");
        var id = RequiredString(json, "id");
        var version = RequiredString(json, "version");
        var inputSchema = json["input_schema"] as JsonObject
            ?? throw new InvalidDataException("missing_input_schema");
        var outputSchema = json["output_schema"] as JsonObject
            ?? throw new InvalidDataException("missing_output_schema");
        var tools = ReadStringSet(json["required_tools"], "required_tools");
        var scopes = ReadStringSet(json["required_scopes"], "required_scopes");
        var steps = ReadSteps(json["steps"]);
        var maxSteps = ReadInt(json, "max_steps");
        var timeoutMs = ReadInt(json, "timeout_ms");
        var requiresApproval = json["requires_approval"]?.GetValue<bool>() ?? false;
        var trustClass = RequiredString(json, "trust_class");
        return new SkillManifest(
            id,
            version,
            inputSchema.DeepClone().AsObject(),
            outputSchema.DeepClone().AsObject(),
            tools,
            scopes,
            steps,
            requiresApproval,
            maxSteps,
            timeoutMs,
            trustClass,
            File.ReadAllText(instructionsPath));
    }

    private static IReadOnlyList<SkillStep> ReadSteps(JsonNode? node)
    {
        if (node is not JsonArray values)
        {
            throw new InvalidDataException("missing_steps");
        }

        var result = new List<SkillStep>();
        foreach (var item in values)
        {
            if (item is not JsonObject step)
            {
                throw new InvalidDataException("invalid_skill_step");
            }

            var kind = RequiredString(step, "kind") switch
            {
                "call_tool" => SkillStepKind.CallTool,
                "validate_result" => SkillStepKind.ValidateResult,
                "branch" => SkillStepKind.Branch,
                "named_transform" => SkillStepKind.NamedTransform,
                _ => throw new InvalidDataException("invalid_skill_step"),
            };
            result.Add(new SkillStep(
                kind,
                RequiredString(step, "name"),
                OptionalString(step, "tool_id"),
                step["arguments"] is JsonObject args ? args.DeepClone().AsObject() : null,
                OptionalString(step, "branch_on")));
        }

        return result;
    }

    private static HashSet<string> ReadStringSet(JsonNode? node, string name)
    {
        if (node is not JsonArray values)
        {
            throw new InvalidDataException($"missing_{name}");
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value is not JsonValue item
                || !item.TryGetValue<string>(out var text)
                || string.IsNullOrWhiteSpace(text)
                || !result.Add(text))
            {
                throw new InvalidDataException($"invalid_{name}");
            }
        }

        return result;
    }

    private static int ReadInt(JsonObject json, string name) =>
        json[name] is JsonValue value && value.TryGetValue<int>(out var parsed)
            ? parsed
            : throw new InvalidDataException($"missing_{name}");

    private static string RequiredString(JsonObject json, string name) =>
        OptionalString(json, name) ?? throw new InvalidDataException($"missing_{name}");

    private static string? OptionalString(JsonObject json, string name) =>
        json[name] is JsonValue value && value.TryGetValue<string>(out var parsed) && !string.IsNullOrWhiteSpace(parsed)
            ? parsed
            : null;
}
