using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuAgent.Broker.Skills;

public sealed class SkillPackageLoader
{
    public const int DefaultMaxFiles = 8;
    public const int DefaultMaxTotalBytes = 192 * 1024;
    public const int DefaultMaxManifestBytes = 64 * 1024;
    public const int DefaultMaxInstructionsBytes = 128 * 1024;

    private static readonly IReadOnlySet<string> ExecutableExtensions = new HashSet<string>(
        [".exe", ".dll", ".bat", ".cmd", ".com", ".ps1", ".vbs", ".js", ".msi"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> ManifestFields = new HashSet<string>(
        [
            "id", "version", "input_schema", "output_schema", "required_tools",
            "required_scopes", "steps", "requires_approval", "max_steps",
            "timeout_ms", "trust_class",
        ],
        StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> StepFields = new HashSet<string>(
        ["kind", "name", "tool_id", "tool_version", "arguments", "branch_on", "true_index", "false_index", "transform"],
        StringComparer.Ordinal);

    private readonly int _maxFiles;
    private readonly int _maxTotalBytes;
    private readonly int _maxManifestBytes;
    private readonly int _maxInstructionsBytes;

    public SkillPackageLoader(
        int maxFiles = DefaultMaxFiles,
        int maxTotalBytes = DefaultMaxTotalBytes,
        int maxManifestBytes = DefaultMaxManifestBytes,
        int maxInstructionsBytes = DefaultMaxInstructionsBytes)
    {
        if (maxFiles is < 2 or > 64
            || maxTotalBytes is < 1 or > 4 * 1024 * 1024
            || maxManifestBytes is < 1 or > 1024 * 1024
            || maxInstructionsBytes is < 1 or > 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFiles));
        }

        _maxFiles = maxFiles;
        _maxTotalBytes = maxTotalBytes;
        _maxManifestBytes = maxManifestBytes;
        _maxInstructionsBytes = maxInstructionsBytes;
    }

    public SkillManifest Load(string packageDirectory)
    {
        var root = Path.GetFullPath(packageDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(root);
        }

        // Packages are intentionally flat. Do not recurse into untrusted directories or
        // junctions while looking for content; a nested directory is rejected as a whole.
        var entries = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly).ToArray();
        if (entries.Length > _maxFiles)
        {
            throw new InvalidDataException("skill_package_too_many_files");
        }

        foreach (var entry in entries)
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("skill_package_reparse_point");
            }

            if (Directory.Exists(entry))
            {
                throw new InvalidDataException("skill_package_unexpected_content");
            }

            if (ExecutableExtensions.Contains(Path.GetExtension(entry)))
            {
                throw new InvalidDataException("executable_skill_content");
            }

            var relative = Path.GetRelativePath(root, entry);
            if (!string.Equals(relative, "skill.json", StringComparison.Ordinal)
                && !string.Equals(relative, "instructions.md", StringComparison.Ordinal))
            {
                throw new InvalidDataException("skill_package_unexpected_content");
            }
        }

        var manifestPath = Path.Combine(root, "skill.json");
        var instructionsPath = Path.Combine(root, "instructions.md");
        if (!File.Exists(manifestPath) || !File.Exists(instructionsPath))
        {
            throw new InvalidDataException("skill_package_incomplete");
        }

        var manifestLength = new FileInfo(manifestPath).Length;
        var instructionsLength = new FileInfo(instructionsPath).Length;
        if (manifestLength > _maxManifestBytes || instructionsLength > _maxInstructionsBytes)
        {
            throw new InvalidDataException("skill_package_too_large");
        }

        if (manifestLength + instructionsLength > _maxTotalBytes)
        {
            throw new InvalidDataException("skill_package_too_large");
        }

        var manifestText = ReadUtf8(manifestPath, manifestLength);
        var instructions = ReadUtf8(instructionsPath, instructionsLength);
        JsonObject json;
        try
        {
            json = JsonNode.Parse(manifestText) as JsonObject
                ?? throw new InvalidDataException("invalid_skill_json");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("invalid_skill_json", error);
        }
        RejectUnknownFields(json, ManifestFields, "unknown_skill_field");
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
        var requiresApproval = ReadOptionalBool(json, "requires_approval");
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
            instructions);
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

            RejectUnknownFields(step, StepFields, "unknown_skill_step_field");

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
                OptionalString(step, "branch_on"),
                ReadOptionalInt(step, "true_index"),
                ReadOptionalInt(step, "false_index"),
                OptionalString(step, "transform"),
                OptionalString(step, "tool_version")));
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

    private static int? ReadOptionalInt(JsonObject json, string name)
    {
        if (!json.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        return node is JsonValue value && value.TryGetValue<int>(out var parsed)
            ? parsed
            : throw new InvalidDataException($"invalid_{name}");
    }

    private static bool ReadOptionalBool(JsonObject json, string name)
    {
        if (!json.TryGetPropertyValue(name, out var node) || node is null)
        {
            return false;
        }

        return node is JsonValue value && value.TryGetValue<bool>(out var parsed)
            ? parsed
            : throw new InvalidDataException($"invalid_{name}");
    }

    private static void RejectUnknownFields(JsonObject json, IReadOnlySet<string> allowed, string code)
    {
        if (json.Any(pair => !allowed.Contains(pair.Key)))
        {
            throw new InvalidDataException(code);
        }
    }

    private static string ReadUtf8(string path, long length)
    {
        if (length > int.MaxValue)
        {
            throw new InvalidDataException("skill_package_too_large");
        }

        try
        {
            return new System.Text.UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("invalid_skill_utf8");
        }
    }

    private static string RequiredString(JsonObject json, string name) =>
        OptionalString(json, name) ?? throw new InvalidDataException($"missing_{name}");

    private static string? OptionalString(JsonObject json, string name) =>
        json[name] is JsonValue value && value.TryGetValue<string>(out var parsed) && !string.IsNullOrWhiteSpace(parsed)
            ? parsed
            : null;
}
