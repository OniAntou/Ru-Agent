using RuAgent.Protocol;

namespace RuAgent.Broker.Policy.Commands;

public sealed record TerminalIntentClassification(bool IsWorkspaceLaunch, string ReasonCode);

/// <summary>
/// Classifies the launch directory, not what a program might do. Both terminal
/// modes run as the Windows user; argv, language and network metadata grant no authority.
/// </summary>
public sealed class TerminalIntentClassifier
{
    internal const string ProtectedPathReason = "terminal_protected_path_requires_approval";

    public TerminalIntentClassification Classify(PolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ToolId is not ("bash" or "powershell" or "run_process")) return Reject("terminal_tool_invalid");
        if (context.BashMode is not (null or "run" or "shell")) return Reject("terminal_mode_invalid");
        var directory = context.WorkingDirectory;
        if (directory is null || string.IsNullOrWhiteSpace(context.CanonicalCwd)
            || !string.Equals(directory.CanonicalPath, context.CanonicalCwd, StringComparison.OrdinalIgnoreCase))
            return Reject("terminal_cwd_untrusted");
        if (directory.IsProtected) return Reject(ProtectedPathReason);
        if (!directory.IsInsideWorkspace) return Reject("terminal_external_cwd_requires_approval");
        return new(true, "workspace_terminal_allow");
    }

    private static TerminalIntentClassification Reject(string reason) => new(false, reason);
}
