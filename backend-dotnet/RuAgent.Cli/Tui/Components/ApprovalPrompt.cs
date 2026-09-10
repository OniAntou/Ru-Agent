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
    public const string Deny = "deny";

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
    /// The three-way prompt used by the interactive surface. Directory grants are only offered
    /// because the host decides whether the pending binding supports them; a rejection there
    /// surfaces as an ordinary approval error instead of a silent escalation.
    /// </summary>
    public static ApprovalPrompt For(string safeSummary, bool offerDirectory = true)
    {
        // Labels stay within SlashDropdown.LabelColumn so they are never clipped.
        var choices = new List<ApprovalChoice>
        {
            new("1 · once", "allow this call", AllowOnce),
            new("2 · deny", "abort this tool call", Deny),
        };

        if (offerDirectory)
        {
            choices.Add(new("3 · folder", "allow this folder for the session", AllowDirectory));
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
