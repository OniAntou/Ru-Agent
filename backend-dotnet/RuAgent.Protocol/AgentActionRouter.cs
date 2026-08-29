using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RuAgent.Protocol;

public enum AgentCommandKind
{
    Compact,
    Help,
    Skills,
    Skill,
}

public sealed record AgentCommand(
    AgentCommandKind Kind,
    string? Strategy = null,
    string? SkillId = null,
    JsonObject? JsonInput = null);

public sealed record AgentCommandParseResult(
    AgentCommand? Command,
    string? Code,
    string SafeMessage)
{
    public bool Success => Command is not null;
    public bool IsCommand => Command is not null || Code is not null;
    public string? ErrorCode => Code;
}

/// <summary>
/// Parses the small, shared command grammar used by GUI and CLI adapters. It
/// never executes a tool and never returns the original command text.
/// </summary>
public static class AgentActionRouter
{
    public const int MaxInputBytes = 16 * 1024;
    public const int MaxSkillJsonBytes = 8 * 1024;
    public const int MaxSkillIdLength = 128;

    private static readonly Regex SkillIdPattern = new(
        "^[a-z][a-z0-9]*(?:\\.[a-z0-9][a-z0-9_-]*)+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static AgentCommandParseResult Parse(string? text)
    {
        if (text is null)
        {
            return NotCommand;
        }

        if (Encoding.UTF8.GetByteCount(text) > MaxInputBytes)
        {
            return Invalid("Command is too large.");
        }

        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed[0] != '/')
        {
            return NotCommand;
        }

        var separator = IndexOfWhitespace(trimmed);
        var name = (separator < 0 ? trimmed[1..] : trimmed[1..separator]).ToLowerInvariant();
        var remainder = separator < 0 ? string.Empty : trimmed[(separator + 1)..].Trim();
        return name switch
        {
            "compact" => ParseCompact(remainder),
            "help" => NoArguments(AgentCommandKind.Help, remainder, "Help lists the available Agent commands."),
            "skills" => NoArguments(AgentCommandKind.Skills, remainder, "Skills lists the skills granted to this session."),
            "skill" => ParseSkill(remainder),
            _ => new AgentCommandParseResult(null, "command_unknown", "Unknown Agent command."),
        };
    }

    private static AgentCommandParseResult ParseCompact(string remainder)
    {
        if (remainder is not ("normal" or "aggressive"))
        {
            return Invalid("Compact requires the normal or aggressive strategy.");
        }

        return new AgentCommandParseResult(
            new AgentCommand(AgentCommandKind.Compact, Strategy: remainder),
            null,
            "Context compaction requested.");
    }

    private static AgentCommandParseResult ParseSkill(string remainder)
    {
        var separator = IndexOfWhitespace(remainder);
        if (separator <= 0)
        {
            return Invalid("Skill requires an id and a JSON object.");
        }

        var skillId = remainder[..separator];
        var jsonText = remainder[(separator + 1)..].Trim();
        if (skillId.Length > MaxSkillIdLength || !SkillIdPattern.IsMatch(skillId))
        {
            return Invalid("Skill id is invalid.");
        }

        if (jsonText.Length == 0 || Encoding.UTF8.GetByteCount(jsonText) > MaxSkillJsonBytes)
        {
            return Invalid("Skill input must be a bounded JSON object.");
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(jsonText);
        }
        catch (JsonException)
        {
            return Invalid("Skill input is invalid JSON.");
        }

        if (node is not JsonObject input)
        {
            return Invalid("Skill input must be a JSON object.");
        }

        return new AgentCommandParseResult(
            new AgentCommand(AgentCommandKind.Skill, SkillId: skillId, JsonInput: input.DeepClone().AsObject()),
            null,
            "Skill execution requested.");
    }

    private static AgentCommandParseResult NoArguments(
        AgentCommandKind kind,
        string remainder,
        string safeMessage) =>
        remainder.Length == 0
            ? new AgentCommandParseResult(new AgentCommand(kind), null, safeMessage)
            : Invalid("This command does not accept arguments.");

    private static int IndexOfWhitespace(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsWhiteSpace(value[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static AgentCommandParseResult Invalid(string safeMessage) =>
        new(null, "command_invalid", safeMessage);

    private static AgentCommandParseResult NotCommand { get; } =
        new(null, null, string.Empty);
}
