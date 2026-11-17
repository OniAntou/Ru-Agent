using System.Diagnostics.CodeAnalysis;

namespace RuAgent.Client;

/// <summary>
/// The sentence a bounded approval ask reason becomes on a user-facing surface. The
/// wire value stays a machine token (<c>external_directory_required</c>); the terminal
/// and desktop approval cards show this copy instead of letting the token leak into a
/// prompt title. Unknown reasons fall back to a generic request sentence; callers that
/// need the raw token for diagnostics read the wire value separately.
/// </summary>
public static class AgentApprovalCopy
{
    /// <summary>Shown for an ask reason this build has no dedicated sentence for.</summary>
    public const string Generic = "Agent is requesting an action that requires approval.";

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
    public const string RuleAsks = "rule_asks";
    public const string McpCallRequiresApproval = "mcp_call_requires_approval";
    public const string ComputerAccessRequiresApproval = "computer_access_requires_approval";

    private static readonly Dictionary<string, string> Table = new(StringComparer.Ordinal)
    {
        [ExternalDirectoryRequired] =
            "Ru needs to access files in a folder outside the project workspace.",
        [TerminalDeleteRequiresApproval] =
            "This terminal command will delete files.",
        [TerminalExternalMutationRequiresApproval] =
            "This terminal command changes something outside the workspace (network, install).",
        [TerminalTimeoutRequiresApproval] =
            "This terminal command runs longer than the standard window.",
        [WebApprovalRequired] =
            "Ru needs approval to fetch this web content.",
        [McpCallRequiresApproval] =
            "This starts or calls one of your MCP servers, a program that runs with your rights.",
        [ComputerAccessRequiresApproval] =
            "This lets Ru see and control these apps on your screen for the rest of the session. Press Ctrl+Alt+Backspace (or Ctrl+Alt+Pause) to stop it.",
        [TerminalUnknownExecutable] =
            "Ru has not recognized the executable for this terminal command.",
        [TerminalProtectedPathRequiresApproval] =
            "This terminal command points at a protected location (SSH keys, cloud credentials, .env).",
        [TerminalExternalCwdRequiresApproval] =
            "This terminal command runs in a folder outside the workspace.",
        [TerminalCwdUntrusted] =
            "This terminal command runs in a folder Ru cannot verify.",
        [TerminalNetworkRequiresApproval] =
            "This terminal command needs network access.",
        [TerminalExternalArgvRequiresApproval] =
            "This terminal command passes a path that points outside the workspace.",
        [TerminalExternalExecutableRequiresApproval] =
            "This terminal command runs an executable outside the workspace.",
        [TerminalUnparsedShellSyntaxRequiresApproval] =
            "This terminal command uses shell syntax Ru cannot fully verify.",
        [RecognizedExecutableUnclassifiedForm] =
            "Ru recognized the executable but not the shape of this command.",
        [GitSubmoduleRequiresApproval] =
            "This git command touches submodules.",
        [GitDirectoryOverrideRequiresApproval] =
            "This git command points git at a different directory.",
        [GitConfigOverrideRequiresApproval] =
            "This git command overrides git configuration for one run.",
        [GitConfigMutationRequiresApproval] =
            "This git command changes git configuration.",
        [RuleAsks] =
            "One of your project rules asks before this action.",
    };

    /// <summary>Resolves a known ask reason; a blank or unknown token returns false.</summary>
    public static bool TryGet(string? code, [NotNullWhen(true)] out string? sentence) =>
        Table.TryGetValue((code ?? string.Empty).Trim(), out sentence);

    /// <summary>The user-facing sentence, or <see cref="Generic"/> for an unknown reason.</summary>
    public static string Render(string? code) =>
        TryGet(code, out var sentence) ? sentence : Generic;
}
