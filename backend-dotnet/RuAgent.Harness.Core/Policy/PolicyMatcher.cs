using System.Collections.ObjectModel;

namespace RuAgent.Harness.Core.Policy;

public sealed record PolicyMatcher
{
    private const int MaxToolIdLength = 128;
    private const int MaxToolVersionLength = 32;
    private const int MaxPathPatternLength = 4096;
    private const int MaxExecutableLength = 256;
    private const int MaxCwdPatternLength = 4096;
    private const int MaxOperationClassLength = 64;
    private const int MaxNetworkIntentLength = 64;
    private const int MaxArgvPatternCount = 64;
    private const int MaxArgvPatternElementLength = 4096;

    private static readonly HashSet<string> AllowedOperationClasses = new(StringComparer.Ordinal)
    {
        "read",
        "write",
        "edit",
        "delete",
        "list",
        "glob",
        "search",
        "patch",
        "restore",
        "discovery",
        "mutation",
        "terminal",
    };

    private static readonly HashSet<string> AllowedNetworkIntents = new(StringComparer.Ordinal)
    {
        "none",
        "declared",
        "egress",
        "unknown",
    };

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
        if (ToolId is not null)
        {
            ValidateBounded(ToolId, MaxToolIdLength, nameof(ToolId));
        }

        if (ToolVersion is not null)
        {
            ValidateBounded(ToolVersion, MaxToolVersionLength, nameof(ToolVersion));
        }

        if (CanonicalPathPattern is not null)
        {
            ValidateBounded(CanonicalPathPattern, MaxPathPatternLength, nameof(CanonicalPathPattern));
        }

        if (ResolvedExecutable is not null)
        {
            ValidateBounded(ResolvedExecutable, MaxExecutableLength, nameof(ResolvedExecutable));
        }

        if (CwdPattern is not null)
        {
            ValidateBounded(CwdPattern, MaxCwdPatternLength, nameof(CwdPattern));
        }

        if (OperationClass is not null)
        {
            ValidateBounded(OperationClass, MaxOperationClassLength, nameof(OperationClass));
            if (!AllowedOperationClasses.Contains(OperationClass))
            {
                throw new ArgumentOutOfRangeException(nameof(OperationClass), $"Unknown operation class '{OperationClass}'.");
            }
        }

        if (NetworkIntent is not null)
        {
            ValidateBounded(NetworkIntent, MaxNetworkIntentLength, nameof(NetworkIntent));
            if (!AllowedNetworkIntents.Contains(NetworkIntent))
            {
                throw new ArgumentOutOfRangeException(nameof(NetworkIntent), $"Unknown network intent '{NetworkIntent}'.");
            }
        }

        IReadOnlyList<string>? argvCopy = null;
        if (ArgvPattern is not null)
        {
            if (ArgvPattern.Count > MaxArgvPatternCount)
            {
                throw new ArgumentOutOfRangeException(nameof(ArgvPattern), $"Argv pattern count exceeds {MaxArgvPatternCount}.");
            }

            var copy = new List<string>(ArgvPattern.Count);
            foreach (var element in ArgvPattern)
            {
                if (element is null)
                {
                    throw new ArgumentException("Argv pattern element cannot be null.", nameof(ArgvPattern));
                }

                ValidateBounded(element, MaxArgvPatternElementLength, $"{nameof(ArgvPattern)} element");
                copy.Add(element);
            }

            argvCopy = new ReadOnlyCollection<string>(copy);
        }

        this.ToolId = ToolId;
        this.ToolVersion = ToolVersion;
        this.CanonicalPathPattern = CanonicalPathPattern;
        this.ResolvedExecutable = ResolvedExecutable;
        this.ArgvPattern = argvCopy;
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

    internal void Validate()
    {
        // Construction already validated; this ensures detached deserialization paths also fail closed.
        if (ToolId is not null)
        {
            ValidateBounded(ToolId, MaxToolIdLength, nameof(ToolId));
        }

        if (ToolVersion is not null)
        {
            ValidateBounded(ToolVersion, MaxToolVersionLength, nameof(ToolVersion));
        }

        if (CanonicalPathPattern is not null)
        {
            ValidateBounded(CanonicalPathPattern, MaxPathPatternLength, nameof(CanonicalPathPattern));
        }

        if (ResolvedExecutable is not null)
        {
            ValidateBounded(ResolvedExecutable, MaxExecutableLength, nameof(ResolvedExecutable));
        }

        if (CwdPattern is not null)
        {
            ValidateBounded(CwdPattern, MaxCwdPatternLength, nameof(CwdPattern));
        }

        if (OperationClass is not null)
        {
            ValidateBounded(OperationClass, MaxOperationClassLength, nameof(OperationClass));
            if (!AllowedOperationClasses.Contains(OperationClass))
            {
                throw new ArgumentOutOfRangeException(nameof(OperationClass), $"Unknown operation class '{OperationClass}'.");
            }
        }

        if (NetworkIntent is not null)
        {
            ValidateBounded(NetworkIntent, MaxNetworkIntentLength, nameof(NetworkIntent));
            if (!AllowedNetworkIntents.Contains(NetworkIntent))
            {
                throw new ArgumentOutOfRangeException(nameof(NetworkIntent), $"Unknown network intent '{NetworkIntent}'.");
            }
        }

        if (ArgvPattern is not null)
        {
            if (ArgvPattern.Count > MaxArgvPatternCount)
            {
                throw new ArgumentOutOfRangeException(nameof(ArgvPattern), $"Argv pattern count exceeds {MaxArgvPatternCount}.");
            }

            foreach (var element in ArgvPattern)
            {
                if (element is null)
                {
                    throw new ArgumentException("Argv pattern element cannot be null.", nameof(ArgvPattern));
                }

                ValidateBounded(element, MaxArgvPatternElementLength, $"{nameof(ArgvPattern)} element");
            }
        }
    }

    private static void ValidateBounded(string value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new ArgumentException($"Value must contain between 1 and {maximumLength} characters.", parameterName);
        }

        if (value.Any(character => character == '\0'))
        {
            throw new ArgumentException("Value cannot contain null characters.", parameterName);
        }
    }
}
