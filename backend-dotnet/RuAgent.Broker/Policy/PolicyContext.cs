using RuAgent.Broker.Policy.Commands;
using RuAgent.Broker.Tools.Paths;

namespace RuAgent.Broker.Policy;

public static class PolicyOperationClasses
{
    public const string Read = "read";
    public const string Write = "write";
    public const string Edit = "edit";
    public const string Delete = "delete";
    public const string List = "list";
    public const string Glob = "glob";
    public const string Search = "search";
    public const string Patch = "patch";
    public const string Restore = "restore";
    public const string Discovery = "discovery";
    public const string Mutation = "mutation";
    public const string Terminal = "terminal";
}

public static class PolicyNetworkIntents
{
    public const string None = "none";
    public const string Declared = "declared";
    public const string Egress = "egress";
    public const string Unknown = "unknown";
}

public sealed class PolicyContextException : Exception
{
    /// <summary>
    /// Maximum length of the bounded hint that may travel to the model next to
    /// the code. The hint names the argument shape the call was missing; it never
    /// carries file contents, paths outside the workspace, or credentials.
    /// </summary>
    public const int MaximumHintLength = 200;

    public PolicyContextException(string code, Exception? innerException = null, string? hint = null)
        : base(code, innerException)
    {
        Code = code;
        Hint = Sanitize(hint);
    }

    public string Code { get; }

    /// <summary>
    /// Bounded, model-facing explanation of how to repair the call, or null when
    /// the code alone is actionable.
    /// </summary>
    public string? Hint { get; }

    private static string? Sanitize(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint))
        {
            return null;
        }

        var cleaned = hint.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (cleaned.Contains('\0'))
        {
            return null;
        }

        return cleaned.Length <= MaximumHintLength ? cleaned : cleaned[..MaximumHintLength];
    }
}

/// <summary>
/// The trusted, canonical description of one tool call. Built only by <see cref="PolicyContextFactory"/>
/// from trusted resolution: the model cannot label its own call as safe and any model-supplied
/// safety fields are ignored.
/// </summary>
public sealed record PolicyContext(
    string ToolId,
    string ToolVersion,
    string CanonicalWorkspaceRoot,
    ResolvedPath? Target,
    string? RelativePathInsideWorkspace,
    string? ResolvedExecutable,
    IReadOnlyList<string> Argv,
    string? CanonicalCwd,
    string OperationClass,
    string NetworkIntent,
    bool NetworkDeclared,
    string IntentHash,
    int? TimeoutMs = null,
    string SessionId = "",
    ResolvedPath? WorkingDirectory = null,
    CommandAnalysis? TerminalAnalysis = null,
    string? ToolOp = null,
    string? BashMode = null)
{
    public static readonly IReadOnlyList<string> EmptyArgv = Array.Empty<string>();
}
