namespace RuApp.Services;

/// <summary>
/// Turns an approval reason code into the sentence the user reads in the inline
/// approval panel.
///
/// The reason codes are the ones RuAgentBroker emits on the wire, so this list has
/// to stay in step with the Broker's policy: a new ask reason that is not mapped
/// here would fall through to the raw token, which tells the user nothing about why
/// their action is being held.
/// </summary>
public static class AgentApprovalSummaryPresentation
{
    /// <summary>Longest summary the panel renders before truncating.</summary>
    public const int MaximumSummaryLength = 64;

    public const string ExternalDirectoryRequired = "external_directory_required";
    public const string TerminalDeleteRequiresApproval = "terminal_delete_requires_approval";
    public const string TerminalExternalMutationRequiresApproval = "terminal_external_mutation_requires_approval";
    public const string TerminalTimeoutRequiresApproval = "terminal_timeout_requires_approval";
    public const string WebApprovalRequired = "web_approval_required";
    public const string TerminalUnknownExecutable = "terminal_unknown_executable";

    /// <summary>
    /// Every reason code that has dedicated copy. Kept public so a test can assert
    /// that the set matches the ask reasons the Broker can produce.
    /// </summary>
    public static IReadOnlyList<string> MappedReasonCodes { get; } =
    [
        ExternalDirectoryRequired,
        TerminalDeleteRequiresApproval,
        TerminalExternalMutationRequiresApproval,
        TerminalTimeoutRequiresApproval,
        WebApprovalRequired,
        TerminalUnknownExecutable,
    ];

    /// <summary>
    /// The panel copy for one summary. A recognised reason code becomes a sentence;
    /// an unrecognised one is passed through bounded rather than hidden, so a new
    /// Broker reason is visible instead of being replaced by a generic line.
    /// </summary>
    public static string Render(string? summary)
    {
        string trimmed = Bounded(summary ?? string.Empty, MaximumSummaryLength);
        return trimmed switch
        {
            ExternalDirectoryRequired =>
                "Ru needs to access a directory outside the project in this chat until the app closes.",
            TerminalDeleteRequiresApproval =>
                "This terminal command will delete files.",
            TerminalExternalMutationRequiresApproval =>
                "This terminal command changes something outside the workspace (network, install).",
            TerminalTimeoutRequiresApproval =>
                "This terminal command runs longer than the standard window.",
            WebApprovalRequired =>
                "Ru needs approval to fetch this web content.",
            TerminalUnknownExecutable =>
                "Ru has not recognized the executable for this terminal command.",
            "" => "Agent is requesting an action that requires approval.",
            _ => trimmed,
        };
    }

    /// <summary>Collapses newlines and truncates so the panel cannot be grown by a frame.</summary>
    public static string Bounded(string value, int maxLength)
    {
        string normalized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "…";
    }
}
