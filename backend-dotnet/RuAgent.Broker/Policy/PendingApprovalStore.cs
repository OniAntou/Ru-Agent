using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;

namespace RuAgent.Broker.Policy;

public enum ApprovalCommandV2
{
    AllowOnce,
    AllowSessionDirectory,
    AllowAlways,
    Deny,
}

public enum V2ApprovalOutcome
{
    ConsumedApproved,
    ConsumedDenied,
    Expired,
    Stale,
    NotFound,
}

public sealed record V2ApprovalBinding
{
    private static readonly HashSet<string> AllowedOperations = new(StringComparer.Ordinal)
    {
        "read", "write", "edit", "delete", "list", "glob",
        "search", "patch", "restore", "discovery", "mutation", "terminal",
    };

    private static readonly HashSet<string> AllowedNetworkIntents = new(StringComparer.Ordinal)
    {
        "none", "declared", "egress", "unknown",
    };

    public V2ApprovalBinding(
        string RunId,
        string ToolCallId,
        string ToolId,
        string ToolVersion,
        string? CanonicalPath,
        string? ResolvedExecutable,
        IReadOnlyList<string> Argv,
        string? Cwd,
        string OperationClass,
        string NetworkIntent,
        long SnapshotVersion,
        string IntentHash,
        string ApprovalKind = "terminal_risk",
        string? BoundaryDirectory = null,
        string? SnapshotSha256 = null)
    {
        if (string.IsNullOrWhiteSpace(RunId) || RunId.Length > 128) throw new ArgumentException("RunId bounded.", nameof(RunId));
        if (string.IsNullOrWhiteSpace(ToolCallId) || ToolCallId.Length > 128) throw new ArgumentException("ToolCallId bounded.", nameof(ToolCallId));
        if (string.IsNullOrWhiteSpace(ToolId) || ToolId.Length > 128) throw new ArgumentException("Tool ID bounded.", nameof(ToolId));
        if (string.IsNullOrWhiteSpace(ToolVersion) || ToolVersion.Length > 32) throw new ArgumentException("ToolVersion bounded.", nameof(ToolVersion));
        if (CanonicalPath is { Length: > 4096 }) throw new ArgumentException("CanonicalPath bounded.", nameof(CanonicalPath));
        if (ResolvedExecutable is { Length: > TerminalLimits.MaxCanonicalExecutableChars }) throw new ArgumentException("Executable bounded.", nameof(ResolvedExecutable));
        ArgumentNullException.ThrowIfNull(Argv);
        if (Argv.Count > 64 || Argv.Any(a => a is { Length: > 4096 })) throw new ArgumentException("Argv bounded.", nameof(Argv));
        if (Cwd is { Length: > 4096 }) throw new ArgumentException("Cwd bounded.", nameof(Cwd));
        if (!AllowedOperations.Contains(OperationClass, StringComparer.Ordinal)) throw new ArgumentOutOfRangeException(nameof(OperationClass));
        if (!AllowedNetworkIntents.Contains(NetworkIntent, StringComparer.Ordinal)) throw new ArgumentOutOfRangeException(nameof(NetworkIntent));
        if (string.IsNullOrWhiteSpace(IntentHash) || IntentHash.Length > 128) throw new ArgumentException("IntentHash bounded.", nameof(IntentHash));
        if (ApprovalKind is not ("external_directory" or "terminal_risk" or "subagent_apply")) throw new ArgumentOutOfRangeException(nameof(ApprovalKind));
        if (BoundaryDirectory is { Length: > 4096 }) throw new ArgumentException("BoundaryDirectory bounded.", nameof(BoundaryDirectory));
        if (SnapshotSha256 is { Length: > 128 }) throw new ArgumentException("SnapshotSha256 bounded.", nameof(SnapshotSha256));

        this.RunId = RunId;
        this.ToolCallId = ToolCallId;
        this.ToolId = ToolId;
        this.ToolVersion = ToolVersion;
        this.CanonicalPath = CanonicalPath;
        this.ResolvedExecutable = ResolvedExecutable;
        this.Argv = Argv.ToArray();
        this.Cwd = Cwd;
        this.OperationClass = OperationClass;
        this.NetworkIntent = NetworkIntent;
        this.SnapshotVersion = SnapshotVersion;
        this.IntentHash = IntentHash;
        this.ApprovalKind = ApprovalKind;
        this.BoundaryDirectory = BoundaryDirectory;
        this.SnapshotSha256 = SnapshotSha256;
    }

    public string RunId { get; init; }
    public string ToolCallId { get; init; }
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
    public string ApprovalKind { get; init; } = "terminal_risk";
    public string? BoundaryDirectory { get; init; }
    /// <summary>
    /// Canonical hash of the snapshot the approval was issued under. Session
    /// approval memory derives snapshot versions, so a version alone can repeat
    /// for a different rule set; null only for legacy bindings without a lease.
    /// </summary>
    public string? SnapshotSha256 { get; init; }
}

public sealed record V2ApprovalResolution(
    V2ApprovalOutcome Outcome,
    V2PendingApproval? Consumed = null);

/// <summary>
/// A pending v2 approval. <see cref="Context"/> and <see cref="AskSafeCode"/> are the Broker's own
/// evaluation of the call; the approval's remember plan is built from them, never from re-parsed arguments.
/// </summary>
public sealed record V2PendingApproval(
    string ApprovalId,
    V2ApprovalBinding Binding,
    DateTimeOffset ExpiresAt,
    PolicyContext? Context = null,
    string? AskSafeCode = null);

public sealed class PendingApprovalStore
{
    /// <summary>
    /// Forgets approvals whose 30-minute window elapsed without a response, so
    /// an abandoned approval does not keep its binding in memory for the life
    /// of the session.
    /// </summary>
    private void DropExpiredApprovals()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _v2)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _v2.TryRemove(new KeyValuePair<string, V2PendingApproval>(pair.Key, pair.Value));
            }
        }
    }

    // ── V2 approval ordering (tool.execute requires one-time approval bound to snapshot + intent hash) ──

    private readonly ConcurrentDictionary<string, V2PendingApproval> _v2 = new(StringComparer.Ordinal);

    public string IssueV2(
        V2ApprovalBinding binding,
        DateTimeOffset expiresAt,
        PolicyContext? context = null,
        string? askSafeCode = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (expiresAt <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt));
        }

        var approvalId = $"apr_{Guid.NewGuid():N}";
        if (approvalId.Length > 128) approvalId = approvalId[..128];
        var pending = new V2PendingApproval(approvalId, binding, expiresAt, context, askSafeCode);
        DropExpiredApprovals();
        _v2[approvalId] = pending;
        return approvalId;
    }

    public bool TryPeekV2(string approvalId, out V2PendingApproval? pending)
    {
        pending = null;
        if (string.IsNullOrWhiteSpace(approvalId)) return false;
        return _v2.TryGetValue(approvalId, out pending);
    }

    public V2ApprovalResolution TryConsumeV2(string approvalId, ApprovalCommandV2 command, V2ApprovalBinding expectedBinding, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(expectedBinding);
        if (string.IsNullOrWhiteSpace(approvalId))
        {
            return new V2ApprovalResolution(V2ApprovalOutcome.NotFound);
        }

        if (!_v2.TryGetValue(approvalId, out var candidate))
        {
            return new V2ApprovalResolution(V2ApprovalOutcome.NotFound);
        }

        if (candidate.ExpiresAt <= now)
        {
            _v2.TryRemove(new KeyValuePair<string, V2PendingApproval>(approvalId, candidate));
            return new V2ApprovalResolution(V2ApprovalOutcome.Expired);
        }

        // A response that does not match the issued binding must not consume it:
        // the identifier is opaque, and a stray or replayed mismatch would
        // otherwise destroy the pending approval the real caller still needs.
        // Binding and expiry are validated while the entry is still present, then
        // the conditional remove below decides the single winner atomically.
        if (!SameBindingV2(candidate.Binding, expectedBinding))
        {
            return new V2ApprovalResolution(V2ApprovalOutcome.Stale);
        }

        // One-time consumption: remove first, exactly one winner.
        if (!_v2.TryRemove(new KeyValuePair<string, V2PendingApproval>(approvalId, candidate)))
        {
            return new V2ApprovalResolution(V2ApprovalOutcome.NotFound);
        }

        if (command == ApprovalCommandV2.Deny)
        {
            return new V2ApprovalResolution(V2ApprovalOutcome.ConsumedDenied);
        }

        // Only one-time tokens and session-directory grants exist. The Broker
        // never persists policy rules. The consumed entry carries the context the
        // remember plan of this approval is built from.
        return new V2ApprovalResolution(V2ApprovalOutcome.ConsumedApproved, candidate);
    }

    /// <summary>
    /// Forgets every pending v2 approval bound to one run. Called when a session
    /// closes so a closed session cannot leave a consumable approval behind.
    /// </summary>
    public void RemoveRun(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        foreach (var pair in _v2)
        {
            if (string.Equals(pair.Value.Binding.RunId, runId, StringComparison.Ordinal))
            {
                _v2.TryRemove(pair.Key, out _);
            }
        }
    }

    private static bool SameBindingV2(V2ApprovalBinding left, V2ApprovalBinding right)
    {
        return string.Equals(left.RunId, right.RunId, StringComparison.Ordinal)
            && string.Equals(left.ToolCallId, right.ToolCallId, StringComparison.Ordinal)
            && string.Equals(left.ToolId, right.ToolId, StringComparison.Ordinal)
            && string.Equals(left.ToolVersion, right.ToolVersion, StringComparison.Ordinal)
            && string.Equals(left.CanonicalPath, right.CanonicalPath, StringComparison.Ordinal)
            && string.Equals(left.ResolvedExecutable, right.ResolvedExecutable, StringComparison.Ordinal)
            && left.Argv.SequenceEqual(right.Argv, StringComparer.Ordinal)
            && string.Equals(left.Cwd, right.Cwd, StringComparison.Ordinal)
            && string.Equals(left.OperationClass, right.OperationClass, StringComparison.Ordinal)
            && string.Equals(left.NetworkIntent, right.NetworkIntent, StringComparison.Ordinal)
            && left.SnapshotVersion == right.SnapshotVersion
            && string.Equals(left.SnapshotSha256, right.SnapshotSha256, StringComparison.Ordinal)
            && string.Equals(left.IntentHash, right.IntentHash, StringComparison.Ordinal)
            && string.Equals(left.ApprovalKind, right.ApprovalKind, StringComparison.Ordinal)
            && string.Equals(left.BoundaryDirectory, right.BoundaryDirectory, StringComparison.Ordinal);
    }

    public static System.Text.Json.Nodes.JsonObject BuildMatcherPreview(V2ApprovalBinding binding)
    {
        string Sanitize(string? value) => value is null ? string.Empty : value.Replace("*", string.Empty).Replace("?", string.Empty);
        return new System.Text.Json.Nodes.JsonObject
        {
            ["tool_id"] = binding.ToolId,
            ["tool_version"] = binding.ToolVersion,
            ["canonical_path"] = binding.CanonicalPath is null ? null : (System.Text.Json.Nodes.JsonNode?)Sanitize(binding.CanonicalPath),
            ["resolved_executable"] = binding.ResolvedExecutable is null ? null : (System.Text.Json.Nodes.JsonNode?)Sanitize(binding.ResolvedExecutable),
            ["argv_pattern"] = new System.Text.Json.Nodes.JsonArray(binding.Argv.Select(Sanitize).Select(x => (System.Text.Json.Nodes.JsonNode?)x).ToArray()),
            ["cwd_pattern"] = binding.Cwd is null ? null : (System.Text.Json.Nodes.JsonNode?)Sanitize(binding.Cwd),
            ["operation_class"] = binding.OperationClass,
            ["network_intent"] = binding.NetworkIntent,
        };
    }

    public void Clear() => _v2.Clear();
}
