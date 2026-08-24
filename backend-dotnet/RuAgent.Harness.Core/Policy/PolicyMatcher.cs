using System.Collections.ObjectModel;

namespace RuAgent.Harness.Core.Policy;

public sealed record PolicyMatcher
{
    public PolicyMatcher(
        string? ToolId = null,
        string? ToolVersion = null,
        string? CanonicalPathPattern = null,
        string? ResolvedExecutable = null,
        IReadOnlyList<string>? ArgvPattern = null,
        string? CwdPattern = null,
        string? OperationClass = null,
        string? NetworkIntent = null)
    {
        this.ToolId = ToolId;
        this.ToolVersion = ToolVersion;
        this.CanonicalPathPattern = CanonicalPathPattern;
        this.ResolvedExecutable = ResolvedExecutable;
        this.ArgvPattern = ArgvPattern is null
            ? null
            : new ReadOnlyCollection<string>(ArgvPattern.ToArray());
        this.CwdPattern = CwdPattern;
        this.OperationClass = OperationClass;
        this.NetworkIntent = NetworkIntent;
    }

    public string? ToolId { get; }

    public string? ToolVersion { get; }

    public string? CanonicalPathPattern { get; }

    public string? ResolvedExecutable { get; }

    public IReadOnlyList<string>? ArgvPattern { get; }

    public string? CwdPattern { get; }

    public string? OperationClass { get; }

    public string? NetworkIntent { get; }
}
