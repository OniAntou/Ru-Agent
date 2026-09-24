using RuAgent.Broker.Tools.Paths;
using RuAgent.Broker.Tools.Terminal;
using RuAgent.Protocol;

namespace RuAgent.Broker.Policy.Commands;

public sealed record TerminalIntentClassification(
    bool IsBoundedLocal,
    string ReasonCode);

public sealed class TerminalIntentClassifier
{
    private static readonly char[] ShellSyntax = ['|', '>', '<', ';', '&', '\r', '\n', '`'];
    private readonly CommandAnalyzerRegistry _analyzers;

    public TerminalIntentClassifier(CommandAnalyzerRegistry? analyzers = null)
    {
        _analyzers = analyzers ?? new CommandAnalyzerRegistry();
    }

    public TerminalIntentClassification Classify(PolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(context.ToolId, "bash", StringComparison.Ordinal))
        {
            return Reject("terminal_tool_invalid");
        }

        if (context.BashMode is not (null or "run" or "shell"))
        {
            return Reject("terminal_mode_invalid");
        }

        if (context.BashMode != "shell" && HasPathQualifiedExecutable(context.ResolvedExecutable))
        {
            return Reject("terminal_external_executable_requires_approval");
        }

        var analysis = context.BashMode == "shell"
            ? context.TerminalAnalysis ?? _analyzers.Analyze(context.ResolvedExecutable, context.Argv)
            : _analyzers.Analyze(context.ResolvedExecutable, context.Argv);
        if (context.BashMode == "shell")
        {
            return Reject(RiskReason(analysis));
        }

        if (context.Argv.Any(HasUnparsedShellSyntax))
        {
            return Reject("terminal_unparsed_shell_syntax_requires_approval");
        }

        if (!TerminalAutoPolicy.IsAutoExecutable(analysis.OperationClass))
        {
            return Reject(RiskReason(analysis));
        }

        if (HasNetworkIntent(context, analysis))
        {
            return Reject("terminal_network_requires_approval");
        }

        if (HasUnsafeWorkingDirectory(context))
        {
            return Reject(WorkingDirectoryReason(context));
        }

        if (HasUnsafeArgvPath(context))
        {
            return Reject("terminal_external_argv_requires_approval");
        }

        return new TerminalIntentClassification(true, "bounded_local");
    }

    private static string RiskReason(CommandAnalysis analysis) => analysis.OperationClass switch
    {
        TerminalOperationClass.ExternalMutation => "terminal_external_mutation_requires_approval",
        TerminalOperationClass.DirectDelete => "terminal_delete_requires_approval",
        _ => "terminal_unknown_executable",
    };

    private static bool HasPathQualifiedExecutable(string? executable) =>
        !string.IsNullOrWhiteSpace(executable)
        && (Path.IsPathRooted(executable)
            || executable.Contains('/')
            || executable.Contains('\\'));

    private static bool HasNetworkIntent(PolicyContext context, CommandAnalysis analysis) =>
        context.NetworkDeclared
        || !string.Equals(context.NetworkIntent, PolicyNetworkIntents.None, StringComparison.Ordinal)
        || !string.Equals(analysis.NetworkIntent, PolicyNetworkIntents.None, StringComparison.Ordinal);

    private static bool HasUnsafeWorkingDirectory(PolicyContext context) =>
        context.WorkingDirectory is null
        || string.IsNullOrWhiteSpace(context.CanonicalCwd)
        || context.WorkingDirectory.IsProtected
        || context.WorkingDirectory.IsOutsideWorkspace
        || !string.Equals(
            context.WorkingDirectory.CanonicalPath,
            context.CanonicalCwd,
            StringComparison.OrdinalIgnoreCase);

    private static string WorkingDirectoryReason(PolicyContext context) =>
        context.WorkingDirectory is { IsOutsideWorkspace: true }
            ? "terminal_external_cwd_requires_approval"
            : "terminal_cwd_untrusted";

    private static bool HasUnsafeArgvPath(PolicyContext context)
    {
        if (string.IsNullOrWhiteSpace(context.CanonicalCwd))
        {
            return true;
        }

        CanonicalPathResolver resolver;
        try
        {
            resolver = new CanonicalPathResolver(context.CanonicalWorkspaceRoot);
        }
        catch (ArgumentException)
        {
            return true;
        }

        foreach (var argument in context.Argv)
        {
            var candidate = PathArgument(argument);
            if (candidate is null)
            {
                continue;
            }

            try
            {
                var absolute = Path.IsPathRooted(candidate)
                    ? candidate
                    : Path.Combine(context.CanonicalCwd, candidate);
                var resolved = resolver.Resolve(absolute);
                if (!resolved.IsInsideWorkspace)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        return false;
    }

    private static string? PathArgument(string argument)
    {
        if (!argument.StartsWith('-'))
        {
            return string.IsNullOrWhiteSpace(argument) ? null : argument;
        }

        var separator = argument.IndexOf('=');
        if (separator > 0 && separator < argument.Length - 1)
        {
            return argument[(separator + 1)..];
        }

        return AttachedPathArgument(argument);
    }

    private static string? AttachedPathArgument(string argument)
    {
        for (var index = 1; index < argument.Length; index++)
        {
            var candidate = argument[index..];
            if (Path.IsPathRooted(candidate))
            {
                return candidate;
            }
        }

        for (var index = 1; index < argument.Length; index++)
        {
            var candidate = argument[index..];
            if (LooksLikePath(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool LooksLikePath(string candidate) =>
        Path.IsPathRooted(candidate)
        || candidate.Contains('/')
        || candidate.Contains('\\')
        || candidate.StartsWith('.')
        || Path.HasExtension(candidate);

    private static bool HasUnparsedShellSyntax(string argument) =>
        argument.IndexOfAny(ShellSyntax) >= 0
        || argument.Contains("$(", StringComparison.Ordinal);

    private static TerminalIntentClassification Reject(string reasonCode) =>
        new(false, reasonCode);
}
