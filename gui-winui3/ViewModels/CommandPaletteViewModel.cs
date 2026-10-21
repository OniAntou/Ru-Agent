using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RuApp.ViewModels;

/// <summary>
/// One thing the palette can do. <see cref="Run"/> calls an existing command of the
/// shell or the chat; the palette adds no execution path of its own.
/// </summary>
public sealed class PaletteCommand(
    string id,
    string title,
    string group,
    Action run,
    string? hint = null,
    string? shortcut = null,
    Func<bool>? canRun = null,
    string? glyph = null)
{
    public string Id { get; } = id;

    public string Title { get; } = title;

    public string Group { get; } = group;

    public string? Hint { get; } = hint;

    public string? Shortcut { get; } = shortcut;

    public string Glyph { get; } = glyph ?? "";

    public bool HasHint => !string.IsNullOrWhiteSpace(Hint);

    public bool HasShortcut => !string.IsNullOrWhiteSpace(Shortcut);

    internal Func<bool> CanRun { get; } = canRun ?? (() => true);

    internal Action Run { get; } = run;
}

/// <summary>
/// The command palette: a keyboard-first list of actions and sessions, filtered as the
/// user types. The shell supplies the commands each time it opens, so the list always
/// reflects the sessions and state of that moment.
/// </summary>
/// <remarks>
/// Matching is case-insensitive and forgiving: the query's characters must appear in
/// order. A match at the start of a word outranks one inside it, and a contiguous run
/// outranks scattered letters, so "nc" finds "New chat" before "Open session cache".
/// A command that cannot run now (Stop with no run in hand) is left out rather than
/// shown disabled: the palette lists what Enter would actually do.
/// </remarks>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    /// <summary>Sessions shown before the user types; typing searches all of them.</summary>
    internal const int DefaultSessionCount = 8;

    internal const string SessionGroup = "Sessions";

    private readonly Func<IReadOnlyList<PaletteCommand>> _source;
    private IReadOnlyList<PaletteCommand> _commands = [];

    public CommandPaletteViewModel(Func<IReadOnlyList<PaletteCommand>> source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public ObservableCollection<PaletteCommand> Results { get; } = new();

    [ObservableProperty] public partial bool IsOpen { get; private set; }

    [ObservableProperty] public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoResults))]
    public partial int SelectedIndex { get; private set; } = -1;

    public bool HasNoResults => Results.Count == 0;

    public PaletteCommand? Selected =>
        SelectedIndex >= 0 && SelectedIndex < Results.Count ? Results[SelectedIndex] : null;

    internal void Open()
    {
        _commands = _source();
        Query = string.Empty;
        Refresh();
        IsOpen = true;
    }

    internal void Close() => IsOpen = false;

    partial void OnQueryChanged(string value)
    {
        if (IsOpen) Refresh();
    }

    internal void MoveSelection(int delta)
    {
        if (Results.Count == 0) return;
        int next = (SelectedIndex + delta) % Results.Count;
        SelectedIndex = next < 0 ? next + Results.Count : next;
        OnPropertyChanged(nameof(Selected));
    }

    internal void Select(PaletteCommand command)
    {
        int index = Results.IndexOf(command);
        if (index < 0) return;
        SelectedIndex = index;
        OnPropertyChanged(nameof(Selected));
    }

    /// <summary>Runs the selected command after closing, so the command owns focus afterwards.</summary>
    internal bool ExecuteSelected()
    {
        if (Selected is not { } command || !command.CanRun()) return false;
        Close();
        command.Run();
        return true;
    }

    private void Refresh()
    {
        string query = Query.Trim();
        var runnable = _commands.Where(command => command.CanRun()).ToList();
        IEnumerable<PaletteCommand> ranked;
        if (query.Length == 0)
        {
            var actions = runnable.Where(command => command.Group != SessionGroup);
            var sessions = runnable.Where(command => command.Group == SessionGroup).Take(DefaultSessionCount);
            ranked = actions.Concat(sessions);
        }
        else
        {
            ranked = runnable
                .Select((command, order) => (command, order, score: Score(query, command.Title)
                    ?? (Score(query, command.Hint ?? string.Empty) is { } hintScore ? hintScore / 2 : (int?)null)))
                .Where(entry => entry.score is not null)
                .OrderByDescending(entry => entry.score)
                .ThenBy(entry => entry.order)
                .Select(entry => entry.command);
        }

        Results.Clear();
        foreach (var command in ranked) Results.Add(command);
        SelectedIndex = Results.Count > 0 ? 0 : -1;
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(HasNoResults));
    }

    /// <summary>
    /// Scores <paramref name="text"/> against <paramref name="query"/>, or null when the
    /// query's characters do not all appear in order.
    /// </summary>
    internal static int? Score(string query, string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        string q = query.ToLowerInvariant();
        string t = text.ToLowerInvariant();

        int contiguous = t.IndexOf(q, StringComparison.Ordinal);
        if (contiguous >= 0)
        {
            bool atWordStart = contiguous == 0 || !char.IsLetterOrDigit(t[contiguous - 1]);
            return (atWordStart ? 1000 : 600) - contiguous - (t.Length - q.Length);
        }

        int score = 0;
        int position = 0;
        int previous = -2;
        foreach (char c in q)
        {
            if (char.IsWhiteSpace(c)) continue;
            int found = t.IndexOf(c, position);
            if (found < 0) return null;
            bool wordStart = found == 0 || !char.IsLetterOrDigit(t[found - 1]);
            score += wordStart ? 30 : found == previous + 1 ? 15 : 5;
            previous = found;
            position = found + 1;
        }

        return score - t.Length / 4;
    }
}
