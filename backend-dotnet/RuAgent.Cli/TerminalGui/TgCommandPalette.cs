using RuAgent.Cli.Tui;

namespace RuAgent.Cli.TerminalGui;

/// <summary>Search and selection state projected from the shared slash-command catalog.</summary>
public sealed class TgCommandPalette
{
    public const int MaxQueryCharacters = 80;
    public const int MaxVisibleCommands = 16;
    public const int MaxMatchedCommands = 64;

    private readonly IReadOnlyList<InteractiveLineReader.SlashCommandInfo> _commands;
    private int _selectedIndex;

    public TgCommandPalette(IEnumerable<InteractiveLineReader.SlashCommandInfo>? commands = null) =>
        _commands = commands?.ToArray() ?? InteractiveLineReader.KnownCommands;

    public string Query { get; private set; } = string.Empty;

    public IReadOnlyList<InteractiveLineReader.SlashCommandInfo> Matches { get; private set; } = [];
    public string? StatusMessage { get; private set; }

    public string? SelectedCommand => Matches.Count == 0 ? null : Matches[_selectedIndex].Command;
    public int SelectedIndex => _selectedIndex;

    public void SetQuery(string query)
    {
        Query = BoundQuery(query);
        StatusMessage = null;
        Matches = _commands
            .Where(command => MatchesQuery(command, Query))
            .Take(MaxMatchedCommands)
            .ToArray();
        _selectedIndex = 0;
    }

    public void Move(int delta)
    {
        if (Matches.Count == 0 || delta == 0)
        {
            return;
        }

        _selectedIndex = ((_selectedIndex + delta) % Matches.Count + Matches.Count) % Matches.Count;
    }

    public void Reset() => SetQuery(string.Empty);

    public void SetStatusMessage(string? message) => StatusMessage = message;

    private static bool MatchesQuery(InteractiveLineReader.SlashCommandInfo command, string query) =>
        query.Length == 0
        || command.Command.Contains(query, StringComparison.OrdinalIgnoreCase)
        || command.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
        || command.Syntax.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string BoundQuery(string query)
    {
        var builder = new System.Text.StringBuilder(MaxQueryCharacters);
        foreach (var rune in query.EnumerateRunes())
        {
            if (builder.Length + rune.Utf16SequenceLength > MaxQueryCharacters)
            {
                break;
            }

            builder.Append(rune);
        }

        return builder.ToString();
    }
}
