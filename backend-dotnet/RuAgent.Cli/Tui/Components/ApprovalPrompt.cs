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
    public const string TrustApprove = "trust_approve";

    // Protected-path asks are deliberately absent: remembering one could turn
    // a single approval into standing access to a credential directory.
    private static readonly HashSet<string> RememberableAskReasons = new(StringComparer.Ordinal)
    {
        "terminal_delete_requires_approval",
        "terminal_external_mutation_requires_approval",
        "terminal_unknown_executable",
        "terminal_timeout_requires_approval",
        "web_approval_required",
        "terminal_external_cwd_requires_approval",
        "terminal_cwd_untrusted",
        "terminal_network_requires_approval",
        "terminal_external_argv_requires_approval",
        "terminal_external_executable_requires_approval",
        "terminal_unparsed_shell_syntax_requires_approval",
        "recognized_executable_unclassified_form",
        "git_submodule_requires_approval",
        "git_directory_override_requires_approval",
        "git_config_override_requires_approval",
        "git_config_mutation_requires_approval",
    };

    private readonly List<ApprovalChoice> _choices;
    private readonly ChoiceList _list;

    public ApprovalPrompt(
        string summary,
        IReadOnlyList<ApprovalChoice> choices,
        int defaultIndex = 1,
        string? title = null)
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
            title ?? "approval · " + Summary,
            _choices.Select(c => new ChoiceItem(c.Label, c.Description, c.Decision)).ToList(),
            defaultIndex,
            "↑↓ · 1-9 · enter · esc");
    }

    /// <summary>Bounded safe summary code from the wire (never raw command text).</summary>
    public string Summary { get; }

    /// <summary>The card heading: what is being approved, with the ask's safe code.</summary>
    public string Title => _list.Title;

    /// <summary>The card footer: the shared navigation keys.</summary>
    public string? Footer => _list.Footer;

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
    /// The two-way prompt used by the interactive surface. A directory decision
    /// is only offered for external-directory asks: the server fails any other
    /// allow_session_directory with invalid_approval_command, so offering it
    /// elsewhere turns a silent escalation into an ordinary approval error.
    /// The external ask offers both halves of a folder approval: the
    /// session-scoped grant and the durable project-scope remember (registered
    /// as a bounded folder pattern). A remember-me choice is offered for known
    /// non-external asks instead; the server persists it as a project rule.
    /// Callers pass the wire SafeCode (the ask reason); anything else denies
    /// the folder choices.
    /// </summary>
    public static ApprovalPrompt For(string safeSummary, bool offerDirectory = false)
    {
        var reason = safeSummary?.Trim() ?? string.Empty;
        var directoryAllowed = string.Equals(
            reason,
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
            choices.Add(new("4 · always", "allow and remember this folder for this project", AllowAlways));
        }
        else if (!directoryAllowed && RememberableAskReasons.Contains(reason))
        {
            choices.Add(new("3 · always", "allow and remember for this project", AllowAlways));
        }

        return new ApprovalPrompt(
            TuiText.Truncate(safeSummary, 60),
            choices,
            defaultIndex: 1);
    }

    /// <summary>
    /// The workspace-trust ask. Trust is a one-time grant per canonical workspace
    /// identity, so the prompt says what is being trusted and that the host keeps it.
    /// The default sits on cancel: a bare Enter must never widen what the agent may read,
    /// and while this ask is open nothing has executed yet, so cancelling rolls back
    /// nothing. The same <see cref="ChoiceList"/> frame and keyboard drive it, which is
    /// what lets the composer ask at all — the type-ahead loop already owns stdin.
    /// </summary>
    public static ApprovalPrompt ForWorkspaceTrust(string workspacePath) =>
        new(
            TuiText.Truncate(workspacePath, 60),
            new List<ApprovalChoice>
            {
                new("1 · trust", "allow this workspace once, kept on this host", TrustApprove),
                new("2 · cancel", "do not trust; this run is cancelled", Deny),
            },
            defaultIndex: 1,
            title: "trust · " + TuiText.Truncate(workspacePath, 44));

    public void Move(int delta) => _list.Move(delta);

    /// <summary>Applies a digit key (1-based) when it matches a choice.</summary>
    public bool TrySelectDigit(char digit) => _list.TrySelectDigit(digit);

    /// <summary>Routes one keystroke through the shared list behaviour.</summary>
    public ChoiceOutcome HandleKey(ConsoleKeyInfo key) => _list.HandleKey(key);

    public void Resolve(string decision) => Decision.TrySetResult(decision);

    /// <summary>The approval block, drawn with the same frame as every other list.</summary>
    public IReadOnlyList<string> Render(int width) => _list.Render(width);

    /// <summary>
    /// The choices as card rows: the label left, the description right, the chosen one as
    /// the filled bar. The window composes these into the shared card frame.
    /// </summary>
    public IReadOnlyList<CardRow> CardBody() =>
        _choices
            .Select((choice, index) => new CardRow(
                index == _list.Selected ? CardRowKind.Selected : CardRowKind.Item,
                choice.Label,
                string.IsNullOrEmpty(choice.Description) ? null : choice.Description))
            .ToList();
}
