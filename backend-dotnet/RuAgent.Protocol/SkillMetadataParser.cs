using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace RuAgent.Protocol;

public sealed record SkillMetadata(
    string Id,
    string Name,
    string Description,
    string Body,
    IReadOnlyList<string> Requires,
    string? Compatibility,
    bool ModelInvocable,
    bool UserInvocable,
    IReadOnlyList<string> Diagnostics);

/// <summary>Bounded parser for the supported SKILL.md frontmatter schema.</summary>
public static class SkillMetadataParser
{
    public const int MaxFrontmatterBytes = 16 * 1024;
    public const int MaxIdLength = 96;
    private const int MaxListItems = 8;

    public static bool IsValidId(string? id) => id is { Length: > 0 and <= MaxIdLength }
        && (id[0] is >= 'a' and <= 'z' or >= '0' and <= '9')
        && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');

    public static SkillMetadata Parse(string content, string folderId)
    {
        ArgumentNullException.ThrowIfNull(content);
        content = content.TrimStart('\uFEFF').Normalize(NormalizationForm.FormC);
        var id = folderId.Trim().ToLowerInvariant();
        if (!IsValidId(id)) throw new ArgumentException("invalid_skill_id", nameof(folderId));
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (lines.Length < 3 || lines[0].Trim().TrimStart('\uFEFF') != "---")
            return Defaults(id, id, content, Array.Empty<string>());

        var closing = Array.FindIndex(lines, 1, line => line.Trim() == "---");
        if (closing < 0) return Defaults(id, id, content, ["frontmatter_unclosed"]);
        var yaml = string.Join('\n', lines[1..closing]);
        if (Encoding.UTF8.GetByteCount(yaml) > MaxFrontmatterBytes)
            return Defaults(id, id, string.Join('\n', lines[(closing + 1)..]).TrimStart('\n'), ["frontmatter_oversize"]);

        var diagnostics = new List<string>();
        var name = id;
        string? description = null;
        string? compatibility = null;
        IReadOnlyList<string> requires = [];
        var modelInvocable = true;
        var userInvocable = true;
        try
        {
            ValidateYaml(yaml);
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode map)
                throw new YamlException("frontmatter must be one mapping");
            var supported = new HashSet<string>(["name", "description", "requires", "requirements", "dependencies", "compatibility", "disable-model-invocation", "user-invocable", "allowed-tools", "license", "metadata"], StringComparer.OrdinalIgnoreCase);
            foreach (var pair in map.Children)
            {
                var key = (pair.Key as YamlScalarNode)?.Value;
                if (string.IsNullOrWhiteSpace(key)) { diagnostics.Add("invalid_field_name"); continue; }
                var value = pair.Value;
                if (!supported.Contains(key)) { diagnostics.Add("unsupported_field:" + new string(key.Take(64).Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '?').ToArray())); continue; }
                switch (key.ToLowerInvariant())
                {
                    case "name": name = Scalar(value, key); break;
                    case "description": description = Scalar(value, key); break;
                    case "compatibility": compatibility = Scalar(value, key); break;
                    case "requires":
                    case "requirements":
                    case "dependencies": requires = ReadList(value, key); break;
                    case "disable-model-invocation": modelInvocable = !ReadBoolean(value, key); break;
                    case "user-invocable": userInvocable = ReadBoolean(value, key); break;
                    case "metadata": if (value is not YamlMappingNode) diagnostics.Add("invalid_field:metadata"); break;
                }
            }
        }
        catch (YamlException) { diagnostics.Add("invalid_frontmatter"); modelInvocable = false; }

        if (string.IsNullOrWhiteSpace(name)) name = id;
        if (string.IsNullOrWhiteSpace(description)) description = DeriveDescription(string.Join('\n', lines[(closing + 1)..]));
        if (string.IsNullOrWhiteSpace(compatibility)) compatibility = null;
        return new SkillMetadata(id, Limit(name, 256), Limit(description, 2048),
            string.Join('\n', lines[(closing + 1)..]).TrimStart('\n').TrimEnd(), requires,
            compatibility is null ? null : Limit(compatibility, 1024), modelInvocable, userInvocable, diagnostics);
    }

    private static SkillMetadata Defaults(string id, string name, string body, IReadOnlyList<string> diagnostics) =>
        new(id, name, DeriveDescription(body), body, [], null, true, true, diagnostics);

    private static string Scalar(YamlNode node, string key) => node is YamlScalarNode scalar && scalar.Value is not null
        ? Limit(scalar.Value.Trim(), key == "description" ? 2048 : 1024)
        : throw new YamlException($"{key} must be scalar");

    private static IReadOnlyList<string> ReadList(YamlNode node, string key)
    {
        IEnumerable<string> values;
        if (node is YamlSequenceNode sequence)
        {
            if (sequence.Children.Any(item => item is not YamlScalarNode)) throw new YamlException($"{key} items must be scalar");
            values = sequence.Children.Cast<YamlScalarNode>().Select(item => item.Value ?? string.Empty);
        }
        else if (node is YamlScalarNode scalar) values = [scalar.Value ?? string.Empty];
        else throw new YamlException($"{key} must be scalar or list");
        return values.SelectMany(value => SkillRequirements.Parse(value))
            .Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxListItems).ToArray();
    }

    private static bool ReadBoolean(YamlNode node, string key)
    {
        if (node is YamlScalarNode scalar && bool.TryParse(scalar.Value, out var value)) return value;
        throw new YamlException($"{key} must be boolean");
    }

    private static void ValidateYaml(string yaml)
    {
        var parser = new YamlDotNet.Core.Parser(new StringReader(yaml));
        var depth = 0;
        var nodes = 0;
        var documents = 0;
        while (parser.MoveNext())
        {
            switch (parser.Current)
            {
                case DocumentStart:
                    if (++documents > 1) throw new YamlException("multiple documents are not supported");
                    break;
                case AnchorAlias:
                    throw new YamlException("aliases are not supported");
                case MappingStart or SequenceStart:
                    if (++depth > 16 || ++nodes > 512) throw new YamlException("frontmatter complexity limit exceeded");
                    break;
                case MappingEnd or SequenceEnd:
                    depth--;
                    break;
                case YamlDotNet.Core.Events.Scalar:
                    if (++nodes > 512) throw new YamlException("frontmatter complexity limit exceeded");
                    break;
            }
        }
    }

    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max];

    private static string DeriveDescription(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            var value = line.Trim().TrimStart('#').Trim();
            if (value.Length > 0) return Limit(value, 2048);
        }
        return "Workspace workflow guidance.";
    }
}
