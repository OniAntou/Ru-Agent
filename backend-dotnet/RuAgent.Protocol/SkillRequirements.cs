using System.Text;

namespace RuAgent.Protocol;

public sealed record SkillRequirementFailure(string Code, string Message);

/// <summary>
/// The requires: check every surface applies before it returns a Markdown skill body.
/// The Broker's skill tool and the Host's /skill command both read through here, so
/// the recognized tokens and the fail-closed codes cannot drift between them.
/// </summary>
public static class SkillRequirements
{
    private const string PythonToken = "python";
    private const string PythonUnavailableCode = "python_unavailable";
    private const string UnsupportedRequirementCode = "unsupported_skill_requirement";
    private const int MaxRequirements = 8;
    private const int MaxRequirementLength = 64;

    public static IReadOnlyList<string> Parse(string value)
    {
        var requirements = new List<string>();
        foreach (var candidate in value.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (requirements.Count >= MaxRequirements)
            {
                break;
            }

            var token = Unquote(candidate);
            if (token.Length == 0)
            {
                continue;
            }

            var requirement = Sanitize(token.Length <= MaxRequirementLength ? token : token[..MaxRequirementLength]);
            if (!requirements.Contains(requirement, StringComparer.OrdinalIgnoreCase))
            {
                requirements.Add(requirement);
            }
        }

        return requirements;
    }

    public static SkillRequirementFailure? FindUnmet(IReadOnlyList<string> requires, Func<bool> pythonAvailable)
    {
        foreach (var requirement in requires)
        {
            if (string.Equals(requirement, PythonToken, StringComparison.OrdinalIgnoreCase))
            {
                if (!pythonAvailable())
                {
                    return new SkillRequirementFailure(
                        PythonUnavailableCode,
                        "This skill requires Python, which is not installed or not on PATH.");
                }

                continue;
            }

            return new SkillRequirementFailure(
                UnsupportedRequirementCode,
                $"This skill requires unsupported capability '{requirement}'. Supported requirements: python.");
        }

        return null;
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' ? character : '?');
        }

        return builder.ToString();
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1].Trim();
        }

        return value;
    }
}
