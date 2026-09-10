using System.Globalization;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Policy;

/// <summary>
/// A single matcher pattern compiled from wire text. Unknown or malformed patterns fail closed:
/// <see cref="IsValid"/> stays false and the evaluator must deny rather than skip the rule.
/// </summary>
public sealed record MatcherSpec
{
    public static readonly MatcherSpec Always = new();

    private MatcherSpec()
    {
        ArgvPattern = Array.Empty<string>();
        IsValid = true;
    }

    public MatcherSpec(
        string? toolId,
        string? toolVersion,
        string? canonicalPathPattern,
        string? resolvedExecutable,
        IReadOnlyList<string>? argvPattern,
        string? cwdPattern,
        string? operationClass,
        string? networkIntent,
        bool isValid)
    {
        ToolId = toolId;
        ToolVersion = toolVersion;
        CanonicalPathPattern = canonicalPathPattern;
        ResolvedExecutable = resolvedExecutable;
        ArgvPattern = argvPattern ?? Array.Empty<string>();
        CwdPattern = cwdPattern;
        OperationClass = operationClass;
        NetworkIntent = networkIntent;
        IsValid = isValid;
    }

    public string? ToolId { get; }
    public string? ToolVersion { get; }
    public string? CanonicalPathPattern { get; }
    public string? ResolvedExecutable { get; }
    public IReadOnlyList<string> ArgvPattern { get; }
    public string? CwdPattern { get; }
    public string? OperationClass { get; }
    public string? NetworkIntent { get; }

    public bool IsValid { get; }
}

public sealed record PolicyRuleSpec(
    string RuleId,
    string Scope,
    string Decision,
    MatcherSpec Matcher,
    long CreationOrder,
    DateTimeOffset? ExpiresAt,
    string? InvalidReason)
{
    public bool IsValid => InvalidReason is null;
}

public static class PolicyRuleMatcher
{
    /// <summary>
    /// Parses a wire rule into a spec. Structural validation already happened at snapshot
    /// validation; this compiles patterns and records (never swallows) any invalid value.
    /// </summary>
    public static PolicyRuleSpec Parse(BrokerPolicyRuleWire rule) =>
        new(
            rule.RuleId,
            rule.Scope,
            rule.Decision,
            CompileMatcher(rule.Matcher),
            rule.CreationOrder,
            ParseTimestampOrNull(rule.ExpiresAt),
            InvalidReason: null);

    public static bool Matches(PolicyRuleSpec rule, PolicyContext context, DateTimeOffset now)
    {
        if (rule.ExpiresAt is { } expiresAt && now >= expiresAt)
        {
            return false;
        }

        return MatchesMatcher(rule.Matcher, context);
    }

    public static bool MatchesMatcher(MatcherSpec matcher, PolicyContext context)
    {
        if (!matcher.IsValid)
        {
            return false;
        }

        if (matcher.ToolId is not null
            && !string.Equals(matcher.ToolId, context.ToolId, StringComparison.Ordinal)
            && !LegacyToolIdMatches(matcher.ToolId, context.ToolId, context.ToolOp, context.BashMode))
        {
            return false;
        }

        if (matcher.ToolVersion is not null && !string.Equals(matcher.ToolVersion, context.ToolVersion, StringComparison.Ordinal))
        {
            return false;
        }

        if (matcher.OperationClass is not null && !string.Equals(matcher.OperationClass, context.OperationClass, StringComparison.Ordinal))
        {
            return false;
        }

        if (matcher.NetworkIntent is not null && !string.Equals(matcher.NetworkIntent, context.NetworkIntent, StringComparison.Ordinal))
        {
            return false;
        }

        if (matcher.CanonicalPathPattern is not null)
        {
            var candidate = PatternCandidate(context);
            if (candidate is null || !GlobMatch(matcher.CanonicalPathPattern, candidate))
            {
                return false;
            }
        }

        if (matcher.ResolvedExecutable is not null && !ExecutableMatches(matcher.ResolvedExecutable, context.ResolvedExecutable))
        {
            return false;
        }

        if (matcher.ArgvPattern.Count > 0 && !ArgvMatches(matcher.ArgvPattern, context.Argv))
        {
            return false;
        }

        if (matcher.CwdPattern is not null
            && (context.CanonicalCwd is null || !GlobMatch(matcher.CwdPattern, context.CanonicalCwd)))
        {
            return false;
        }

        return true;
    }

    internal static bool GlobMatch(string pattern, string candidate)
    {
        // Restricted grammar: '*' matches within one path segment, '**' spans separators,
        // everything else compares case-insensitively as literals. Patterns are anchored
        // to the whole value, separators compare uniformly, and "{dir}/**" also covers
        // the directory itself so a directory grant reaches its own listing.
        var normalizedPattern = pattern.Replace('\\', '/');
        var normalizedCandidate = candidate.Replace('\\', '/');
        if (normalizedPattern.EndsWith("/**", StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                normalizedPattern[..^3],
                normalizedCandidate,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return GlobMatchCore(normalizedPattern.AsSpan(), normalizedCandidate.AsSpan());
    }

    private static bool GlobMatchCore(ReadOnlySpan<char> pattern, ReadOnlySpan<char> candidate)
    {
        while (true)
        {
            if (pattern.IsEmpty)
            {
                return candidate.IsEmpty;
            }

            if (pattern.Length >= 2 && pattern[0] == '*' && pattern[1] == '*')
            {
                var rest = pattern[2..];
                for (var i = 0; i <= candidate.Length; i++)
                {
                    if (GlobMatchCore(rest, candidate[i..]))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (pattern[0] == '*')
            {
                var rest = pattern[1..];
                for (var i = 0; i <= candidate.Length; i++)
                {
                    if (i > 0 && IsSeparator(candidate[i - 1]))
                    {
                        break;
                    }

                    if (GlobMatchCore(rest, candidate[i..]))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (candidate.IsEmpty || !char.ToLowerInvariant(pattern[0]).Equals(char.ToLowerInvariant(candidate[0])))
            {
                return false;
            }

            pattern = pattern[1..];
            candidate = candidate[1..];
        }
    }

    private static bool IsSeparator(char c) => c is '/' or '\\';

    private static string? PatternCandidate(PolicyContext context)
    {
        if (context.RelativePathInsideWorkspace is not null)
        {
            return context.RelativePathInsideWorkspace;
        }

        return context.Target?.CanonicalPath;
    }

    private static bool LegacyToolIdMatches(string matcherToolId, string contextToolId, string? toolOp, string? bashMode) =>
        (matcherToolId, contextToolId) switch
        {
            ("files.list", "read") => toolOp == "list",
            ("files.glob", "glob") => true,
            ("files.glob", "read") => toolOp == "list",
            ("files.search", "grep") => true,
            ("files.search", "read") => toolOp == "list",
            ("files.read", "read") => toolOp == "read",
            ("files.write", "write") => toolOp == "write",
            ("files.edit", "edit") => true,
            ("files.delete", "write") => toolOp == "delete",
            ("files.restore", "write") => toolOp == "restore",
            ("terminal.run", "bash") => bashMode == "run",
            ("terminal.shell", "bash") => bashMode == "shell",
            ("web.search", "read") => toolOp == "web_search",
            ("web.fetch", "read") => toolOp == "web_fetch",
            ("web.search", "web_search") => true,
            ("web.fetch", "web_fetch") => true,
            ("help.docs", "read") => toolOp == "help",
            _ => false,
        };

    private static bool ExecutableMatches(string pattern, string? resolvedExecutable)
    {
        if (resolvedExecutable is null)
        {
            return false;
        }

        if (string.Equals(pattern, resolvedExecutable, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var baseName = Path.GetFileName(resolvedExecutable);
        return string.Equals(pattern, baseName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ArgvMatches(IReadOnlyList<string> pattern, IReadOnlyList<string> argv)
    {
        if (pattern.Count > argv.Count)
        {
            return false;
        }

        for (var i = 0; i < pattern.Count; i++)
        {
            if (!GlobMatch(pattern[i], argv[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static MatcherSpec CompileMatcher(BrokerPolicyMatcherWire matcher)
    {
        var isValid = true;
        isValid &= IsNullOrWellFormed(matcher.ToolId, 128);
        isValid &= IsNullOrWellFormed(matcher.ToolVersion, 32);
        isValid &= IsNullOrWellFormed(matcher.CanonicalPathPattern, 4096);
        isValid &= IsNullOrWellFormed(matcher.ResolvedExecutable, 256);
        isValid &= IsNullOrWellFormed(matcher.CwdPattern, 4096);

        if (matcher.ArgvPattern is { } argv)
        {
            if (argv.Count > 64)
            {
                isValid = false;
            }

            foreach (var element in argv)
            {
                isValid &= IsNullOrWellFormed(element, 4096);
            }
        }

        if (!isValid)
        {
            throw new AgentProtocolException("invalid_snapshot_rule");
        }

        return new MatcherSpec(
            matcher.ToolId,
            matcher.ToolVersion,
            matcher.CanonicalPathPattern,
            matcher.ResolvedExecutable,
            matcher.ArgvPattern,
            matcher.CwdPattern,
            matcher.OperationClass,
            matcher.NetworkIntent,
            isValid: true);
    }

    private static bool IsNullOrWellFormed(string? value, int maximumLength) =>
        value is null || (!string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength);

    private static DateTimeOffset? ParseTimestampOrNull(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
    }
}
