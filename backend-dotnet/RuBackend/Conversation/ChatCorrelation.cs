using System.Text.RegularExpressions;

namespace RuBackend.Conversation;

public static partial class ChatCorrelation
{
    private const int MinimumLength = 8;
    private const int MaximumLength = 64;

    public static string Generate() => $"c_{Guid.NewGuid():N}";

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();
        return candidate.Length is >= MinimumLength and <= MaximumLength
            && SafeIdentifierRegex().IsMatch(candidate);
    }

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var candidate = value.Trim();
        return IsValid(candidate) ? candidate : null;
    }

    [GeneratedRegex("^[a-zA-Z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifierRegex();
}
