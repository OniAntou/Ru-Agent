using System.Text.RegularExpressions;
using RuAgent.Client;

namespace RuApp.Services;

/// <summary>Where a slash command comes from; also the menu's group order.</summary>
public enum ComposerSlashSource
{
    /// <summary>The workspace's <c>.ru/commands</c>; it shadows a personal template of the same name.</summary>
    Workspace,

    /// <summary><c>%USERPROFILE%\.ru\commands</c>.</summary>
    Personal,

    /// <summary>Handled by the app itself rather than expanded from a template.</summary>
    BuiltIn,
}

/// <summary>One row of the composer's slash menu.</summary>
/// <param name="MatchedLength">How many leading characters of <paramref name="Name"/> the typed draft matched.</param>
/// <param name="TakesArguments">The template places arguments with <c>$ARGUMENTS</c>.</param>
public sealed record ComposerSlashSuggestion(
    string Name,
    string Description,
    ComposerSlashSource Source,
    bool TakesArguments = false,
    int MatchedLength = 0)
{
    public bool IsBuiltIn => Source == ComposerSlashSource.BuiltIn;
    public string Command => "/" + Name;
    public string AutomationLabel => Description.Length == 0 ? Command : $"{Command}, {Description}";

    /// <summary>The typed part of the command, drawn stronger so the reason for the match shows.</summary>
    public string MatchedText => "/" + Name[..Math.Clamp(MatchedLength, 0, Name.Length)];

    /// <summary>The rest of the name, not typed yet.</summary>
    public string RemainingText => Name[Math.Clamp(MatchedLength, 0, Name.Length)..];

    /// <summary>Segoe Fluent glyph: a document for a template, a puzzle piece for an app command.</summary>
    public string Glyph => IsBuiltIn ? "\uEA86" : "\uE8A5";

    public string GroupLabel => ComposerSlashCommands.GroupLabel(Source);
}

/// <summary>One labelled section of the slash menu, for a grouped list view.</summary>
public sealed class ComposerSlashGroup(string label, IEnumerable<ComposerSlashSuggestion> rows) : List<ComposerSlashSuggestion>(rows)
{
    public string Label { get; } = label;
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
        new("skills", "Open Customize to browse and enable skills", ComposerSlashSource.BuiltIn),
    ];

    // A draft that is still only the command token; the menu closes at the first space.
    [GeneratedRegex("^/[a-z0-9_-]{0,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    /// <summary>Whether the draft is a slash token being typed, so the menu has something to match.</summary>
    public static bool IsTypingCommand(string? draft) => draft is not null && TokenPattern().IsMatch(draft);

    /// <summary>
    /// Commands whose name starts with what was typed, in menu order: workspace templates,
    /// then personal ones, then the app's own commands, each group alphabetical. Reads the
    /// command folders, so call it off the UI thread.
    /// </summary>
    public static IReadOnlyList<ComposerSlashSuggestion> Suggest(string? draft, string? workspace, string? userHome)
    {
        if (!IsTypingCommand(draft)) return [];
        var prefix = draft![1..];
        var root = workspace ?? string.Empty;
        var templates = new List<ComposerSlashSuggestion>();
        foreach (var name in CustomPromptCommands.List(root, userHome))
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || Reserved.Contains(name, StringComparer.Ordinal)) continue;
            if (CustomPromptCommands.Find("/" + name, root, userHome) is not { } command) continue;
            // Find reads the workspace first, so a usable workspace file is the one that answers.
            var source = CustomPromptCommands.Find("/" + name, root, userHome: null) is not null
                ? ComposerSlashSource.Workspace
                : ComposerSlashSource.Personal;
            var takesArguments = command.Template.Contains(CustomPromptCommands.ArgumentsPlaceholder, StringComparison.Ordinal);
            templates.Add(new(name, OneLine(command.Description), source, takesArguments, prefix.Length));
        }

        var matches = templates
            .OrderBy(row => row.Source)
            .ThenBy(row => row.Name, StringComparer.Ordinal)
            .Take(MaxSuggestions)
            .ToList();
        foreach (var builtIn in BuiltIns)
        {
            if (matches.Count < MaxSuggestions && builtIn.Name.StartsWith(prefix, StringComparison.Ordinal))
                matches.Add(builtIn with { MatchedLength = prefix.Length });
        }

        return matches;
    }

    /// <summary>
    /// The menu's sections, in <see cref="Suggest"/> order. A single source needs no
    /// labels, so callers show the flat list when this returns one group.
    /// </summary>
    public static IReadOnlyList<ComposerSlashGroup> Group(IReadOnlyList<ComposerSlashSuggestion> rows) =>
        rows.GroupBy(row => row.Source)
            .Select(group => new ComposerSlashGroup(GroupLabel(group.Key), group))
            .ToArray();

    public static string GroupLabel(ComposerSlashSource source) => source switch
    {
        ComposerSlashSource.Workspace => "Workspace",
        ComposerSlashSource.Personal => "Personal",
        _ => "Ru",
    };

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
