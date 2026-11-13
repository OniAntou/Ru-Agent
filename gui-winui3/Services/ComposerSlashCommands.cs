using System.Text.RegularExpressions;
using RuAgent.Client;

namespace RuApp.Services;

/// <summary>One row of the composer's slash menu.</summary>
/// <param name="IsBuiltIn">Handled by the app itself rather than expanded from a template.</param>
public sealed record ComposerSlashSuggestion(string Name, string Description, bool IsBuiltIn)
{
    public string Command => "/" + Name;
    public string AutomationLabel => Description.Length == 0 ? Command : $"{Command}, {Description}";
}

/// <summary>
/// Custom slash commands in the desktop composer: the same <c>.ru/commands/*.md</c>
/// templates the terminal chat reads, workspace entries shadowing user ones. A template
/// expands to an ordinary prompt before the turn is sent, so it grants no tool, path or
/// approval authority, and the transcript shows exactly what the Agent received.
/// </summary>
public static partial class ComposerSlashCommands
{
    public const int MaxSuggestions = 8;

    /// <summary>Names the app or Host already handles; a template can never replace them.</summary>
    private static readonly string[] Reserved = ["skill", "skills"];

    private static readonly ComposerSlashSuggestion[] BuiltIns =
    [
        new("skills", "Open Customize to browse and enable skills", true),
    ];

    // A draft that is still only the command token; the menu closes at the first space.
    [GeneratedRegex("^/[a-z0-9_-]{0,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    /// <summary>Whether the draft is a slash token being typed, so the menu has something to match.</summary>
    public static bool IsTypingCommand(string? draft) => draft is not null && TokenPattern().IsMatch(draft);

    /// <summary>
    /// Commands whose name starts with what was typed, custom templates first. Reads the
    /// command folders, so call it off the UI thread.
    /// </summary>
    public static IReadOnlyList<ComposerSlashSuggestion> Suggest(string? draft, string? workspace, string? userHome)
    {
        if (!IsTypingCommand(draft)) return [];
        var prefix = draft![1..];
        var matches = new List<ComposerSlashSuggestion>();
        foreach (var name in CustomPromptCommands.List(workspace ?? string.Empty, userHome))
        {
            if (matches.Count >= MaxSuggestions) break;
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || Reserved.Contains(name, StringComparer.Ordinal)) continue;
            if (CustomPromptCommands.Find("/" + name, workspace ?? string.Empty, userHome) is { } command)
                matches.Add(new(name, OneLine(command.Description), false));
        }

        foreach (var builtIn in BuiltIns)
        {
            if (matches.Count < MaxSuggestions && builtIn.Name.StartsWith(prefix, StringComparison.Ordinal)) matches.Add(builtIn);
        }

        return matches;
    }

    /// <summary>
    /// The prompt a custom command stands for, or null when the text is not one: it does
    /// not start with a usable template name, or the name is reserved.
    /// </summary>
    public static string? Expand(string? text, string? workspace, string? userHome)
    {
        var trimmed = text?.TrimStart();
        if (string.IsNullOrEmpty(trimmed) || trimmed[0] != '/') return null;
        var end = trimmed.IndexOfAny([' ', '\t', '\r', '\n']);
        var token = end < 0 ? trimmed : trimmed[..end];
        if (Reserved.Contains(token[1..], StringComparer.Ordinal)) return null;
        if (CustomPromptCommands.Find(token, workspace ?? string.Empty, userHome) is not { } command) return null;
        var expanded = CustomPromptCommands.Expand(command, end < 0 ? null : trimmed[end..]);
        return string.IsNullOrWhiteSpace(expanded) ? null : expanded;
    }

    /// <summary>A template's description is user text: keep it to one bounded line in the menu.</summary>
    private static string OneLine(string description)
    {
        var line = string.Join(' ', description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= 120 ? line : line[..119] + "…";
    }
}
