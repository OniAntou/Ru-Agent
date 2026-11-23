using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuAgent.Protocol;

public enum AgentCommandKind
{
    Compact,
    Help,
    Skills,
    Skill,
    Tasks,
}

public sealed record AgentCommand(
    AgentCommandKind Kind,
    string? Strategy = null,
    string? SkillId = null,
    JsonObject? JsonInput = null,
    string? TaskKillId = null,
    string? SkillArguments = null,
    string? TaskReadId = null,
    int? TaskReadOffset = null,
    int? TaskReadLimit = null);

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
    public const int MaxSkillIdLength = 96;
    // Mirrors TerminalLimits.MaxExecutionIdChars in the Protocol layer, which
    // the Broker's task registry enforces.
    public const int MaxExecutionIdLength = 256;

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
            "tasks" => ParseTasks(remainder),
            _ => new AgentCommandParseResult(null, "command_unknown", "Unknown Agent command."),
        };
    }

    private static AgentCommandParseResult ParseTasks(string remainder)
    {
        if (remainder.Length == 0)
        {
            return new AgentCommandParseResult(
                new AgentCommand(AgentCommandKind.Tasks),
                null,
                "Session tasks requested.");
        }

        const string killPrefix = "kill ";
        if (remainder.StartsWith(killPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var executionId = remainder[killPrefix.Length..].Trim();
            if (executionId.Length == 0
                || executionId.Length > MaxExecutionIdLength
                || executionId.Contains('\0')
                || executionId.Any(char.IsWhiteSpace))
            {
                return Invalid("Task kill requires one execution id.");
            }

            return new AgentCommandParseResult(
                new AgentCommand(AgentCommandKind.Tasks, TaskKillId: executionId),
                null,
                "Task stop requested.");
        }

        const string readPrefix = "read ";
        if (remainder.StartsWith(readPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ParseTaskRead(remainder[readPrefix.Length..].Trim());
        }

        return Invalid("Tasks accepts no arguments, \"kill <execution_id>\" or \"read <execution_id> [offset] [limit]\".");
    }

    /// <summary>
    /// Parses the output-read form. An omitted offset asks for the tail page,
    /// which is what a reader wants when a task failed; pagination stays inside
    /// the same bounds as <c>terminal_read</c>.
    /// </summary>
    private static AgentCommandParseResult ParseTaskRead(string remainder)
    {
        var parts = remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 3)
        {
            return Invalid("Task read accepts an execution id, then an optional offset and limit.");
        }

        var executionId = parts[0];
        if (executionId.Length == 0
            || executionId.Length > MaxExecutionIdLength
            || executionId.Contains('\0')
            || executionId.Any(char.IsWhiteSpace))
        {
            return Invalid("Task read requires one execution id.");
        }

        int? offset = null;
        if (parts.Length > 1)
        {
            if (!int.TryParse(parts[1], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsedOffset)
                || parsedOffset < 0 || parsedOffset > TerminalLimits.MaxSpoolFileChars)
            {
                return Invalid("Task read offset must be a whole number inside the retained output.");
            }

            offset = parsedOffset;
        }

        int? limit = null;
        if (parts.Length > 2)
        {
            if (!int.TryParse(parts[2], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsedLimit)
                || parsedLimit < 1 || parsedLimit > TerminalLimits.MaxSpoolPageChars)
            {
                return Invalid("Task read limit must be from 1 to " + TerminalLimits.MaxSpoolPageChars + ".");
            }

            limit = parsedLimit;
        }

        return new AgentCommandParseResult(
            new AgentCommand(AgentCommandKind.Tasks, TaskReadId: executionId, TaskReadOffset: offset, TaskReadLimit: limit),
            null,
            "Task output requested.");
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
        var skillId = separator < 0 ? remainder : remainder[..separator];
        var arguments = separator < 0 ? string.Empty : remainder[(separator + 1)..].Trim();
        if (!SkillIdentity.IsValid(skillId) || Encoding.UTF8.GetByteCount(arguments) > MaxSkillJsonBytes)
        {
            return Invalid("Skill requires an ID, e.g. /skill verify [arguments].");
        }

        return new AgentCommandParseResult(
            new AgentCommand(AgentCommandKind.Skill, SkillId: skillId, JsonInput: new JsonObject(), SkillArguments: arguments),
            null,
            "Skill invocation requested.");
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
