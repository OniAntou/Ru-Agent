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
    public PolicyContextException(string code, Exception? innerException = null)
        : base(code, innerException)
    {
        Code = code;
    }

    public string Code { get; }
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
    int? TimeoutMs = null)
{
    public static readonly IReadOnlyList<string> EmptyArgv = Array.Empty<string>();
}
