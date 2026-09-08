using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;

namespace RuAgent.Broker.Policy;

public sealed record PendingApproval(
    string ApprovalId,
    AgentEnvelope Request,
    ToolManifest Manifest,
    JsonObject Arguments,
    string ArgumentsHash,
    string SafeAction,
    DateTimeOffset ExpiresAt,
    string? TerminalApprovalToken,
    bool Approved);

public enum ApprovalCommandV2
{
    AllowOnce,
    AllowSessionDirectory,
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
        string? BoundaryDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(RunId) || RunId.Length > 128) throw new ArgumentException("RunId bounded.", nameof(RunId));
        if (string.IsNullOrWhiteSpace(ToolCallId) || ToolCallId.Length > 128) throw new ArgumentException("ToolCallId bounded.", nameof(ToolCallId));
        if (string.IsNullOrWhiteSpace(ToolId) || ToolId.Length > 128) throw new ArgumentException("Tool ID bounded.", nameof(ToolId));
        if (string.IsNullOrWhiteSpace(ToolVersion) || ToolVersion.Length > 32) throw new ArgumentException("ToolVersion bounded.", nameof(ToolVersion));
        if (CanonicalPath is { Length: > 4096 }) throw new ArgumentException("CanonicalPath bounded.", nameof(CanonicalPath));
        if (ResolvedExecutable is { Length: > 256 }) throw new ArgumentException("Executable bounded.", nameof(ResolvedExecutable));
        ArgumentNullException.ThrowIfNull(Argv);
        if (Argv.Count > 64 || Argv.Any(a => a is { Length: > 4096 })) throw new ArgumentException("Argv bounded.", nameof(Argv));
        if (Cwd is { Length: > 4096 }) throw new ArgumentException("Cwd bounded.", nameof(Cwd));
        if (!AllowedOperations.Contains(OperationClass, StringComparer.Ordinal)) throw new ArgumentOutOfRangeException(nameof(OperationClass));
        if (!AllowedNetworkIntents.Contains(NetworkIntent, StringComparer.Ordinal)) throw new ArgumentOutOfRangeException(nameof(NetworkIntent));
        if (string.IsNullOrWhiteSpace(IntentHash) || IntentHash.Length > 128) throw new ArgumentException("IntentHash bounded.", nameof(IntentHash));
        if (ApprovalKind is not ("external_directory" or "terminal_risk")) throw new ArgumentOutOfRangeException(nameof(ApprovalKind));
        if (BoundaryDirectory is { Length: > 4096 }) throw new ArgumentException("BoundaryDirectory bounded.", nameof(BoundaryDirectory));

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
}

public sealed record V2ApprovalResolution(
    V2ApprovalOutcome Outcome);

public sealed record V2PendingApproval(
    string ApprovalId,
    V2ApprovalBinding Binding,
    DateTimeOffset ExpiresAt);

public sealed class PendingApprovalStore
{
    private readonly ConcurrentDictionary<string, PendingApproval> _pending = new(StringComparer.Ordinal);

    public string Issue(
        AgentEnvelope request,
        ToolManifest manifest,
        JsonObject arguments,
        string safeAction,
        DateTimeOffset expiresAt,
        string? terminalApprovalToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(safeAction);
        if (expiresAt <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt));
        }

        var approvalId = $"approval-{Guid.NewGuid():N}";
        var requestCopy = request with { Payload = request.Payload.DeepClone().AsObject() };
        var argumentsCopy = arguments.DeepClone().AsObject();
        var pending = new PendingApproval(
            approvalId,
            requestCopy,
            manifest,
            argumentsCopy,
            ComputeArgumentsHash(manifest.Id, manifest.Version, argumentsCopy),
            safeAction.Length <= 256 ? safeAction : safeAction[..256],
            expiresAt,
            terminalApprovalToken,
            false);
        _pending[approvalId] = pending;
        return approvalId;
    }

    public bool TryConsume(
        string approvalId,
        string sessionId,
        bool approved,
        DateTimeOffset now,
        out PendingApproval? pending)
    {
        pending = null;
        if (string.IsNullOrWhiteSpace(approvalId)
            || string.IsNullOrWhiteSpace(sessionId)
            || !_pending.TryGetValue(approvalId, out var candidate)
            || candidate.ExpiresAt <= now
            || !string.Equals(candidate.Request.SessionId, sessionId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(
                candidate.ArgumentsHash,
                ComputeArgumentsHash(candidate.Manifest.Id, candidate.Manifest.Version, candidate.Arguments),
                StringComparison.Ordinal))
        {
            return false;
        }

        if (!_pending.TryRemove(new KeyValuePair<string, PendingApproval>(approvalId, candidate)))
        {
            return false;
        }

        pending = candidate with { Approved = approved };
        return true;
    }

    public void RemoveSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        foreach (var pair in _pending)
        {
            if (string.Equals(pair.Value.Request.SessionId, sessionId, StringComparison.Ordinal))
            {
                _pending.TryRemove(pair.Key, out _);
            }
        }
    }

    // ── V2 approval ordering (tool.execute requires one-time approval bound to snapshot + intent hash) ──

    private readonly ConcurrentDictionary<string, V2PendingApproval> _v2 = new(StringComparer.Ordinal);

    public string IssueV2(V2ApprovalBinding binding, DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (expiresAt <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt));
        }

        var approvalId = $"apr_{Guid.NewGuid():N}";
        if (approvalId.Length > 128) approvalId = approvalId[..128];
        var pending = new V2PendingApproval(approvalId, binding, expiresAt);
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

        // One-time consumption: remove first, exactly one winner.
        if (!_v2.TryRemove(new KeyValuePair<string, V2PendingApproval>(approvalId, candidate)))
        {
            return new V2ApprovalResolution(V2ApprovalOutcome.NotFound);
        }

        if (candidate.ExpiresAt <= now)
        {
            return new V2ApprovalResolution(V2ApprovalOutcome.Expired);
        }

        if (!SameBindingV2(candidate.Binding, expectedBinding))
        {
            return new V2ApprovalResolution(V2ApprovalOutcome.Stale);
        }

        if (command == ApprovalCommandV2.Deny)
        {
            return new V2ApprovalResolution(V2ApprovalOutcome.ConsumedDenied);
        }

        // Only one-time tokens and session-directory grants exist. The Broker
        // never persists policy rules.
        return new V2ApprovalResolution(V2ApprovalOutcome.ConsumedApproved);
    }

    public void ClearV2() => _v2.Clear();

    public void ClearAll()
    {
        _pending.Clear();
        _v2.Clear();
    }

    // Keep Clear() for v1 compat but also clear v2 for test isolation (broker restart).
    public void ClearWithV2() => ClearAll();

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

    public void Clear()
    {
        _pending.Clear();
        _v2.Clear();
    }

    public static string ComputeArgumentsHash(
        string toolId,
        string toolVersion,
        JsonObject arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolVersion);
        ArgumentNullException.ThrowIfNull(arguments);
        var canonical = new JsonObject
        {
            ["tool_id"] = toolId,
            ["tool_version"] = toolVersion,
            ["arguments"] = Canonicalize(arguments),
        };
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToJsonString())))
            .ToLowerInvariant();
    }

    private static JsonNode? Canonicalize(JsonNode? node)
    {
        if (node is JsonObject objectNode)
        {
            var result = new JsonObject();
            foreach (var pair in objectNode.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                result[pair.Key] = Canonicalize(pair.Value);
            }

            return result;
        }

        if (node is JsonArray array)
        {
            var result = new JsonArray();
            foreach (var item in array)
            {
                result.Add(Canonicalize(item));
            }

            return result;
        }

        return node?.DeepClone();
    }
}
