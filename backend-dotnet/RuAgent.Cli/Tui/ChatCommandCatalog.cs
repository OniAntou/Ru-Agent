namespace RuAgent.Cli.Tui;

/// <summary>Shared command metadata and completion for the chat surfaces.</summary>
public static class ChatCommandCatalog
{
    public sealed record SlashCommandInfo(string Command, string Description, string Syntax = "");

    public static readonly SlashCommandInfo[] KnownCommands =
    [
        new("/model", "Switch or show the active model", "/model [name]"),
        new("/provider", "Choose a provider, then enter its API key", "/provider [name] | /provider remove <name>"),
        new("/reasoning", "Show or set the active model's reasoning effort", "/reasoning [effort]"),
        new("/status", "Show session status and preset", "/status"),
        new("/preset", "Choose permissions for new turns", "/preset [plan|standard|full_access]"),
        new("/new", "Start a new session", "/new"),
        new("/sessions", "Browse sessions or reopen by id", "/sessions [query|id:<session-id>]"),
        new("/subagents", "View child tasks and results", "/subagents [run-id] [agent result [version]]"),
        new("/tasks", "View and stop background terminal tasks", "/tasks [run-id] | /tasks kill <execution-id>"),
        new("/changes", "Summarize recorded file edits and previews", "/changes [run-id]"),
        new("/history", "Browse turns; edit or continue in a new chat branch", "/history [turn [view|edit|continue]] | /history cancel"),
        new("/follow", "Follow a retained run", "/follow [run-id]"),
        new("/help", "Show every command", "/help"),
        new("/update", "Check or install a Ru update", "/update [--yes] [--channel stable|preview]"),
        new("/exit", "Quit", "/exit"),
    ];

    private static readonly HashSet<string> RemovedCommands = new(StringComparer.OrdinalIgnoreCase)
        { "/delete", "/resume", "/retry", "/release", "/clear" };

    internal static bool IsRemovedCommand(string line) => RemovedCommands.Contains(
        line.TrimStart().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "");

    private static readonly HashSet<string> ReportCommands =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "/status", "/help", "/subagents",
        };

    /// <summary>True when <paramref name="slashCmd"/> is a report-only command.</summary>
    public static bool IsReportCommand(string? slashCmd) =>
        slashCmd is not null && ReportCommands.Contains(slashCmd.Trim());

    public static IReadOnlyList<SlashCommandInfo> FindMatches(string input)
    {
        if (string.IsNullOrEmpty(input) || !input.StartsWith('/'))
        {
            return Array.Empty<SlashCommandInfo>();
        }

        var prefix = input.Trim();
        return KnownCommands
            .Where(c => c.Command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static string? AutoComplete(string input, IEnumerable<string> candidates)
    {
        if (string.IsNullOrEmpty(input) || !input.StartsWith('/'))
        {
            return null;
        }

        var matches = candidates
            .Where(c => c.StartsWith(input, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 1)
        {
            return matches[0];
        }

        return null;
    }

    public static string SanitizePastedText(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || maxChars <= 0)
        {
            return string.Empty;
        }

        var line = text.Split(['\r', '\n'], 2, StringSplitOptions.None)[0].Trim();
        return line.Length <= maxChars ? line : line[..maxChars];
    }

}
