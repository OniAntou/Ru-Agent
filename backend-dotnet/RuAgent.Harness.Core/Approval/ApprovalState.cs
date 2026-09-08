using RuAgent.Harness.Core.Policy;

namespace RuAgent.Harness.Core.Approval;

/// <summary>
/// The immutable canonical intent one pending approval is bound to. Any change to any
/// field produces a new hash and therefore a stale approval, per the approved design.
/// </summary>
public sealed record ToolApprovalBinding
{
    private static readonly string[] AllowedOperations =
    {
        "read", "write", "edit", "delete", "list", "glob",
        "search", "patch", "restore", "discovery", "mutation", "terminal",
    };

    private static readonly string[] AllowedNetworkIntents =
    {
        "none", "declared", "egress", "unknown",
    };

    public ToolApprovalBinding(
        RunId runId,
        ToolCallId toolCallId,
        string toolId,
        string toolVersion,
        string? canonicalPath,
        string? resolvedExecutable,
        IReadOnlyList<string> argv,
        string? cwd,
        string operationClass,
        string networkIntent,
        long snapshotVersion,
        string intentHash,
        DateTimeOffset expiresAt)
    {
        _ = runId.Value;
        _ = toolCallId.Value;
        ValidateBounded(toolId, 128, nameof(toolId));
        ValidateBounded(toolVersion, 32, nameof(toolVersion));
        ValidateOptional(canonicalPath, 4096, nameof(canonicalPath));
        ValidateOptional(resolvedExecutable, 256, nameof(resolvedExecutable));

        ArgumentNullException.ThrowIfNull(argv);
        if (argv.Count > 64)
        {
            throw new ArgumentException("Argv pattern must be bounded.", nameof(argv));
        }

        foreach (var argument in argv)
        {
            ValidateBounded(argument, 4096, nameof(argv));
        }

        ValidateOptional(cwd, 4096, nameof(cwd));

        if (!AllowedOperations.Contains(operationClass, StringComparer.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(operationClass), operationClass);
        }

        if (!AllowedNetworkIntents.Contains(networkIntent, StringComparer.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(networkIntent), networkIntent);
        }

        ValidateBounded(intentHash, 128, nameof(intentHash));

        RunId = runId;
        ToolCallId = toolCallId;
        ToolId = toolId;
        ToolVersion = toolVersion;
        CanonicalPath = canonicalPath;
        ResolvedExecutable = resolvedExecutable;
        Argv = argv.ToArray();
        Cwd = cwd;
        OperationClass = operationClass;
        NetworkIntent = networkIntent;
        SnapshotVersion = snapshotVersion;
        IntentHash = intentHash;
        ExpiresAt = expiresAt;
    }

    private static void ValidateOptional(string? value, int maximumLength, string parameterName)
    {
        if (value is not null)
        {
            ValidateBounded(value, maximumLength, parameterName);
        }
    }

    private static void ValidateBounded(string? value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value.Contains('\0'))
        {
            throw new ArgumentException("Value must be non-empty, bounded, and contain no null characters.", parameterName);
        }
    }

    public RunId RunId { get; init; }

    public ToolCallId ToolCallId { get; init; }

    public string ToolId { get; init; }

    public string ToolVersion { get; init; }

    public string? CanonicalPath { get; init; }

    public string? ResolvedExecutable { get; init; }

    public IReadOnlyList<string> Argv { get; init; }

    public string? Cwd { get; init; }

    public string OperationClass { get; init; }

    public string NetworkIntent { get; init; }

    public long SnapshotVersion { get; init; }

    public string IntentHash { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }
}

public enum ApprovalOutcome
{
    ConsumedApproved,
    ConsumedDenied,
    Expired,
    Stale,
    NotFound,
}

public sealed record ApprovalResolution(
    ApprovalOutcome Outcome)
{
    public static readonly ApprovalResolution NotFound =
        new(ApprovalOutcome.NotFound);
}

/// <summary>
/// Pure approval state machine: total function over (pending, command, expected binding, now).
/// One-time consumption; typed expiry; every binding change returns <see cref="ApprovalOutcome.Stale"/>.
/// </summary>
public static class ApprovalState
{
    public const int DefaultExpiryMinutes = 30;

    public static PendingToolApproval Issue(string approvalId, ToolApprovalBinding binding) =>
        PendingToolApproval.Issue(approvalId, binding);

    public static ApprovalResolution TryResolve(
        PendingToolApproval? pending,
        ApprovalCommand command,
        ToolApprovalBinding expectedBinding,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(expectedBinding);
        if (pending is null)
        {
            return ApprovalResolution.NotFound;
        }

        if (pending.Binding.ExpiresAt <= now)
        {
            return new ApprovalResolution(ApprovalOutcome.Expired);
        }

        if (!SameBinding(pending.Binding, expectedBinding))
        {
            return new ApprovalResolution(ApprovalOutcome.Stale);
        }

        if (!command.IsAllow)
        {
            return new ApprovalResolution(ApprovalOutcome.ConsumedDenied);
        }

        return new ApprovalResolution(ApprovalOutcome.ConsumedApproved);
    }

    private static bool SameBinding(ToolApprovalBinding left, ToolApprovalBinding right)
    {
        return left.RunId == right.RunId
            && left.ToolCallId == right.ToolCallId
            && string.Equals(left.ToolId, right.ToolId, StringComparison.Ordinal)
            && string.Equals(left.ToolVersion, right.ToolVersion, StringComparison.Ordinal)
            && string.Equals(left.CanonicalPath, right.CanonicalPath, StringComparison.Ordinal)
            && string.Equals(left.ResolvedExecutable, right.ResolvedExecutable, StringComparison.Ordinal)
            && left.Argv.SequenceEqual(right.Argv, StringComparer.Ordinal)
            && string.Equals(left.Cwd, right.Cwd, StringComparison.Ordinal)
            && string.Equals(left.OperationClass, right.OperationClass, StringComparison.Ordinal)
            && string.Equals(left.NetworkIntent, right.NetworkIntent, StringComparison.Ordinal)
            && left.SnapshotVersion == right.SnapshotVersion
            && string.Equals(left.IntentHash, right.IntentHash, StringComparison.Ordinal);
    }
}

public sealed record PendingToolApproval
{
    private PendingToolApproval(string approvalId, ToolApprovalBinding binding)
    {
        if (string.IsNullOrWhiteSpace(approvalId) || approvalId.Length > 128)
        {
            throw new ArgumentException("Approval ID must be bounded.", nameof(approvalId));
        }

        ArgumentNullException.ThrowIfNull(binding);

        ApprovalId = approvalId;
        Binding = binding;
    }

    public string ApprovalId { get; }

    public ToolApprovalBinding Binding { get; }

    public static PendingToolApproval Issue(string approvalId, ToolApprovalBinding binding) =>
        new(approvalId, binding);
}

/// <summary>
/// Owns pending approvals so every resolution is strictly one-time: approved, denied,
/// expired, and stale outcomes all remove the pending entry. A stale/expired entry can
/// only be answered again after a NEW approval is issued (re-evaluation), never by
/// retrying the old ID.
/// </summary>
public sealed class PendingApprovalLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingToolApproval> _pending = new(StringComparer.Ordinal);

    public PendingToolApproval Issue(string approvalId, ToolApprovalBinding binding)
    {
        var pending = PendingToolApproval.Issue(approvalId, binding);
        lock (_gate)
        {
            _pending.Add(approvalId, pending);
        }

        return pending;
    }

    public bool HasPending(string approvalId)
    {
        lock (_gate)
        {
            return _pending.ContainsKey(approvalId);
        }
    }

    public ApprovalResolution Resolve(
        string approvalId,
        ApprovalCommand command,
        ToolApprovalBinding expectedBinding,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(expectedBinding);
        PendingToolApproval? pending;
        lock (_gate)
        {
            if (!_pending.TryGetValue(approvalId, out pending))
            {
                return ApprovalResolution.NotFound;
            }

            // Consume first: exactly one resolution wins regardless of outcome.
            _pending.Remove(approvalId);
        }

        return ApprovalState.TryResolve(pending, command, expectedBinding, now);
    }
}
