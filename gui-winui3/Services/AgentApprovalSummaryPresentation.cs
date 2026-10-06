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
    public const string TerminalProtectedPathRequiresApproval = "terminal_protected_path_requires_approval";
    public const string TerminalExternalCwdRequiresApproval = "terminal_external_cwd_requires_approval";
    public const string TerminalCwdUntrusted = "terminal_cwd_untrusted";
    public const string TerminalNetworkRequiresApproval = "terminal_network_requires_approval";
    public const string TerminalExternalArgvRequiresApproval = "terminal_external_argv_requires_approval";
    public const string TerminalExternalExecutableRequiresApproval = "terminal_external_executable_requires_approval";
    public const string TerminalUnparsedShellSyntaxRequiresApproval = "terminal_unparsed_shell_syntax_requires_approval";
    public const string RecognizedExecutableUnclassifiedForm = "recognized_executable_unclassified_form";
    public const string GitSubmoduleRequiresApproval = "git_submodule_requires_approval";
    public const string GitDirectoryOverrideRequiresApproval = "git_directory_override_requires_approval";
    public const string GitConfigOverrideRequiresApproval = "git_config_override_requires_approval";
    public const string GitConfigMutationRequiresApproval = "git_config_mutation_requires_approval";
    public const string PlanPresetRequiresApproval = "plan_preset_requires_approval";
    public const string RuleAsks = "rule_asks";

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
        TerminalProtectedPathRequiresApproval,
        TerminalExternalCwdRequiresApproval,
        TerminalCwdUntrusted,
        TerminalNetworkRequiresApproval,
        TerminalExternalArgvRequiresApproval,
        TerminalExternalExecutableRequiresApproval,
        TerminalUnparsedShellSyntaxRequiresApproval,
        RecognizedExecutableUnclassifiedForm,
        GitSubmoduleRequiresApproval,
        GitDirectoryOverrideRequiresApproval,
        GitConfigOverrideRequiresApproval,
        GitConfigMutationRequiresApproval,
        PlanPresetRequiresApproval,
        RuleAsks,
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
            TerminalProtectedPathRequiresApproval =>
                "This terminal command points at a protected location (SSH keys, cloud credentials, .env).",
            TerminalExternalCwdRequiresApproval =>
                "This terminal command runs in a folder outside the workspace.",
            TerminalCwdUntrusted =>
                "This terminal command runs in a folder Ru cannot verify.",
            TerminalNetworkRequiresApproval =>
                "This terminal command needs network access.",
            TerminalExternalArgvRequiresApproval =>
                "This terminal command passes a path that points outside the workspace.",
            TerminalExternalExecutableRequiresApproval =>
                "This terminal command runs an executable outside the workspace.",
            TerminalUnparsedShellSyntaxRequiresApproval =>
                "This terminal command uses shell syntax Ru cannot fully verify.",
            RecognizedExecutableUnclassifiedForm =>
                "Ru recognized the executable but not the shape of this command.",
            GitSubmoduleRequiresApproval =>
                "This git command touches submodules.",
            GitDirectoryOverrideRequiresApproval =>
                "This git command points git at a different directory.",
            GitConfigOverrideRequiresApproval =>
                "This git command overrides git configuration for one run.",
            GitConfigMutationRequiresApproval =>
                "This git command changes git configuration.",
            PlanPresetRequiresApproval =>
                "The Plan preset asks before every change; approve once or switch preset.",
            RuleAsks =>
                "One of your project rules asks before this action.",
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
