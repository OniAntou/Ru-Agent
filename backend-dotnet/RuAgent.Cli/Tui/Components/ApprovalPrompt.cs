namespace RuAgent.Cli.Tui;

/// <summary>One selectable answer to an approval request.</summary>
public sealed record ApprovalChoice(string Label, string Description, string Decision);

/// <summary>
/// A pending approval rendered where the composer lives: arrow keys or digits pick, Enter
/// confirms, Escape denies. Navigation is delegated to <see cref="ChoiceList"/>, so approvals
/// behave exactly like the other pickers. The streaming side awaits
/// <see cref="Decision"/> and posts the answer.
/// </summary>
public sealed class ApprovalPrompt
{
    public const string AllowOnce = "once";
    public const string AllowDirectory = "session_directory";
    public const string AllowAlways = "always";
    public const string Deny = "deny";

    // A protected-path ask is deliberately absent from this set: it is not
    // remembered, so a single approval cannot become standing access to a
    // credential directory. The GUI mirrors this in
    // AgentApprovalPresentation.OneTimeOnlyReasons.
    private static readonly HashSet<string> RememberableAskReasons = new(StringComparer.Ordinal)
    {
        "terminal_delete_requires_approval",
        "terminal_external_mutation_requires_approval",
        "terminal_unknown_executable",
        "terminal_timeout_requires_approval",
        "web_approval_required",
    };

    private readonly List<ApprovalChoice> _choices;
    private readonly ChoiceList _list;

    public ApprovalPrompt(string summary, IReadOnlyList<ApprovalChoice> choices, int defaultIndex = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        ArgumentNullException.ThrowIfNull(choices);
        if (choices.Count == 0)
        {
            throw new ArgumentException("An approval needs at least one choice.", nameof(choices));
        }

        Summary = TuiText.Truncate(summary, 60);
        _choices = [.. choices];
        _list = new ChoiceList(
            "approval · " + Summary,
            _choices.Select(c => new ChoiceItem(c.Label, c.Description, c.Decision)).ToList(),
            defaultIndex,
            "↑↓ · 1-9 · enter · esc");
    }

    /// <summary>Bounded safe summary code from the wire (never raw command text).</summary>
    public string Summary { get; }

    public IReadOnlyList<ApprovalChoice> Choices => _choices;

    public int Selected
    {
        get => _list.Selected;
        set => _list.Selected = Math.Clamp(value, 0, _choices.Count - 1);
    }

    public ApprovalChoice Current => _choices[_list.Selected];

    /// <summary>Completes with the chosen decision token once the operator confirms.</summary>
    public TaskCompletionSource<string> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The two-way prompt used by the interactive surface. A directory grant is
    /// only offered for external-directory asks: the server fails any other
    /// allow_session_directory with invalid_approval_command, so offering it
    /// elsewhere turns a silent escalation into an ordinary approval error.
    /// A remember-me choice is offered for known non-external asks instead;
    /// the server persists it as a project rule. Callers pass the wire
    /// SafeCode (the ask reason); anything else denies the folder choice.
    /// </summary>
    public static ApprovalPrompt For(string safeSummary, bool offerDirectory = false)
    {
        var directoryAllowed = string.Equals(
            safeSummary?.Trim(),
            "external_directory_required",
            StringComparison.Ordinal);
        // Labels stay within SlashDropdown.LabelColumn so they are never clipped.
        var choices = new List<ApprovalChoice>
        {
            new("1 · once", "allow this call", AllowOnce),
            new("2 · deny", "abort this tool call", Deny),
        };

        if (offerDirectory && directoryAllowed)
        {
            choices.Add(new("3 · folder", "allow this folder for the session", AllowDirectory));
        }
        else if (!directoryAllowed && RememberableAskReasons.Contains(safeSummary?.Trim() ?? string.Empty))
        {
            choices.Add(new("3 · always", "allow and remember for this project", AllowAlways));
        }

        return new ApprovalPrompt(TuiText.Truncate(safeSummary, 60), choices, defaultIndex: 1);
    }

    public void Move(int delta) => _list.Move(delta);

    /// <summary>Applies a digit key (1-based) when it matches a choice.</summary>
    public bool TrySelectDigit(char digit) => _list.TrySelectDigit(digit);

    /// <summary>Routes one keystroke through the shared list behaviour.</summary>
    public ChoiceOutcome HandleKey(ConsoleKeyInfo key) => _list.HandleKey(key);

    public void Resolve(string decision) => Decision.TrySetResult(decision);

    /// <summary>The approval block, drawn with the same frame as every other list.</summary>
    public IReadOnlyList<string> Render(int width) => _list.Render(width);
}
