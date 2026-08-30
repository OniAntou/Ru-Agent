using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Protocol;
using RuBackend.Harness;

namespace RuBackend.Harness.Broker;

public sealed class HarnessToolGateway : IToolGateway, IToolRunCancellation
{
    private const int MaxFilePathLength = 4096;
    private const int MaxPatternLength = 512;
    private const int MaxQueryLength = 1024;
    private const int MaxDiscoveryEntries = 512;
    private const int MaxDiscoveryResults = 64;
    private const int MaxReadBytes = 256 * 1024;
    private const int MaxWebOutputBytes = 512 * 1024;
    private const int MaxMutationBytes = 4 * 1024 * 1024;
    private const int MaxMutationFileBytes = 4 * 1024 * 1024;
    private const int MaxExpectedOccurrences = 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly string _canonicalRoot;
    private readonly IReadOnlySet<string> _advertisedCapabilities;
    private readonly Func<RunId, AgentPermissionPreset> _presetProvider;
    private readonly PolicySnapshot? _policySnapshot;
    private readonly IIdGenerator _ids;
    private readonly IReadOnlyList<string> _sanitizedSearchDirectories;
    private readonly ConcurrentDictionary<string, ActiveTerminalExecution> _activeTerminalExecutions = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeWebExecutions = new();
    private readonly ConcurrentDictionary<string, PreparedTerminalSpec> _pendingTerminalSpecs = new();
    private readonly ConcurrentDictionary<string, string> _pendingArguments = new();
    private readonly ConcurrentDictionary<string, string> _pendingToolIds = new();
    private readonly ConcurrentDictionary<string, string> _pendingExpectedHashes = new();
    private readonly ConcurrentDictionary<string, string> _pendingResolvedPaths = new();
    private readonly ConcurrentDictionary<string, RunId> _pendingRunIds = new();
    private readonly ConcurrentDictionary<string, byte[]> _hostRecoveryBytes = new();
    private readonly ConcurrentDictionary<string, string> _hostRecoveryPath = new();
    private readonly ConcurrentDictionary<string, string> _hostRecoveryHash = new();
    private readonly ConcurrentDictionary<string, RunId> _hostRecoveryOwners = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _hostRecoveryCreatedAt = new();
    private readonly ConcurrentDictionary<string, bool> _hostConsumed = new();
    private static readonly TimeSpan HostRecoveryTtl = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, byte> _cancelledRuns = new();
    private readonly ConcurrentDictionary<string, byte> _cancelledExecutions = new();
    private readonly AgentWebToolExecutor? _webExecutor;
    private readonly object _mutationGate = new();
    private readonly RuBackend.Harness.Reconciliation.IAgentApprovalGrants? _approvalGrants;
    private const int MaxHostRecoveries = 4096;

    public HarnessToolGateway(string canonicalRoot, IReadOnlyList<string> advertisedCapabilities)
        : this(canonicalRoot, advertisedCapabilities, presetProvider: null, idGenerator: null)
    {
    }

    public HarnessToolGateway(
        string canonicalRoot,
        IReadOnlyList<string> advertisedCapabilities,
        Func<RunId, AgentPermissionPreset>? presetProvider,
        IIdGenerator? idGenerator,
        RuBackend.Harness.Reconciliation.IAgentApprovalGrants? approvalGrants = null,
        PolicySnapshot? policySnapshot = null,
        AgentWebToolExecutor? webExecutor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);
        ArgumentNullException.ThrowIfNull(advertisedCapabilities);
        var lexicalRoot = Path.GetFullPath(canonicalRoot);
        if (!Directory.Exists(lexicalRoot))
        {
            throw new DirectoryNotFoundException(lexicalRoot);
        }

        _canonicalRoot = CanonicalizeExistingDirectory(lexicalRoot);

        _advertisedCapabilities = advertisedCapabilities.ToHashSet(StringComparer.Ordinal);
        _presetProvider = presetProvider ?? (_ => AgentPermissionPreset.Standard);
        _policySnapshot = policySnapshot;
        _ids = idGenerator ?? new GatewayIdGenerator();
        _approvalGrants = approvalGrants;
        _webExecutor = webExecutor;
        _sanitizedSearchDirectories = SanitizeSearchDirectories();
    }

    // For testing without filesystem, allow injection.
    internal HarnessToolGateway(string canonicalRoot, IReadOnlySet<string> capabilities)
        : this(canonicalRoot, capabilities.ToList(), presetProvider: null, idGenerator: null)
    {
    }

    internal int ActiveTerminalExecutionCount => _activeTerminalExecutions.Count;

    public Task<ToolProposalDecision> EvaluateAsync(ToolProposal proposal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_advertisedCapabilities.Contains(proposal.ToolId))
        {
            return Task.FromResult(ToolProposalDecision.Deny(ToolAccessKind.ReadOnly, "phase_not_available"));
        }

        // All file tools are now advertised via capabilities; validate known IDs
        var allowedIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "files.list", "files.glob", "files.search", "files.read",
            "files.write", "files.edit", "files.delete", "files.restore",
            "terminal.run", "terminal.shell", "web.search", "web.fetch"
        };
        if (!allowedIds.Contains(proposal.ToolId) && proposal.ToolId != "notes.create" && proposal.ToolId != "system.time" && proposal.ToolId != "system.status" && proposal.ToolId != "system.volume" && proposal.ToolId != "app.list")
        {
            return Task.FromResult(ToolProposalDecision.Deny(MapAccessKind(proposal.ToolId), "phase_not_available"));
        }

        if (proposal.ToolId is ("web.search" or "web.fetch")
            && (_webExecutor is null || !_webExecutor.IsEnabled))
        {
            return Task.FromResult(ToolProposalDecision.Deny(ToolAccessKind.ReadOnly, "web_disabled"));
        }

        // For restore, advertised only while active run owns valid recovery â€“ check in-memory host registry
        if (proposal.ToolId == "files.restore")
        {
            // If no active recovery for this execution's run, still allow evaluation to proceed; policy will handle ask/deny.
            // For gateway tracer, we allow if capability advertised; actual availability is checked at execution.
        }

        try
        {
            ValidateArguments(proposal);
        }
        catch (ArgumentException)
        {
            return Task.FromResult(ToolProposalDecision.Deny(MapAccessKind(proposal.ToolId), "invalid_arguments"));
        }

        if (proposal.ToolId is "terminal.run" or "terminal.shell")
        {
            return Task.FromResult(EvaluateTerminal(proposal));
        }

        return Task.FromResult(EvaluateNonTerminal(proposal));
    }

    private ToolProposalDecision EvaluateNonTerminal(ToolProposal proposal)
    {
        var accessKind = MapAccessKind(proposal.ToolId);
        var target = TryGetTarget(proposal);
        // Path canonicalization is a hard safety boundary, not a policy rule.
        // Reject an explicitly supplied path that could not be resolved before a
        // broad allow rule (or Full Access preset) gets a chance to match it.
        if (target is null && (RequiresPath(proposal.ToolId) || HasExplicitPath(proposal)))
        {
            return ToolProposalDecision.Deny(accessKind, "invalid_arguments");
        }

        var operationClass = OperationClassForTool(proposal.ToolId);
        var relativePath = target is null
            ? null
            : Path.GetRelativePath(_canonicalRoot, target).Replace(Path.DirectorySeparatorChar, '/');

        var matchedRule = FindMatchingRule(proposal, operationClass, relativePath, canonicalPath: target);
        if (matchedRule is not null)
        {
            // A policy deny is authoritative, including for protected paths. An
            // allow rule may refine a normal request, but it cannot turn a
            // protected target into an implicit allow; that still requires the
            // one-time approval bridge.
            if (target is not null && IsProtectedPath(target))
            {
                return matchedRule.Decision == PolicyDecision.Deny
                    ? ToolProposalDecision.Deny(accessKind, "policy_denied")
                    : AskOrAllowWithGrant(proposal, accessKind, "protected_path");
            }

            return DecisionFromPolicy(proposal, accessKind, matchedRule.Decision, "rule_matched");
        }

        // Protected paths remain behind an explicit approval boundary even when a
        // broad Full Access fallback is present.
        if (target is not null && IsProtectedPath(target))
        {
            return AskOrAllowWithGrant(proposal, accessKind, "protected_path");
        }

        // The host gateway is rooted to the trusted workspace. Relative traversal
        // cannot escape it; an explicit absolute target is a deliberate request
        // and follows the selected policy preset/approval. An approved external
        // directory is a run-scoped boundary: it covers descendants, while the
        // operation-specific rules below still apply (for example delete remains
        // an approval action even after a directory grant).
        var preset = _policySnapshot?.Preset ?? _presetProvider(proposal.RunId);
        var outsideWorkspace = target is not null && !IsInsideWorkspacePath(target);
        var externalDirectoryGranted = outsideWorkspace
            && _approvalGrants is { } externalGrants
            && externalGrants.IsExternalDirectoryGranted(proposal.RunId.Value, target!);
        if (outsideWorkspace && !externalDirectoryGranted)
        {
            if (preset == AgentPermissionPreset.FullAccess)
            {
                return Allow(proposal, accessKind);
            }

            return AskOrAllowWithGrant(proposal, accessKind, "workspace_path_outside_root");
        }

        if (preset == AgentPermissionPreset.FullAccess)
        {
            return Allow(proposal, accessKind);
        }

        return proposal.ToolId switch
        {
            "files.delete" or "files.restore" =>
                AskOrAllowWithGrant(proposal, accessKind, proposal.ToolId == "files.delete"
                    ? "delete_requires_approval"
                    : "restore_once"),
            "files.list" or "files.glob" or "files.search" or "files.read"
                or "files.write" or "files.edit" =>
                Allow(proposal, accessKind),
            "web.search" or "web.fetch" => Allow(proposal, accessKind),
            _ => ToolProposalDecision.Deny(accessKind, "phase_not_available"),
        };
    }

    private ToolProposalDecision DecisionFromPolicy(
        ToolProposal proposal,
        ToolAccessKind accessKind,
        PolicyDecision decision,
        string reason)
    {
        return decision switch
        {
            PolicyDecision.Allow => Allow(proposal, accessKind),
            PolicyDecision.Deny => ToolProposalDecision.Deny(accessKind, "policy_denied"),
            PolicyDecision.Ask => AskOrAllowWithGrant(proposal, accessKind, reason),
            _ => ToolProposalDecision.Deny(accessKind, "policy_invalid"),
        };
    }

    private ToolProposalDecision AskOrAllowWithGrant(
        ToolProposal proposal,
        ToolAccessKind accessKind,
        string reason)
    {
        if (_approvalGrants is { } grants
            && grants.TryConsume(proposal.RunId.Value, ComputeApprovalIntentHash(proposal)))
        {
            return Allow(proposal, accessKind);
        }

        var safeCode = reason is "workspace_path_outside_root" or "cwd_outside_workspace"
            ? "external_directory_required"
            : "approval_required";
        return ToolProposalDecision.Ask(accessKind, _ids.NewApprovalId(), safeCode);
    }

    private ToolProposalDecision Allow(ToolProposal proposal, ToolAccessKind accessKind) =>
        ToolProposalDecision.Allow(accessKind, ComputeCanonicalToken(proposal));

    private PolicyRule? FindMatchingRule(
        ToolProposal proposal,
        string operationClass,
        string? relativePath,
        string? canonicalPath = null,
        string? resolvedExecutable = null,
        IReadOnlyList<string>? argv = null,
        string? canonicalCwd = null,
        string networkIntent = "none")
    {
        if (_policySnapshot is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        PolicyRule? matched = null;
        foreach (var rule in _policySnapshot.OrderedRules)
        {
            if (rule.IsExpired(now)
                || !string.Equals(rule.Matcher.ToolId, proposal.ToolId, StringComparison.Ordinal) && rule.Matcher.ToolId is not null
                || !string.Equals(rule.Matcher.ToolVersion, proposal.ToolVersion, StringComparison.Ordinal) && rule.Matcher.ToolVersion is not null
                || rule.Matcher.OperationClass is not null && !string.Equals(rule.Matcher.OperationClass, operationClass, StringComparison.Ordinal)
                || rule.Matcher.NetworkIntent is not null && !string.Equals(rule.Matcher.NetworkIntent, networkIntent, StringComparison.Ordinal)
                || rule.Matcher.ResolvedExecutable is not null
                    && !ExecutableMatches(rule.Matcher.ResolvedExecutable, resolvedExecutable)
                || rule.Matcher.CwdPattern is not null
                    && (canonicalCwd is null
                        || !PathPatternMatches(rule.Matcher.CwdPattern, relativePath: null, canonicalPath: canonicalCwd))
                || rule.Matcher.ArgvPattern is not null
                    && !ArgvMatches(rule.Matcher.ArgvPattern, argv ?? Array.Empty<string>()))
            {
                continue;
            }

            var pathPattern = rule.Matcher.CanonicalPathPattern;
            if (pathPattern is not null
                && !PathPatternMatches(pathPattern, relativePath, canonicalPath))
            {
                continue;
            }

            matched = rule;
        }

        return matched;
    }

    private bool PathPatternMatches(
        string pattern,
        string? relativePath,
        string? canonicalPath)
    {
        if (relativePath is not null
            && (GlobMatches(pattern, relativePath)
                || GlobMatches(
                    pattern,
                    Path.Combine(_canonicalRoot, relativePath).Replace(Path.DirectorySeparatorChar, '/'))))
        {
            return true;
        }

        if (canonicalPath is null)
        {
            return false;
        }

        var normalizedPattern = pattern.Replace('\\', '/');
        var normalizedCandidate = canonicalPath.Replace('\\', '/');
        if (GlobMatches(normalizedPattern, normalizedCandidate))
        {
            return true;
        }

        // A persisted external-directory rule uses `<dir>/**`. Treat the
        // directory itself as part of that scope as well as descendants; the
        // generic glob expression otherwise requires a separator after `/**`.
        if (normalizedPattern.EndsWith("/**", StringComparison.Ordinal))
        {
            var directory = normalizedPattern[..^3].TrimEnd('/');
            return string.Equals(directory, normalizedCandidate, StringComparison.OrdinalIgnoreCase)
                || normalizedCandidate.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool ExecutableMatches(string pattern, string? resolvedExecutable)
    {
        if (resolvedExecutable is null)
        {
            return false;
        }

        return string.Equals(pattern, resolvedExecutable, StringComparison.OrdinalIgnoreCase)
            || string.Equals(pattern, Path.GetFileName(resolvedExecutable), StringComparison.OrdinalIgnoreCase);
    }

    private static bool ArgvMatches(IReadOnlyList<string> pattern, IReadOnlyList<string> argv)
    {
        if (pattern.Count > argv.Count)
        {
            return false;
        }

        for (var index = 0; index < pattern.Count; index++)
        {
            if (!GlobMatches(pattern[index], argv[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static string OperationClassForTool(string toolId) => toolId switch
    {
        "files.list" => "list",
        "files.glob" => "glob",
        "files.search" => "search",
        "files.read" => "read",
        "files.write" => "write",
        "files.edit" => "edit",
        "files.apply_patch" => "patch",
        "files.delete" => "delete",
        "files.restore" => "restore",
        "web.search" or "web.fetch" => "read",
        _ => "mutation",
    };

    private static bool RequiresPath(string toolId) => toolId is
        "files.read" or "files.write" or "files.edit" or "files.apply_patch"
        or "files.delete" or "files.restore";

    private static bool HasExplicitPath(ToolProposal proposal) =>
        proposal.Arguments.TryGetProperty("path", out var path)
        && path.ValueKind is not JsonValueKind.Null;

    private string? TryGetTarget(ToolProposal proposal)
    {
        var acceptsPath = RequiresPath(proposal.ToolId)
            || proposal.ToolId is "files.list" or "files.glob" or "files.search";
        if (!acceptsPath)
        {
            return null;
        }

        if (!proposal.Arguments.TryGetProperty("path", out var path)
            || path.ValueKind != JsonValueKind.String)
        {
            return _canonicalRoot;
        }

        var input = path.GetString();
        if (string.IsNullOrWhiteSpace(input) || input.Contains('\0'))
        {
            return null;
        }

        try
        {
            // Resolve the final filesystem identity before making a policy decision;
            // a lexical prefix check alone treats a junction/symlink outside the
            // workspace as if it were an in-root file.
            return ResolveSafeWorkspacePath(
                input,
                allowMissingLeaf: true,
                allowAbsoluteOutside: Path.IsPathRooted(input));
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private bool IsProtectedPath(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var normalized = Path.GetFullPath(candidate);
        var segments = normalized.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment =>
                string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase)
                || segment.StartsWith(".env", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Keep the Host gateway's protected catalog aligned with the Broker
        // resolver. These paths contain durable state or credentials and must
        // never become writable merely because a broad policy rule matched.
        var lower = normalized.ToLowerInvariant();
        return lower.EndsWith("agent_harness.db", StringComparison.Ordinal)
            || lower.EndsWith("harness_key.dat", StringComparison.Ordinal)
            || lower.EndsWith("runtime_descriptor.json", StringComparison.Ordinal)
            || lower.EndsWith("broker.json", StringComparison.Ordinal)
            || lower.Contains("agent_harness", StringComparison.Ordinal)
            || lower.Contains("harness_key", StringComparison.Ordinal)
            || lower.Contains("credentials", StringComparison.Ordinal)
            || lower.Contains("credential", StringComparison.Ordinal);
    }

    private bool IsInsideWorkspacePath(string candidate)
    {
        var root = _canonicalRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _canonicalRoot
            : _canonicalRoot + Path.DirectorySeparatorChar;
        return string.Equals(candidate, _canonicalRoot, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool GlobMatches(string pattern, string candidate)
    {
        var escaped = Regex.Escape(pattern.Replace('\\', '/'))
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", "[^/]");
        return Regex.IsMatch(candidate.Replace('\\', '/'), "^(?:" + escaped + ")$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public Task<PreparedToolExecution> PrepareAsync(ApprovedToolExecution execution, CancellationToken cancellationToken)
    {
        try
        {
            return PrepareCoreAsync(execution, cancellationToken);
        }
        catch
        {
            if (execution is not null)
            {
                CleanupExecutionState(execution.ExecutionId);
            }

            throw;
        }
    }

    private Task<PreparedToolExecution> PrepareCoreAsync(ApprovedToolExecution execution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        cancellationToken.ThrowIfCancellationRequested();
        if (_cancelledRuns.ContainsKey(execution.Proposal.RunId.Value))
        {
            throw new OperationCanceledException(cancellationToken);
        }
        var expected = ComputeCanonicalToken(execution.Proposal);
        if (!string.Equals(expected, execution.CanonicalIntentToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("canonical_token_mismatch");
        }

        if (!_advertisedCapabilities.Contains(execution.Proposal.ToolId))
        {
            throw new InvalidOperationException("capability_not_advertised");
        }

        // Prepare is an authorization boundary too. Do not rely on EvaluateAsync
        // having run first: callers can forge an ApprovedToolExecution in-process.
        ValidateArguments(execution.Proposal);

        var preparationToken = $"prepared-{execution.CanonicalIntentToken}";
        StoreArguments(execution.ExecutionId, execution.Proposal.Arguments.GetRawText());
        _pendingToolIds[execution.ExecutionId.Value] = execution.Proposal.ToolId;
        _pendingRunIds[execution.ExecutionId.Value] = execution.Proposal.RunId;

        if (execution.Proposal.ToolId is "terminal.run" or "terminal.shell")
        {
            return Task.FromResult(PrepareTerminal(execution));
        }

        // Generic preparation payload handling: if tool is files.delete, read at most recovery cap and return RecoveryOpaque intent
        if (execution.Proposal.ToolId == "files.delete")
        {
            cancellationToken.ThrowIfCancellationRequested();
            var args = JsonNode.Parse(execution.Proposal.Arguments.GetRawText()) as JsonObject ?? new JsonObject();
            var path = args["path"]?.GetValue<string>() ?? throw new ArgumentException("missing_path");
            var expectedSha = args["expected_sha256"]?.GetValue<string>();

            // Resolve within workspace and validate
            var resolved = ResolveWithinRootForPrepare(path);
            var info = new FileInfo(resolved);
            if (!info.Exists)
            {
                throw new FileNotFoundException("file_not_found");
            }

            if (Directory.Exists(resolved))
            {
                throw new ArgumentException("not_a_file");
            }

            if (info.Length > 1 * 1024 * 1024)
            {
                throw new ArgumentException("recovery_too_large");
            }

            // Validate hash without allocating unbounded: compute via streaming
            var currentHash = ComputeFileHash(resolved);
            expectedSha ??= currentHash;
            if (!string.Equals(currentHash, expectedSha, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("workspace_changed");
            }

            // Read byte-exact, bounded. FileInfo.Length is only a snapshot and
            // must not be used to size an allocation after a concurrent growth.
            var bytes = ReadBoundedBytes(resolved, 1 * 1024 * 1024, "recovery_too_large");

            // Prepare recovery payload â€“ byte-exact, opaque, internal only
            var recoveryPayload = new PreparedPayload(
                HarnessPayloadKind.RecoveryOpaque,
                HarnessPayloadAudience.InternalOnly,
                HarnessPayloadEncoding.Opaque,
                Math.Max(bytes.Length, 1),
                bytes);

            // Store for execution-time re-resolution validation
            _pendingDeleteBytes[execution.ExecutionId.Value] = bytes;
            _pendingDeletePath[execution.ExecutionId.Value] = resolved;
            _pendingDeleteHash[execution.ExecutionId.Value] = expectedSha;
            _pendingExpectedHashes[execution.ExecutionId.Value] = expectedSha;

            if (_cancelledRuns.ContainsKey(execution.Proposal.RunId.Value))
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return Task.FromResult(new PreparedToolExecution(
                execution.ExecutionId,
                execution.Proposal.ToolCallId,
                execution.Proposal.ModelOrder,
                ToolAccessKind.Mutation,
                preparationToken,
                new[] { recoveryPayload }));
        }

        if (execution.Proposal.ToolId == "files.restore")
        {
            var args = JsonNode.Parse(execution.Proposal.Arguments.GetRawText()) as JsonObject ?? new JsonObject();
            _ = args["recovery_id"]?.GetValue<string>() ?? throw new ArgumentException("missing_recovery_id");
            var access = ToolAccessKind.Mutation;
            return Task.FromResult(new PreparedToolExecution(
                execution.ExecutionId,
                execution.Proposal.ToolCallId,
                execution.Proposal.ModelOrder,
                access,
                preparationToken,
                Array.Empty<PreparedPayload>()));
        }

        if (execution.Proposal.ToolId is "files.write" or "files.edit")
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Capture the current hash when the model omitted it. This keeps the
            // contract usable while still binding the mutation to the file state
            // observed during preparation.
            if (execution.Proposal.ToolId is "files.write" or "files.edit")
            {
                var args = JsonNode.Parse(execution.Proposal.Arguments.GetRawText()) as JsonObject
                    ?? throw new ArgumentException("invalid_arguments");
                var path = args["path"]?.GetValue<string>()
                    ?? throw new ArgumentException("missing_path");
                var resolved = ResolveWithinRootForPrepare(path);
                if (Directory.Exists(resolved))
                {
                    throw new ArgumentException("not_a_file");
                }
                if (execution.Proposal.ToolId == "files.write"
                    && File.Exists(resolved)
                    && !(args["overwrite"]?.GetValue<bool>() ?? false))
                {
                    throw new IOException("file_exists");
                }
                var expectedHash = args["expected_sha256"]?.GetValue<string>() ?? ComputeFileHash(resolved);
                _pendingExpectedHashes[execution.ExecutionId.Value] = expectedHash;
                _pendingResolvedPaths[execution.ExecutionId.Value] = resolved;
            }

            // For write/edit/patch, no recovery payload, but still mutation.
            return Task.FromResult(new PreparedToolExecution(
                execution.ExecutionId,
                execution.Proposal.ToolCallId,
                execution.Proposal.ModelOrder,
                ToolAccessKind.Mutation,
                preparationToken,
                Array.Empty<PreparedPayload>()));
        }

        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new PreparedToolExecution(
            execution.ExecutionId,
            execution.Proposal.ToolCallId,
            execution.Proposal.ModelOrder,
            ToolAccessKind.ReadOnly,
            preparationToken,
            Array.Empty<PreparedPayload>()));
    }

    public async Task<ToolExecutionReceipt> ExecuteAsync(PreparedToolExecution execution, Func<ToolOutputChunk, ValueTask> publishDelta, CancellationToken cancellationToken)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(execution);
            return await ExecuteCoreAsync(execution, publishDelta, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (execution is not null && _cancelledExecutions.ContainsKey(execution.ExecutionId.Value))
        {
            // A run-level cancellation is represented as a durable receipt so the
            // coordinator can record it and, for mutations, enter reconciliation.
            // Caller-token cancellation still propagates normally.
            return new ToolExecutionReceipt(
                execution.ExecutionId,
                execution.ToolCallId,
                ToolExecutionOutcome.Cancelled,
                "process_cancelled",
                null,
                sideEffectsPossible: execution.AccessKind != ToolAccessKind.ReadOnly);
        }
        finally
        {
            if (execution is not null)
            {
                CleanupExecutionState(execution.ExecutionId);
                _cancelledExecutions.TryRemove(execution.ExecutionId.Value, out _);
            }
        }
    }

    private async Task<ToolExecutionReceipt> ExecuteCoreAsync(PreparedToolExecution execution, Func<ToolOutputChunk, ValueTask> publishDelta, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(publishDelta);

        if (_cancelledExecutions.ContainsKey(execution.ExecutionId.Value))
        {
            return new ToolExecutionReceipt(
                execution.ExecutionId,
                execution.ToolCallId,
                ToolExecutionOutcome.Cancelled,
                "cancelled",
                null,
                sideEffectsPossible: false);
        }

        if (!_pendingToolIds.TryGetValue(execution.ExecutionId.Value, out var toolId)
            || !_pendingArguments.TryGetValue(execution.ExecutionId.Value, out var argsJson))
        {
            return FailedReceipt(execution, "decision_stale");
        }

        ThrowIfExecutionCancelled(execution.ExecutionId, cancellationToken);
        PruneHostRecoveries(DateTimeOffset.UtcNow);

        JsonObject args;
        try
        {
            args = JsonNode.Parse(argsJson) as JsonObject ?? new JsonObject();
        }
        catch
        {
            args = new JsonObject();
        }

        // Handle delete with execution-time re-resolution and opaque recovery ID receipt
        if (toolId is "terminal.run" or "terminal.shell")
        {
            return await ExecuteTerminalAsync(execution, args, publishDelta, cancellationToken).ConfigureAwait(false);
        }

        if (toolId is "web.search" or "web.fetch")
        {
            return await ExecuteWebAsync(execution, toolId, args, publishDelta, cancellationToken).ConfigureAwait(false);
        }

        if (toolId == "files.delete")
        {
            try
            {
                ThrowIfExecutionCancelled(execution.ExecutionId, cancellationToken);
                // Retrieve pending recovery bytes stored at Prepare
                if (!_pendingDeleteBytes.TryRemove(execution.ExecutionId.Value, out var recoveryBytes)
                    || !_pendingDeletePath.TryRemove(execution.ExecutionId.Value, out var canonicalPath)
                    || !_pendingDeleteHash.TryRemove(execution.ExecutionId.Value, out var beforeHash))
                {
                    throw new InvalidOperationException("recovery_unavailable");
                }

                var argsDelete = args;
                var pathInput = argsDelete["path"]?.GetValue<string>() ?? throw new ArgumentException("missing_path");
                var expectedSha = argsDelete["expected_sha256"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(expectedSha)
                    && !_pendingExpectedHashes.TryGetValue(execution.ExecutionId.Value, out expectedSha))
                {
                    throw new ArgumentException("missing_expected_sha256");
                }

                // Re-resolve at execution to detect junction/symlink changes
                var reResolved = ResolveWithinRootForPrepare(pathInput);
                if (!string.Equals(reResolved, canonicalPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException("workspace_changed");
                }

                var currentHash = ComputeFileHash(reResolved);
                if (!string.Equals(currentHash, expectedSha, StringComparison.Ordinal))
                {
                    throw new UnauthorizedAccessException("workspace_changed");
                }

                var info = new FileInfo(reResolved);
                if (Directory.Exists(reResolved))
                {
                    throw new ArgumentException("not_a_file");
                }
                if (info.Length > 1 * 1024 * 1024)
                {
                    throw new ArgumentException("recovery_too_large");
                }

                // Verify recovery bytes hash matches expected
                var computed = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(recoveryBytes)).ToLowerInvariant();
                if (!string.Equals(computed, beforeHash, StringComparison.Ordinal))
                {
                    throw new UnauthorizedAccessException("workspace_changed");
                }

                // Perform the final preimage check and delete under one mutation gate.
                ThrowIfExecutionCancelled(execution.ExecutionId, cancellationToken);
                DeleteIfHashMatches(reResolved, expectedSha, recoveryBytes, cancellationToken);

                // Generate opaque recovery ID and store for restore, respecting run ownership via executionId binding
                var recoveryId = "payload_" + Guid.NewGuid().ToString("N");
                _hostRecoveryBytes[recoveryId] = recoveryBytes;
                _hostRecoveryPath[recoveryId] = canonicalPath;
                _hostRecoveryHash[recoveryId] = beforeHash;
                if (_pendingRunIds.TryGetValue(execution.ExecutionId.Value, out var ownerRunId))
                {
                    _hostRecoveryOwners[recoveryId] = ownerRunId;
                }
                _hostConsumed[recoveryId] = false;
                _hostRecoveryCreatedAt[recoveryId] = DateTimeOffset.UtcNow;

                // Also handle generic recovery payload intent: if execution.PreparationPayloads contained RecoveryOpaque, it was already committed via Core.
                // Expose recovery ID via receipt, never expose bytes to model/client
                var output = new JsonObject
                {
                    ["path"] = ToToolPath(canonicalPath),
                    ["before_sha256"] = beforeHash,
                    ["recovery_id"] = recoveryId,
                    ["bytes"] = recoveryBytes.Length,
                };
                var outputBytes = Encoding.UTF8.GetBytes(output.ToJsonString());
                const int chunkSizeDel = 16 * 1024;
                for (int offset = 0; offset < outputBytes.Length; offset += chunkSizeDel)
                {
                    var chunk = Encoding.UTF8.GetString(outputBytes, offset, Math.Min(chunkSizeDel, outputBytes.Length - offset));
                    await publishDelta(new ToolOutputChunk(execution.ExecutionId, (offset / chunkSizeDel) + 1, ToolOutputStream.Stdout, chunk)).ConfigureAwait(false);
                }

                return new ToolExecutionReceipt(
                    execution.ExecutionId,
                    execution.ToolCallId,
                    ToolExecutionOutcome.Succeeded,
                    "ok",
                    null,
                    sideEffectsPossible: true,
                    modelContent: outputBytes,
                    contentEncoding: RuAgent.Harness.Core.Persistence.HarnessPayloadEncoding.Json);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException or IOException or ArgumentException or InvalidOperationException)
            {
                var code = ex is UnauthorizedAccessException ? "workspace_changed"
                    : ex is FileNotFoundException ? "file_not_found"
                    : ex is ArgumentException ae ? (ae.Message == "recovery_too_large" ? "recovery_too_large" : "invalid_arguments")
                    : ex is InvalidOperationException ioe ? (ioe.Message == "recovery_unavailable" ? "recovery_unavailable" : ioe.Message)
                    : "io_error";
                // Map stable codes
                if (ex.Message == "recovery_too_large") code = "recovery_too_large";
                else if (ex.Message == "workspace_changed") code = "workspace_changed";
                else if (ex.Message == "recovery_unavailable") code = "recovery_unavailable";
                else if (System.Text.RegularExpressions.Regex.IsMatch(ex.Message, "^[a-z][a-z0-9_]{0,63}$")) code = ex.Message;
                var errorJson = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = code }));
                return new ToolExecutionReceipt(
                    execution.ExecutionId,
                    execution.ToolCallId,
                    ToolExecutionOutcome.Failed,
                    code,
                    null,
                    sideEffectsPossible: false,
                    modelContent: errorJson,
                    contentEncoding: RuAgent.Harness.Core.Persistence.HarnessPayloadEncoding.Json);
            }
        }

        if (toolId == "files.restore")
        {
            string? restoreRecoveryId = null;
            var restoreReservationHeld = false;
            try
            {
                ThrowIfExecutionCancelled(execution.ExecutionId, cancellationToken);
                var recoveryId = args["recovery_id"]?.GetValue<string>() ?? throw new ArgumentException("missing_recovery_id");
                restoreRecoveryId = recoveryId;
                if (!_hostRecoveryBytes.TryGetValue(recoveryId, out var bytes))
                {
                    throw new FileNotFoundException("recovery_unavailable");
                }

                if (!_pendingRunIds.TryGetValue(execution.ExecutionId.Value, out var executingRunId)
                    || !_hostRecoveryOwners.TryGetValue(recoveryId, out var ownerRunId)
                    || ownerRunId != executingRunId)
                {
                    throw new UnauthorizedAccessException("recovery_owner_mismatch");
                }

                if (!_hostConsumed.TryGetValue(recoveryId, out var consumed))
                {
                    throw new FileNotFoundException("recovery_unavailable");
                }

                // Reserve the single-use recovery before inspecting or writing the
                // target. A read-then-write check lets two concurrent restores both
                // pass and violates the one-time recovery contract.
                if (consumed || !_hostConsumed.TryUpdate(recoveryId, true, false))
                {
                    throw new InvalidOperationException("recovery_consumed");
                }

                restoreReservationHeld = true;

                var canonicalPath = _hostRecoveryPath[recoveryId];
                var expectedHash = _hostRecoveryHash[recoveryId];

                // Check run is still active â€“ for host in-memory, we check if file was already deleted and not yet restored? Terminal check simulated via consumed flag.
                // Original target must still be missing; recreated/changed rejects
                if (File.Exists(canonicalPath) || Directory.Exists(canonicalPath))
                {
                    throw new InvalidOperationException("recovery_target_exists");
                }

                var resolved = ResolveWithinRootForPrepare(canonicalPath);
                // Verify still inside and not protected
                if (!string.Equals(resolved, canonicalPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException("workspace_changed");
                }

                var dir = Path.GetDirectoryName(canonicalPath);
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    throw new DirectoryNotFoundException("target_directory_missing");
                }

                // Re-check the final parent immediately before the atomic replace;
                // a parent junction swap must not redirect the restore elsewhere.
                var finalParent = CanonicalizeExistingDirectory(dir);
                if (!string.Equals(finalParent, dir, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException("workspace_changed");
                }

                // Byte-exact restore, never redacted/mutated
                // Do not replace a file that appeared after the existence check;
                // restore must fail rather than clobber a concurrent user write.
                ThrowIfExecutionCancelled(execution.ExecutionId, cancellationToken);
                RestoreAtomically(canonicalPath, bytes, cancellationToken);
                var restoredHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
                if (!string.Equals(restoredHash, expectedHash, StringComparison.Ordinal))
                {
                    throw new IOException("io_error");
                }

                restoreReservationHeld = false;
                _hostRecoveryBytes.TryRemove(recoveryId, out _);
                _hostRecoveryPath.TryRemove(recoveryId, out _);
                _hostRecoveryHash.TryRemove(recoveryId, out _);
                _hostRecoveryOwners.TryRemove(recoveryId, out _);
                _hostRecoveryCreatedAt.TryRemove(recoveryId, out _);

                var output = new JsonObject
                {
                    ["recovery_id"] = recoveryId,
                    ["path"] = ToToolPath(canonicalPath),
                    ["bytes"] = bytes.Length,
                    ["restored_sha256"] = restoredHash,
                };
                var outputBytes = Encoding.UTF8.GetBytes(output.ToJsonString());
                const int chunkSizeRes = 16 * 1024;
                for (int offset = 0; offset < outputBytes.Length; offset += chunkSizeRes)
                {
                    var chunk = Encoding.UTF8.GetString(outputBytes, offset, Math.Min(chunkSizeRes, outputBytes.Length - offset));
                    await publishDelta(new ToolOutputChunk(execution.ExecutionId, (offset / chunkSizeRes) + 1, ToolOutputStream.Stdout, chunk)).ConfigureAwait(false);
                }

                return new ToolExecutionReceipt(
                    execution.ExecutionId,
                    execution.ToolCallId,
                    ToolExecutionOutcome.Succeeded,
                    "ok",
                    null,
                    sideEffectsPossible: true,
                    modelContent: outputBytes,
                    contentEncoding: RuAgent.Harness.Core.Persistence.HarnessPayloadEncoding.Json);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException or IOException or ArgumentException or InvalidOperationException)
            {
                if (restoreReservationHeld && restoreRecoveryId is not null)
                {
                    _hostConsumed.TryUpdate(restoreRecoveryId, false, true);
                }

                var code = ex is UnauthorizedAccessException ? "workspace_changed"
                    : ex is FileNotFoundException ? "recovery_unavailable"
                    : ex is ArgumentException ? "invalid_arguments"
                    : ex is InvalidOperationException ioe ? ioe.Message
                    : "io_error";
                if (System.Text.RegularExpressions.Regex.IsMatch(ex.Message, "^[a-z][a-z0-9_]{0,63}$")) code = ex.Message;
                if (ex.Message is "target_directory_missing")
                {
                    code = "workspace_changed";
                }
                var errorJson = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = code }));
                // For restore, sideEffectsPossible false on failure
                return new ToolExecutionReceipt(
                    execution.ExecutionId,
                    execution.ToolCallId,
                    ToolExecutionOutcome.Failed,
                    code,
                    null,
                    sideEffectsPossible: false,
                    modelContent: errorJson,
                    contentEncoding: RuAgent.Harness.Core.Persistence.HarnessPayloadEncoding.Json);
            }
        }

        // Handle write/edit/patch as mutations (simplified)
        if (toolId is "files.write" or "files.edit" or "files.apply_patch")
        {
            try
            {
                ThrowIfExecutionCancelled(execution.ExecutionId, cancellationToken);
                JsonObject output;
                if (toolId == "files.write")
                {
                    // Expect path, content, expected_sha256
                    var pathInput = args["path"]?.GetValue<string>() ?? throw new ArgumentException("missing_path");
                    var content = args["content"]?.GetValue<string>() ?? throw new ArgumentException("missing_content");
                    var overwrite = args["overwrite"]?.GetValue<bool>() ?? false;
                    var expected = args["expected_sha256"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(expected))
                    {
                        expected = _pendingExpectedHashes.TryGetValue(execution.ExecutionId.Value, out var preparedHash)
                            ? preparedHash
                            : "missing";
                    }
                    var resolved = ResolveWithinRootForPrepare(pathInput);
                    if (Directory.Exists(resolved))
                    {
                        throw new ArgumentException("not_a_file");
                    }
                    if (File.Exists(resolved) && !overwrite)
                    {
                        throw new IOException("file_exists");
                    }
                    if (_pendingResolvedPaths.TryGetValue(execution.ExecutionId.Value, out var preparedPath)
                        && !string.Equals(preparedPath, resolved, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new UnauthorizedAccessException("workspace_changed");
                    }
                    var beforeHash = ComputeFileHash(resolved);
                    if (!string.Equals(beforeHash, expected, StringComparison.Ordinal))
                    {
                        throw new UnauthorizedAccessException("workspace_changed");
                    }

                    var bytes = EncodeUtf8Strict(content);
                    if (bytes.Length > MaxMutationBytes)
                    {
                        throw new ArgumentException("content_too_large");
                    }
                    var dir = Path.GetDirectoryName(resolved);
                    if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                    ThrowIfExecutionCancelled(execution.ExecutionId, cancellationToken);
                    ReplaceIfHashMatches(resolved, bytes, beforeHash, overwrite, cancellationToken);
                    var afterHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
                    output = new JsonObject { ["path"] = ToToolPath(resolved), ["before_sha256"] = beforeHash, ["after_sha256"] = afterHash };
                }
                else if (toolId == "files.edit")
                {
                    var pathInput = args["path"]?.GetValue<string>() ?? throw new ArgumentException("missing_path");
                    var oldStr = args["old_string"]?.GetValue<string>() ?? throw new ArgumentException("missing_old_string");
                    var newStr = args["new_string"]?.GetValue<string>() ?? string.Empty;
                    var expected = args["expected_sha256"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(expected)
                        && !_pendingExpectedHashes.TryGetValue(execution.ExecutionId.Value, out expected))
                    {
                        throw new ArgumentException("missing_expected_sha256");
                    }
                    var resolved = ResolveWithinRootForPrepare(pathInput);
                    if (Directory.Exists(resolved))
                    {
                        throw new ArgumentException("not_a_file");
                    }
                    if (_pendingResolvedPaths.TryGetValue(execution.ExecutionId.Value, out var preparedPath)
                        && !string.Equals(preparedPath, resolved, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new UnauthorizedAccessException("workspace_changed");
                    }
                    var beforeHash = ComputeFileHash(resolved);
                    if (!string.Equals(beforeHash, expected, StringComparison.Ordinal))
                    {
                        throw new UnauthorizedAccessException("workspace_changed");
                    }

                    var info = new FileInfo(resolved);
                    if (!info.Exists)
                    {
                        throw new FileNotFoundException("file_not_found");
                    }

                    if (info.Length > MaxMutationFileBytes)
                    {
                        throw new ArgumentException("file_too_large");
                    }

                    string text;
                    try
                    {
                        var currentBytes = ReadBoundedBytes(resolved, MaxMutationFileBytes, "file_too_large");
                        text = StrictUtf8.GetString(currentBytes);
                    }
                    catch (DecoderFallbackException error)
                    {
                        throw new ArgumentException("invalid_utf8", error);
                    }
                    var expectedOccurrences = args["expected_occurrences"]?.GetValue<int>() ?? 1;
                    var occurrences = CountOccurrences(text, oldStr);
                    if (occurrences != expectedOccurrences)
                    {
                        throw new ArgumentException("edit_match_count");
                    }

                    var updated = text.Replace(oldStr, newStr, StringComparison.Ordinal);
                    var updatedBytes = EncodeUtf8Strict(updated);
                    if (updatedBytes.Length > MaxMutationBytes)
                    {
                        throw new ArgumentException("content_too_large");
                    }

                    ThrowIfExecutionCancelled(execution.ExecutionId, cancellationToken);
                    ReplaceIfHashMatches(resolved, updatedBytes, beforeHash, cancellationToken: cancellationToken);
                    var afterHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(updatedBytes)).ToLowerInvariant();
                    output = new JsonObject { ["path"] = ToToolPath(resolved), ["before_sha256"] = beforeHash, ["after_sha256"] = afterHash };
                }
                else
                {
                    // This capability has no Host executor yet. A forged prepared
                    // execution must fail closed instead of returning a false success.
                    return FailedReceipt(execution, "patch_not_available");
                }

                var outputBytes = Encoding.UTF8.GetBytes(output.ToJsonString());
                const int chunkSize = 16 * 1024;
                for (int offset = 0; offset < outputBytes.Length; offset += chunkSize)
                {
                    var chunk = Encoding.UTF8.GetString(outputBytes, offset, Math.Min(chunkSize, outputBytes.Length - offset));
                    await publishDelta(new ToolOutputChunk(execution.ExecutionId, (offset / chunkSize) + 1, ToolOutputStream.Stdout, chunk)).ConfigureAwait(false);
                }

                return new ToolExecutionReceipt(
                    execution.ExecutionId,
                    execution.ToolCallId,
                    ToolExecutionOutcome.Succeeded,
                    "ok",
                    null,
                    sideEffectsPossible: true,
                    modelContent: outputBytes,
                    contentEncoding: RuAgent.Harness.Core.Persistence.HarnessPayloadEncoding.Json);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException or IOException or ArgumentException)
            {
                var code = ex is UnauthorizedAccessException ? "workspace_changed" : ex is FileNotFoundException ? "file_not_found" : ex is ArgumentException ? "invalid_arguments" : "io_error";
                if (System.Text.RegularExpressions.Regex.IsMatch(ex.Message, "^[a-z][a-z0-9_]{0,63}$")) code = ex.Message;
                var errorJson = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = code }));
                return new ToolExecutionReceipt(
                    execution.ExecutionId,
                    execution.ToolCallId,
                    ToolExecutionOutcome.Failed,
                    code,
                    null,
                    sideEffectsPossible: false,
                    modelContent: errorJson,
                    contentEncoding: RuAgent.Harness.Core.Persistence.HarnessPayloadEncoding.Json);
            }
        }

        if (toolId is not ("files.list" or "files.glob" or "files.search" or "files.read"))
        {
            return FailedReceipt(execution, "phase_not_available");
        }

        var path = args["path"] is JsonValue pv && pv.TryGetValue<string>(out var p) ? p : ".";

        try
        {
            ThrowIfExecutionCancelled(execution.ExecutionId, cancellationToken);
            JsonObject output;
            if (toolId == "files.list")
            {
                var maxEntries = ReadOptionalInt(args, "max_entries", MaxDiscoveryEntries, 1, MaxDiscoveryEntries);
                output = ListFiles(path, maxEntries);
            }
            else if (toolId == "files.glob")
            {
                output = GlobFiles(args);
            }
            else if (toolId == "files.search")
            {
                output = SearchFiles(args);
            }
            else
            {
                var offset = ReadOptionalLong(args, "offset", 0, 0, long.MaxValue);
                var limit = ReadOptionalInt(args, "limit", MaxReadBytes, 1, MaxReadBytes);
                output = ReadFile(path, offset, limit);
            }

            var outputBytes = Encoding.UTF8.GetBytes(output.ToJsonString());
            const int chunkSize = 16 * 1024;
            for (int offset = 0; offset < outputBytes.Length; offset += chunkSize)
            {
                var chunk = Encoding.UTF8.GetString(outputBytes, offset, Math.Min(chunkSize, outputBytes.Length - offset));
                await publishDelta(new ToolOutputChunk(execution.ExecutionId, (offset / chunkSize) + 1, ToolOutputStream.Stdout, chunk)).ConfigureAwait(false);
            }

            return new ToolExecutionReceipt(
                execution.ExecutionId,
                execution.ToolCallId,
                ToolExecutionOutcome.Succeeded,
                "ok",
                null,
                sideEffectsPossible: false,
                modelContent: outputBytes,
                contentEncoding: RuAgent.Harness.Core.Persistence.HarnessPayloadEncoding.Json);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException or IOException or DecoderFallbackException or ArgumentException)
        {
            var errorCode = ex is DecoderFallbackException ? "invalid_utf8"
                : ex is ArgumentException ? "invalid_arguments"
                : ex is UnauthorizedAccessException ? "access_denied"
                    : ex.Message == "file_exists" ? "file_exists"
                    : "io_error";
            if (System.Text.RegularExpressions.Regex.IsMatch(ex.Message, "^[a-z][a-z0-9_]{0,63}$")) errorCode = ex.Message;
            var errorJson = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = errorCode }));
            return new ToolExecutionReceipt(
                execution.ExecutionId,
                execution.ToolCallId,
                ToolExecutionOutcome.Failed,
                errorCode,
                null,
                sideEffectsPossible: false,
                modelContent: errorJson,
                contentEncoding: RuAgent.Harness.Core.Persistence.HarnessPayloadEncoding.Json);
        }
    }

    private async Task<ToolExecutionReceipt> ExecuteWebAsync(
        PreparedToolExecution execution,
        string toolId,
        JsonObject arguments,
        Func<ToolOutputChunk, ValueTask> publishDelta,
        CancellationToken cancellationToken)
    {
        if (_webExecutor is null)
        {
            return FailedReceipt(execution, "web_disabled");
        }

        var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_activeWebExecutions.TryAdd(execution.ExecutionId.Value, linkedCancellation))
        {
            linkedCancellation.Dispose();
            return FailedReceipt(execution, "execution_in_progress");
        }

        try
        {
            ThrowIfExecutionCancelled(execution.ExecutionId, linkedCancellation.Token);
            var output = await _webExecutor.ExecuteAsync(
                toolId,
                arguments,
                linkedCancellation.Token).ConfigureAwait(false);
            var outputBytes = Encoding.UTF8.GetBytes(output.ToJsonString());
            if (outputBytes.Length > MaxWebOutputBytes)
            {
                return FailedReceipt(execution, "output_too_large");
            }

            const int chunkSize = 16 * 1024;
            for (var offset = 0; offset < outputBytes.Length; offset += chunkSize)
            {
                var chunk = Encoding.UTF8.GetString(
                    outputBytes,
                    offset,
                    Math.Min(chunkSize, outputBytes.Length - offset));
                await publishDelta(new ToolOutputChunk(
                    execution.ExecutionId,
                    (offset / chunkSize) + 1,
                    ToolOutputStream.Stdout,
                    chunk)).ConfigureAwait(false);
            }

            return new ToolExecutionReceipt(
                execution.ExecutionId,
                execution.ToolCallId,
                ToolExecutionOutcome.Succeeded,
                "ok",
                null,
                sideEffectsPossible: false,
                modelContent: outputBytes,
                contentEncoding: HarnessPayloadEncoding.Json);
        }
        catch (OperationCanceledException) when (
            _cancelledExecutions.ContainsKey(execution.ExecutionId.Value)
            || cancellationToken.IsCancellationRequested)
        {
            return new ToolExecutionReceipt(
                execution.ExecutionId,
                execution.ToolCallId,
                ToolExecutionOutcome.Cancelled,
                "process_cancelled",
                null,
                sideEffectsPossible: false);
        }
        catch (UnauthorizedAccessException error)
        {
            return FailedReceipt(execution, StableWebErrorCode(error.Message, "access_denied"));
        }
        catch (ArgumentException error)
        {
            return FailedReceipt(execution, StableWebErrorCode(error.Message, "invalid_arguments"));
        }
        catch (InvalidDataException error)
        {
            return FailedReceipt(execution, StableWebErrorCode(error.Message, "web_response_invalid"));
        }
        catch (HttpRequestException)
        {
            return FailedReceipt(execution, "web_request_failed");
        }
        catch (Exception)
        {
            return FailedReceipt(execution, "web_adapter_failed");
        }
        finally
        {
            if (_activeWebExecutions.TryRemove(execution.ExecutionId.Value, out var active))
            {
                active.Dispose();
            }
        }
    }

    private static string StableWebErrorCode(string? candidate, string fallback) =>
        candidate is { Length: >= 1 and <= 64 }
        && Regex.IsMatch(candidate, "^[a-z][a-z0-9_]{0,63}$")
            ? candidate
            : fallback;

    public Task CancelAsync(ExecutionId executionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _cancelledExecutions[executionId.Value] = 0;
        if (_activeTerminalExecutions.TryGetValue(executionId.Value, out var active))
        {
            active.Cancel();
        }
        if (_activeWebExecutions.TryGetValue(executionId.Value, out var activeWeb))
        {
            activeWeb.Cancel();
        }

        // Cancellation also discards a prepared-but-not-started call. This is
        // important when a later tool in the same model step asks for approval or
        // when the coordinator is cancelled between prepare and dispatch; otherwise
        // the in-memory authorization material would remain reusable until process
        // restart.
        CleanupExecutionState(executionId);

        return Task.CompletedTask;
    }

    /// <summary>Cancels every live terminal execution started under the run.</summary>
    public Task CancelRunAsync(RunId runId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _cancelledRuns[runId.Value] = 0;
        foreach (var pair in _activeTerminalExecutions)
        {
            if (pair.Value.RunId.Value == runId.Value)
            {
                _cancelledExecutions[pair.Key] = 0;
                pair.Value.Cancel();
            }
        }
        foreach (var pair in _activeWebExecutions)
        {
            if (_pendingRunIds.TryGetValue(pair.Key, out var activeRun)
                && activeRun.Value == runId.Value)
            {
                _cancelledExecutions[pair.Key] = 0;
                pair.Value.Cancel();
            }
        }

        // Drop every prepared call for the run, including read-only calls that have
        // not started yet. This closes the race where a cancelled scheduler would
        // otherwise enter a file mutation after the durable run state is terminal.
        foreach (var pair in _pendingRunIds)
        {
            if (pair.Value.Value == runId.Value)
            {
                _cancelledExecutions[pair.Key] = 0;
                CleanupExecutionState(new ExecutionId(pair.Key));
            }
        }

        return Task.CompletedTask;
    }

    private readonly ConcurrentDictionary<string, byte[]> _pendingDeleteBytes = new();
    private readonly ConcurrentDictionary<string, string> _pendingDeletePath = new();
    private readonly ConcurrentDictionary<string, string> _pendingDeleteHash = new();

    private void StoreArguments(ExecutionId executionId, string argsJson)
    {
        _pendingArguments[executionId.Value] = argsJson;
    }

    private void CleanupExecutionState(ExecutionId executionId)
    {
        var key = executionId.Value;
        _pendingArguments.TryRemove(key, out _);
        _pendingToolIds.TryRemove(key, out _);
        _pendingExpectedHashes.TryRemove(key, out _);
        _pendingResolvedPaths.TryRemove(key, out _);
        _pendingRunIds.TryRemove(key, out _);
        _pendingDeleteBytes.TryRemove(key, out _);
        _pendingDeletePath.TryRemove(key, out _);
        _pendingDeleteHash.TryRemove(key, out _);
        _pendingTerminalSpecs.TryRemove(key, out _);
    }

    private void PruneHostRecoveries(DateTimeOffset now)
    {
        var cutoff = now - HostRecoveryTtl;
        foreach (var entry in _hostRecoveryCreatedAt)
        {
            if (entry.Value > cutoff)
            {
                continue;
            }

            var recoveryId = entry.Key;
            _hostRecoveryCreatedAt.TryRemove(new KeyValuePair<string, DateTimeOffset>(recoveryId, entry.Value));
            _hostRecoveryBytes.TryRemove(recoveryId, out _);
            _hostRecoveryPath.TryRemove(recoveryId, out _);
            _hostRecoveryHash.TryRemove(recoveryId, out _);
            _hostRecoveryOwners.TryRemove(recoveryId, out _);
            _hostConsumed.TryRemove(recoveryId, out _);
        }

        if (_hostRecoveryCreatedAt.Count <= MaxHostRecoveries)
        {
            return;
        }

        foreach (var entry in _hostRecoveryCreatedAt
                     .OrderBy(pair => pair.Value)
                     .Take(_hostRecoveryCreatedAt.Count - MaxHostRecoveries))
        {
            var recoveryId = entry.Key;
            if (!_hostRecoveryCreatedAt.TryRemove(new KeyValuePair<string, DateTimeOffset>(recoveryId, entry.Value)))
            {
                continue;
            }

            _hostRecoveryBytes.TryRemove(recoveryId, out _);
            _hostRecoveryPath.TryRemove(recoveryId, out _);
            _hostRecoveryHash.TryRemove(recoveryId, out _);
            _hostRecoveryOwners.TryRemove(recoveryId, out _);
            _hostConsumed.TryRemove(recoveryId, out _);
        }
    }

    private void ThrowIfExecutionCancelled(ExecutionId executionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_cancelledExecutions.ContainsKey(executionId.Value)
            || (_pendingRunIds.TryGetValue(executionId.Value, out var runId)
                && _cancelledRuns.ContainsKey(runId.Value)))
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private string ResolveWithinRootForPrepare(string path)
    {
        try
        {
            return ResolveSafeWorkspacePath(
                path,
                allowMissingLeaf: true,
                allowAbsoluteOutside: Path.IsPathRooted(path));
        }
        catch (UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException("workspace_changed");
        }
    }

    private static string ComputeFileHash(string canonicalPath)
    {
        if (!File.Exists(canonicalPath))
        {
            return "missing";
        }

        using var stream = new FileStream(canonicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            sha.TransformBlock(buffer, 0, read, null, 0);
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static byte[] ReadBoundedBytes(string path, int maxBytes, string tooLargeCode)
    {
        if (maxBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            81920,
            FileOptions.SequentialScan);
        using var output = new MemoryStream(Math.Min(maxBytes, 81920));
        var buffer = new byte[Math.Min(maxBytes, 81920)];
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            if (output.Length > maxBytes - read)
            {
                throw new ArgumentException(tooLargeCode);
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static string ReadBoundedText(string path, int maxBytes)
    {
        var bytes = ReadBoundedBytes(path, maxBytes, "file_too_large");
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException error)
        {
            throw new ArgumentException("invalid_utf8", error);
        }
    }

    private JsonObject ListFiles(string relativePath, int maxEntries)
    {
        var directory = ResolveWithinRoot(relativePath);
        if (!Directory.Exists(directory))
        {
            throw new IOException("not_a_directory");
        }

        var collected = new List<JsonObject>();
        var scannedEntries = 0;
        var scanLimit = Math.Max(maxEntries * 4, maxEntries + 1);
        var scanLimitHit = false;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (scannedEntries++ >= scanLimit)
            {
                scanLimitHit = true;
                break;
            }

            var safeEntry = TryResolveExistingInsideRoot(entry, directory);
            if (safeEntry is null)
            {
                continue;
            }

            if (IsProtectedPath(safeEntry))
            {
                continue;
            }

            var info = new FileInfo(safeEntry);
            var relative = ToToolPath(safeEntry);
            collected.Add(new JsonObject
            {
                ["path"] = relative,
                ["kind"] = Directory.Exists(safeEntry) ? "directory" : "file",
                ["size"] = Directory.Exists(safeEntry) ? 0 : info.Length,
            });

            if (collected.Count > maxEntries)
            {
                break;
            }
        }

        var truncated = collected.Count > maxEntries || scanLimitHit;
        var entries = new JsonArray(collected.Take(maxEntries).Select(item => (JsonNode)item).ToArray());
        return new JsonObject
        {
            ["entries"] = entries,
            ["count"] = entries.Count,
            ["truncated"] = truncated,
        };
    }

    private JsonObject ReadFile(string relativePath, long offset = 0, int maxBytes = MaxReadBytes)
    {
        var path = ResolveWithinRoot(relativePath);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            if (Directory.Exists(path))
            {
                throw new IOException("is_directory");
            }

            throw new FileNotFoundException("file_not_found", path);
        }

        if (offset < 0 || maxBytes is < 1 or > MaxReadBytes)
        {
            throw new ArgumentException("invalid_arguments");
        }

        if (offset >= info.Length)
        {
            var beyondRelative = ToToolPath(path);
            return new JsonObject
            {
                ["path"] = beyondRelative,
                ["content"] = string.Empty,
                ["truncated"] = false,
                ["bytes_read"] = 0,
                ["total_bytes"] = info.Length,
                ["offset"] = offset,
            };
        }

        var bytes = new byte[checked(maxBytes + 1)];
        var total = 0;
        using (var stream = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete,
                   81920,
                   FileOptions.SequentialScan))
        {
            stream.Seek(offset, SeekOrigin.Begin);
            while (total < bytes.Length)
            {
                var read = stream.Read(bytes, total, bytes.Length - total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }
        }

        var rawLength = Math.Min(total, maxBytes);
        var start = SkipLeadingUtf8ContinuationBytes(bytes, rawLength);
        var validLength = GetValidUtf8Length(bytes, start, rawLength);
        var emittedBytes = Math.Max(0, validLength - start);
        // Compare the bytes remaining instead of adding offset + total, which could
        // overflow for a hostile long offset near Int64.MaxValue.
        var hasMore = offset < info.Length
            && total < bytes.Length
            && total < info.Length - offset;
        var truncated = total > maxBytes || hasMore || validLength < rawLength || start > 0;
        var content = StrictUtf8.GetString(bytes, start, emittedBytes);
        var relative = ToToolPath(path);
        return new JsonObject
        {
            ["path"] = relative,
            ["content"] = content,
            ["truncated"] = truncated,
            ["bytes_read"] = emittedBytes,
            ["total_bytes"] = info.Length,
            ["offset"] = offset,
        };
    }

    private JsonObject GlobFiles(JsonObject args)
    {
        var basePath = args["path"] is JsonValue bv && bv.TryGetValue<string>(out var bp) ? bp : ".";
        var pattern = args["pattern"] is JsonValue pv2 && pv2.TryGetValue<string>(out var pat) ? pat : throw new ArgumentException("missing pattern");
        var maxResults = args["max_results"] is JsonValue mv && mv.TryGetValue<int>(out var mr) ? mr : 64;
        var baseDir = ResolveWithinRoot(basePath);
        if (!Directory.Exists(baseDir))
        {
            throw new IOException("not_a_directory");
        }

        var regex = GlobToRegex(pattern);
        var results = new List<string>();
        var stack = new Stack<string>();
        stack.Push(baseDir);
        var visitedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int scanned = 0;
        var resultLimit = maxResults + 1;
        var scanLimitHit = false;
        while (stack.Count > 0 && results.Count < resultLimit && scanned < 2048)
        {
            var cur = stack.Pop();
            if (!visitedDirectories.Add(cur))
            {
                continue;
            }

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(cur);
            }
            catch
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (results.Count >= resultLimit || scanned >= 2048)
                {
                    break;
                }

                var safeEntry = TryResolveExistingInsideRoot(entry, baseDir);
                if (safeEntry is null)
                {
                    continue;
                }

                if (IsProtectedPath(safeEntry))
                {
                    continue;
                }

                var rel = ToToolPath(safeEntry);
                bool matches = false;
                try
                {
                    matches = regex.IsMatch(rel) || regex.IsMatch(Path.GetFileName(rel));
                }
                catch
                {
                    matches = false;
                }

                if (matches)
                {
                    results.Add(rel);
                }

                if (Directory.Exists(safeEntry))
                {
                    stack.Push(safeEntry);
                }

                scanned++;
            }
        }

        scanLimitHit = scanned >= 2048 && stack.Count > 0;

        results.Sort(StringComparer.Ordinal);
        var final = results.Count > maxResults ? results.Take(maxResults).ToList() : results;
        var truncated = results.Count > maxResults || scanLimitHit;
        var arr = new JsonArray(final.Select(r => (JsonNode)JsonValue.Create(r)).ToArray());
        return new JsonObject { ["results"] = arr, ["count"] = final.Count, ["truncated"] = truncated };
    }

    private JsonObject SearchFiles(JsonObject args)
    {
        var basePath = args["path"] is JsonValue bv && bv.TryGetValue<string>(out var bp) ? bp : ".";
        var query = args["query"] is JsonValue qv && qv.TryGetValue<string>(out var q) ? q : throw new ArgumentException("missing query");
        var fileGlob = args["file_glob"] is JsonValue fg && fg.TryGetValue<string>(out var fgStr) ? fgStr : null;
        var caseSensitive = args["case_sensitive"] is JsonValue csv && csv.TryGetValue<bool>(out var cs) ? cs : false;
        var maxResults = args["max_results"] is JsonValue mv && mv.TryGetValue<int>(out var mr) ? mr : 64;
        var baseDir = ResolveWithinRoot(basePath);
        if (!Directory.Exists(baseDir))
        {
            throw new IOException("not_a_directory");
        }

        Regex? fileGlobRegex = fileGlob is not null ? GlobToRegex(fileGlob) : null;
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var results = new List<JsonObject>();
        var stack = new Stack<string>();
        stack.Push(baseDir);
        var visitedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int scannedFiles = 0;
        var resultLimit = maxResults + 1;
        var scanLimitHit = false;
        while (stack.Count > 0 && results.Count < resultLimit && scannedFiles < 1024)
        {
            var cur = stack.Pop();
            if (!visitedDirectories.Add(cur))
            {
                continue;
            }

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(cur);
            }
            catch
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (results.Count >= resultLimit || scannedFiles >= 1024)
                {
                    break;
                }

                var safeEntry = TryResolveExistingInsideRoot(entry, baseDir);
                if (safeEntry is null)
                {
                    continue;
                }

                if (IsProtectedPath(safeEntry))
                {
                    continue;
                }

                if (Directory.Exists(safeEntry))
                {
                    stack.Push(safeEntry);
                    continue;
                }

                var rel = ToToolPath(safeEntry);
                if (fileGlobRegex is not null)
                {
                    bool m = false;
                    try { m = fileGlobRegex.IsMatch(rel) || fileGlobRegex.IsMatch(Path.GetFileName(rel)); } catch { m = false; }
                    if (!m) continue;
                }

                FileInfo info;
                try { info = new FileInfo(safeEntry); if (!info.Exists || info.Length > 1 * 1024 * 1024) continue; } catch { continue; }
                // binary check
                bool isBinary = false;
                try
                {
                    using var s = new FileStream(safeEntry, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
                    var buf = new byte[Math.Min(8000, (int)Math.Min(s.Length, 8000))];
                    var read = s.Read(buf, 0, buf.Length);
                    for (int i = 0; i < read; i++) if (buf[i] == 0) { isBinary = true; break; }
                }
                catch { continue; }
                if (isBinary) continue;

                try
                {
                    var fileText = ReadBoundedText(safeEntry, 1 * 1024 * 1024);
                    using var reader = new StringReader(fileText);
                    string? line;
                    int lineNum = 0;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lineNum++;
                        // The file read is already bounded; search the complete
                        // line and truncate only the returned preview below.
                        int idx = line.IndexOf(query, comparison);
                        if (idx >= 0 && results.Count < resultLimit)
                        {
                            var preview = line.Length > 512 ? line.Substring(0, 512) : line;
                            results.Add(new JsonObject { ["path"] = rel, ["line"] = lineNum, ["column"] = idx + 1, ["preview"] = preview, ["line_text"] = preview });
                        }
                        if (results.Count >= maxResults) break;
                    }
                }
                catch { continue; }
                scannedFiles++;
            }
        }

        scanLimitHit = scannedFiles >= 1024 && stack.Count > 0;

        var sorted = results.OrderBy(r => r["path"]!.GetValue<string>(), StringComparer.Ordinal).ThenBy(r => r["line"]!.GetValue<int>()).ToList();
        var final = sorted.Count > maxResults ? sorted.Take(maxResults).ToList() : sorted;
        var truncated = sorted.Count > maxResults || scanLimitHit;
        var arr = new JsonArray(final.Select(r => (JsonNode)r.DeepClone()).ToArray());
        return new JsonObject { ["results"] = arr, ["count"] = final.Count, ["truncated"] = truncated, ["scanned_files"] = scannedFiles };
    }

    private static Regex GlobToRegex(string pattern)
    {
        var normalized = pattern.Replace('\\', '/');
        var escaped = Regex.Escape(normalized);
        escaped = escaped.Replace(@"\*\*", "___DOUBLESTAR___");
        escaped = escaped.Replace(@"\*", "[^/]*");
        escaped = escaped.Replace(@"\?", "[^/]");
        escaped = escaped.Replace("___DOUBLESTAR___", ".*");
        return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static int CountOccurrences(string text, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException("empty_old_string");
        }

        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static byte[] EncodeUtf8Strict(string value)
    {
        try
        {
            return StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException error)
        {
            throw new ArgumentException("invalid_utf8", error);
        }
    }

    private static void WriteAtomically(string targetPath, byte[] bytes, bool overwrite = true)
    {
        var directory = Path.GetDirectoryName(targetPath)
            ?? throw new DirectoryNotFoundException("target_directory_missing");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       81920,
                       FileOptions.SequentialScan))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, targetPath, overwrite);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // Preserve the original write error; the temporary file is best-effort cleanup.
            }
        }
    }

    private void ReplaceIfHashMatches(
        string targetPath,
        byte[] bytes,
        string expectedHash,
        bool overwrite = true,
        CancellationToken cancellationToken = default)
    {
        lock (_mutationGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var safeTarget = ReResolveMutationTarget(targetPath);
            var currentHash = ComputeFileHash(safeTarget);
            if (!string.Equals(currentHash, expectedHash, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("workspace_changed");
            }

            if (!overwrite && !string.Equals(currentHash, "missing", StringComparison.Ordinal))
            {
                throw new IOException("file_exists");
            }

            cancellationToken.ThrowIfCancellationRequested();
            WriteAtomically(safeTarget, bytes, overwrite);
            var afterHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(ComputeFileHash(safeTarget), afterHash, StringComparison.Ordinal))
            {
                throw new IOException("io_error");
            }
        }
    }

    private void DeleteIfHashMatches(
        string targetPath,
        string expectedHash,
        byte[]? expectedBytes = null,
        CancellationToken cancellationToken = default)
    {
        lock (_mutationGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var safeTarget = ReResolveMutationTarget(targetPath);
            var currentHash = ComputeFileHash(safeTarget);
            if (!string.Equals(currentHash, expectedHash, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("workspace_changed");
            }

            if (expectedBytes is not null)
            {
                var currentBytes = ReadBoundedBytes(safeTarget, MaxMutationFileBytes, "recovery_too_large");
                if (!currentBytes.SequenceEqual(expectedBytes))
                {
                    throw new UnauthorizedAccessException("workspace_changed");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(safeTarget);
            if (File.Exists(safeTarget) || Directory.Exists(safeTarget))
            {
                throw new IOException("io_error");
            }
        }
    }

    private void RestoreAtomically(
        string targetPath,
        byte[] bytes,
        CancellationToken cancellationToken = default)
    {
        lock (_mutationGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var safeTarget = ReResolveMutationTarget(targetPath);
            if (File.Exists(safeTarget) || Directory.Exists(safeTarget))
            {
                throw new InvalidOperationException("recovery_target_exists");
            }

            cancellationToken.ThrowIfCancellationRequested();
            WriteAtomically(safeTarget, bytes, overwrite: false);
        }
    }

    private string ReResolveMutationTarget(string targetPath)
    {
        var isInside = IsInsideWorkspacePath(targetPath);
        var input = isInside
            ? Path.GetRelativePath(_canonicalRoot, targetPath).Replace(Path.DirectorySeparatorChar, '/')
            : targetPath;
        string resolved;
        try
        {
            resolved = ResolveSafeWorkspacePath(
                input,
                allowMissingLeaf: true,
                allowAbsoluteOutside: !isInside);
        }
        catch (Exception error) when (
            error is ArgumentException or IOException or UnauthorizedAccessException
                or DirectoryNotFoundException or FileNotFoundException)
        {
            throw new UnauthorizedAccessException("workspace_changed", error);
        }

        if (!string.Equals(resolved, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("workspace_changed");
        }

        return resolved;
    }

    private static int SkipLeadingUtf8ContinuationBytes(byte[] buffer, int length)
    {
        var start = 0;
        while (start < length && (buffer[start] & 0xC0) == 0x80)
        {
            start++;
        }

        return start;
    }

    private static int GetValidUtf8Length(byte[] buffer, int start, int length)
    {
        if (length <= start)
        {
            return start;
        }

        var index = length - 1;
        if ((buffer[index] & 0x80) == 0)
        {
            return length;
        }

        var continuationCount = 0;
        while (index >= start && (buffer[index] & 0xC0) == 0x80)
        {
            continuationCount++;
            index--;
        }

        if (index < start)
        {
            return start;
        }

        var leading = buffer[index];
        var expectedLength = leading switch
        {
            >= 0xC2 and <= 0xDF => 2,
            >= 0xE0 and <= 0xEF => 3,
            >= 0xF0 and <= 0xF4 => 4,
            _ => 1,
        };

        return continuationCount + 1 < expectedLength ? index : length;
    }

    private string ResolveWithinRoot(string path)
    {
        return ResolveSafeWorkspacePath(
            path,
            allowMissingLeaf: true,
            allowAbsoluteOutside: Path.IsPathRooted(path));
    }

    private string ResolveSafeWorkspacePath(
        string inputPath,
        bool allowMissingLeaf,
        bool allowAbsoluteOutside = false)
    {
        if (string.IsNullOrWhiteSpace(inputPath)
            || inputPath.Contains('\0'))
        {
            throw new UnauthorizedAccessException("workspace_path_invalid");
        }

        var isAbsolute = Path.IsPathRooted(inputPath);
        if (isAbsolute && !allowAbsoluteOutside)
        {
            throw new UnauthorizedAccessException("workspace_path_invalid");
        }

        var candidate = Path.GetFullPath(
            isAbsolute ? inputPath : Path.Combine(_canonicalRoot, inputPath));
        var rootWithSep = _canonicalRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _canonicalRoot
            : _canonicalRoot + Path.DirectorySeparatorChar;
        var candidateInside = string.Equals(candidate, _canonicalRoot, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
        if (!candidateInside && !(allowAbsoluteOutside && isAbsolute))
        {
            throw new UnauthorizedAccessException("workspace_path_outside_root");
        }

        string finalPath;
        var candidateExists = File.Exists(candidate) || Directory.Exists(candidate);
        if (candidateExists)
        {
            finalPath = CanonicalizeExistingPath(candidate);
        }
        else
        {
            // A broken symlink/junction reports false for File.Exists and
            // Directory.Exists, but a subsequent write would still follow it.
            // Reject any reparse-point leaf before treating it as a new file.
            try
            {
                if (File.GetAttributes(candidate).HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new UnauthorizedAccessException("workspace_path_outside_root");
                }
            }
            catch (FileNotFoundException)
            {
                // Truly missing leaf; parent canonicalization below is sufficient.
            }
            catch (DirectoryNotFoundException)
            {
                // Truly missing leaf; parent canonicalization below will reject a
                // missing parent rather than following an unresolved link.
            }

            if (!allowMissingLeaf)
            {
                throw new FileNotFoundException("workspace_path_not_found", candidate);
            }

            // New files do not have a final handle yet. Canonicalize the existing
            // parent so a junction in any parent component cannot redirect a write.
            var parent = Directory.GetParent(candidate)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
            {
                throw new DirectoryNotFoundException(parent);
            }

            var finalParent = CanonicalizeExistingDirectory(parent);
            finalPath = Path.Combine(finalParent, Path.GetFileName(candidate));
        }

        var finalInside = string.Equals(finalPath, _canonicalRoot, StringComparison.OrdinalIgnoreCase)
            || finalPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
        if (!finalInside && !(allowAbsoluteOutside && isAbsolute))
        {
            throw new UnauthorizedAccessException("workspace_path_outside_root");
        }

        return finalPath;
    }

    private string? TryResolveExistingInsideRoot(string path, string? boundaryRoot = null)
    {
        try
        {
            var resolved = CanonicalizeExistingPath(path);
            var boundary = boundaryRoot ?? _canonicalRoot;
            if (!IsWithinPath(resolved, boundary))
            {
                return null;
            }

            return resolved;
        }
        catch (Exception error) when (
            error is UnauthorizedAccessException or IOException or ArgumentException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static bool IsWithinPath(string candidate, string root)
    {
        var normalizedCandidate = Path.GetFullPath(candidate);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var rootWithSeparator = normalizedRoot + Path.DirectorySeparatorChar;
        return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static ToolAccessKind MapAccessKind(string toolId) => toolId switch
    {
        "files.list" => ToolAccessKind.ReadOnly,
        "files.glob" => ToolAccessKind.ReadOnly,
        "files.search" => ToolAccessKind.ReadOnly,
        "files.read" => ToolAccessKind.ReadOnly,
        "web.search" => ToolAccessKind.ReadOnly,
        "web.fetch" => ToolAccessKind.ReadOnly,
        _ => ToolAccessKind.Mutation,
    };

    private static void ValidateArguments(ToolProposal proposal)
    {
        var args = proposal.Arguments;
        if (args.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("arguments must be object");
        }

        try
        {
            _ = HarnessIntentCanonicalizer.Canonicalize(args);
        }
        catch (JsonException error)
        {
            throw new ArgumentException("invalid_arguments", error);
        }

        switch (proposal.ToolId)
        {
            case "files.list":
                OptionalString(args, "path", MaxFilePathLength);
                OptionalInt(args, "max_entries", 1, MaxDiscoveryEntries);
                break;
            case "files.glob":
                RequiredString(args, "pattern", MaxPatternLength);
                OptionalString(args, "path", MaxFilePathLength);
                OptionalInt(args, "max_results", 1, MaxDiscoveryResults);
                break;
            case "files.search":
                RequiredString(args, "query", MaxQueryLength);
                OptionalString(args, "path", MaxFilePathLength);
                OptionalString(args, "file_glob", MaxPatternLength);
                OptionalBool(args, "case_sensitive");
                OptionalInt(args, "max_results", 1, MaxDiscoveryResults);
                break;
            case "files.read":
                RequiredString(args, "path", MaxFilePathLength);
                OptionalLong(args, "offset", 0, long.MaxValue);
                OptionalInt(args, "limit", 1, MaxReadBytes);
                break;
            case "files.write":
                RequiredString(args, "path", MaxFilePathLength);
                var content = RequiredString(args, "content", MaxMutationBytes, allowEmpty: true);
                ValidateUtf8AndSize(content, MaxMutationBytes, "content_too_large");
                OptionalSha256(args, "expected_sha256");
                OptionalBool(args, "overwrite");
                break;
            case "files.edit":
                RequiredString(args, "path", MaxFilePathLength);
                var oldString = RequiredString(args, "old_string", MaxMutationBytes);
                if (oldString.Length == 0)
                {
                    throw new ArgumentException("old_string_empty");
                }

                ValidateUtf8AndSize(oldString, MaxMutationBytes, "content_too_large");
                var newString = OptionalString(args, "new_string", MaxMutationBytes, allowEmpty: true);
                if (newString is not null)
                {
                    ValidateUtf8AndSize(newString, MaxMutationBytes, "content_too_large");
                }

                OptionalInt(args, "expected_occurrences", 1, MaxExpectedOccurrences);
                OptionalSha256(args, "expected_sha256");
                break;
            case "files.delete":
                RequiredString(args, "path", MaxFilePathLength);
                OptionalSha256(args, "expected_sha256");
                break;
            case "files.restore":
                RequiredString(args, "recovery_id", 256);
                break;
            case "web.search":
                EnsureOnlyArguments(args, "query");
                RequiredString(args, "query", 512);
                break;
            case "web.fetch":
                EnsureOnlyArguments(args, "url");
                var rawUrl = RequiredString(args, "url", 2048);
                if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)
                    || uri.Scheme is not ("http" or "https")
                    || string.IsNullOrWhiteSpace(uri.Host)
                    || !string.IsNullOrEmpty(uri.UserInfo))
                {
                    throw new ArgumentException("invalid_url");
                }
                break;
        }
    }

    private static void EnsureOnlyArguments(JsonElement args, params string[] allowedNames)
    {
        var allowed = allowedNames.ToHashSet(StringComparer.Ordinal);
        if (args.EnumerateObject().Any(property => !allowed.Contains(property.Name)))
        {
            throw new ArgumentException("invalid_arguments");
        }
    }

    private static string RequiredString(JsonElement args, string name, int maxLength, bool allowEmpty = false)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"missing_{name}");
        }

        var text = value.GetString();
        if (text is null || text.Length > maxLength || (!allowEmpty && string.IsNullOrWhiteSpace(text)))
        {
            throw new ArgumentException($"invalid_{name}");
        }

        return text;
    }

    private static string? OptionalString(JsonElement args, string name, int maxLength, bool allowEmpty = false)
    {
        if (!args.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"invalid_{name}");
        }

        var text = value.GetString();
        if (text is null || text.Length > maxLength || (!allowEmpty && string.IsNullOrWhiteSpace(text)))
        {
            throw new ArgumentException($"invalid_{name}");
        }

        return text;
    }

    private static int? OptionalInt(JsonElement args, string name, int minimum, int maximum)
    {
        if (!args.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)
            || number < minimum || number > maximum)
        {
            throw new ArgumentException($"invalid_{name}");
        }

        return number;
    }

    private static long? OptionalLong(JsonElement args, string name, long minimum, long maximum)
    {
        if (!args.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number)
            || number < minimum || number > maximum)
        {
            throw new ArgumentException($"invalid_{name}");
        }

        return number;
    }

    private static bool? OptionalBool(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
        {
            throw new ArgumentException($"invalid_{name}");
        }

        return value.GetBoolean();
    }

    private static string? OptionalSha256(JsonElement args, string name)
    {
        var value = OptionalString(args, name, 64, allowEmpty: false);
        if (value is null)
        {
            return null;
        }

        if (!string.Equals(value, "missing", StringComparison.Ordinal)
            && (value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))))
        {
            throw new ArgumentException($"invalid_{name}");
        }

        return value;
    }

    private static void ValidateUtf8AndSize(string value, int maxBytes, string tooLargeCode)
    {
        try
        {
            if (StrictUtf8.GetByteCount(value) > maxBytes)
            {
                throw new ArgumentException(tooLargeCode);
            }
        }
        catch (EncoderFallbackException error)
        {
            throw new ArgumentException("invalid_utf8", error);
        }
    }

    private static int ReadOptionalInt(JsonObject args, string name, int defaultValue, int minimum, int maximum)
    {
        if (!args.TryGetPropertyValue(name, out var node))
        {
            return defaultValue;
        }

        if (node is not JsonValue value || !value.TryGetValue<int>(out var number)
            || number < minimum || number > maximum)
        {
            throw new ArgumentException($"invalid_{name}");
        }

        return number;
    }

    private static long ReadOptionalLong(JsonObject args, string name, long defaultValue, long minimum, long maximum)
    {
        if (!args.TryGetPropertyValue(name, out var node))
        {
            return defaultValue;
        }

        if (node is not JsonValue value || !value.TryGetValue<long>(out var number)
            || number < minimum || number > maximum)
        {
            throw new ArgumentException($"invalid_{name}");
        }

        return number;
    }

    private string ComputeCanonicalToken(ToolProposal proposal)
    {
        if (proposal.ToolId is "terminal.run" or "terminal.shell")
        {
            return ComputeTerminalIntentToken(proposal.ToolId, proposal.ToolVersion, proposal.Arguments);
        }

        // Canonical intent token is SHA256 of tool_id + version + recursively
        // canonicalized arguments. Object property order cannot change approval.
        var hash = HarnessIntentCanonicalizer.ComputeHash(
            proposal.ToolId,
            proposal.ToolVersion,
            proposal.Arguments);
        return $"canonical-{proposal.ToolId}-{hash[..16]}";
    }

    private static string ComputeApprovalIntentHash(ToolProposal proposal)
    {
        return HarnessIntentCanonicalizer.ComputeHash(
            proposal.ToolId,
            proposal.ToolVersion,
            proposal.Arguments);
    }

    // ------------------------------------------------------------------
    // Terminal v2: canonical intents, classification, and streamed execution.
    //
    // The Host gateway is the headless execution authority. Decision matrices mirror
    // the Broker command analyzers (version 1). The intent token binds tool identity,
    // requested executable, argv, canonical working directory, operation class,
    // network intent, and timeout; any changed field yields a different token, so a
    // prepared dispatch can never be replayed against a changed call. Structural
    // violations deny before any adapter work; recognized read/test/build/lint forms
    // allow in Standard within the standard window; everything else asks.
    // ------------------------------------------------------------------

    private ToolProposalDecision EvaluateTerminal(ToolProposal proposal)
    {
        var parsed = ParseTerminalArguments(proposal.ToolId, proposal.Arguments);
        if (parsed.Request is null)
        {
            return ToolProposalDecision.Deny(ToolAccessKind.Mutation, parsed.ErrorCode!);
        }

        var request = parsed.Request;
        var workingDirectory = ClassifyWorkingDirectory(request.WorkingDirectory);
        if (workingDirectory.ErrorCode is not null)
        {
            return ToolProposalDecision.Deny(
                MapTerminalAccessKind(request.OperationClass),
                workingDirectory.ErrorCode);
        }

        var analysis = HostTerminalClassifier.Analyze(proposal.ToolId, request.Executable, request.RawArgv);
        var operationClass = analysis.OperationClass;
        var accessKind = MapTerminalAccessKind(operationClass);

        string? resolvedExecutable = null;
        string? executableError = null;
        try
        {
            resolvedExecutable = proposal.ToolId == "terminal.run"
                ? ResolveExecutable(request.Executable!, workingDirectory.CanonicalPath)
                : ResolveShellExecutable(request.ShellRuntime ?? "powershell");
        }
        catch (UnauthorizedAccessException error)
        {
            // Unknown executables are a policy decision in Standard (they must
            // produce an approval request), not a transport/schema failure. Keep
            // the error until after the fallback has had a chance to ask.
            executableError = error.Message;
        }

        var matchedRule = resolvedExecutable is null
            ? null
            : FindMatchingRule(
                proposal,
                "terminal",
                relativePath: null,
                canonicalPath: workingDirectory.CanonicalPath,
                resolvedExecutable: resolvedExecutable,
                argv: request.ExecutionArgv(),
                canonicalCwd: workingDirectory.CanonicalPath,
                networkIntent: request.NetworkIntent ? "declared" : "none");
        var ruleAllowed = false;
        if (matchedRule is not null)
        {
            var ruleDecision = DecisionFromPolicy(proposal, accessKind, matchedRule.Decision, "rule_matched");
            if (ruleDecision.Decision != PolicyDecision.Allow)
            {
                return ruleDecision;
            }

            ruleAllowed = true;
        }

        var timeoutAsks = request.TimeoutMs > TerminalLimits.StandardApprovalThresholdMs;
        var fullAccess = (_policySnapshot?.Preset ?? _presetProvider(proposal.RunId))
            == AgentPermissionPreset.FullAccess;
        var externalDirectoryGranted = workingDirectory.Classification == TerminalWorkingDirectoryClass.OutsideWorkspace
            && _approvalGrants is { } externalGrants
            && externalGrants.IsExternalDirectoryGranted(
                proposal.RunId.Value,
                workingDirectory.CanonicalPath);

        string? askReason;
        if (ruleAllowed)
        {
            askReason = null;
        }
        else if (!fullAccess)
        {
            askReason = workingDirectory.Classification == TerminalWorkingDirectoryClass.Protected
                ? "protected_path"
                : workingDirectory.Classification == TerminalWorkingDirectoryClass.OutsideWorkspace
                    ? "cwd_outside_workspace"
                    : request.NetworkIntent
                        ? "declared_network"
                        : proposal.ToolId == "terminal.shell"
                ? "terminal_requires_rule"
                : operationClass switch
                {
                    TerminalOperationClass.ReadOnly or TerminalOperationClass.Test
                        or TerminalOperationClass.Build or TerminalOperationClass.Lint =>
                        timeoutAsks ? "extended_timeout_approval" : null,
                    TerminalOperationClass.GitMutation => "git_mutation",
                    TerminalOperationClass.ExternalMutation => "external_mutation",
                    _ => analysis.ReasonCode is "unrecognized_executable" or "missing_executable"
                        ? "unknown_executable"
                        : analysis.ReasonCode,
                };
        }
        else
        {
            askReason = timeoutAsks ? "extended_timeout_approval" : null;
        }

        // The external-directory grant only resolves the workspace boundary. A
        // declared network request, unknown executable, shell, or destructive
        // command still follows its own approval policy.
        if (externalDirectoryGranted && askReason == "cwd_outside_workspace")
        {
            askReason = null;
        }

        if (askReason is not null)
        {
            // An explicit user approval from the V1 endpoint consumes one pending ask for
            // this run; evaluation then continues to a normal Allow with the real
            // canonical intent token.
            if (!(_approvalGrants is { } grants
                && grants.TryConsume(proposal.RunId.Value, ComputeApprovalIntentHash(proposal))))
            {
                return ToolProposalDecision.Ask(accessKind, _ids.NewApprovalId());
            }
        }

        if (resolvedExecutable is null)
        {
            return ToolProposalDecision.Deny(accessKind, executableError ?? "executable_not_found");
        }

        var canonicalIntentToken = BuildTerminalIntentToken(
            proposal.ToolId,
            proposal.ToolVersion,
            resolvedExecutable,
            request.ExecutionArgv(),
            workingDirectory.CanonicalPath,
            request.NetworkIntent,
            request.TimeoutMs,
            operationClass);

        return ToolProposalDecision.Allow(accessKind, canonicalIntentToken);
    }

    private PreparedToolExecution PrepareTerminal(ApprovedToolExecution execution)
    {
        var parsed = ParseTerminalArguments(execution.Proposal.ToolId, execution.Proposal.Arguments);
        if (parsed.Request is null)
        {
            throw new ArgumentException(parsed.ErrorCode ?? "invalid_arguments");
        }

        var request = parsed.Request;
        // Preparation-time re-resolution: the evaluated intent must still resolve to a
        // legal, existing working directory and executable on this host right now.
        var workingDirectory = ClassifyWorkingDirectory(request.WorkingDirectory);
        if (workingDirectory.ErrorCode is not null)
        {
            throw new UnauthorizedAccessException("workspace_changed");
        }

        var resolvedExecutable = execution.Proposal.ToolId == "terminal.run"
            ? ResolveExecutable(request.Executable!, workingDirectory.CanonicalPath)
            : ResolveShellExecutable(request.ShellRuntime ?? "powershell");
        var operationClass = HostTerminalClassifier.Analyze(
            execution.Proposal.ToolId,
            request.Executable,
            request.RawArgv).OperationClass;
        var executionArgv = request.ExecutionArgv();
        var preparedIntentToken = BuildTerminalIntentToken(
            execution.Proposal.ToolId,
            execution.Proposal.ToolVersion,
            resolvedExecutable,
            executionArgv,
            workingDirectory.CanonicalPath,
            request.NetworkIntent,
            request.TimeoutMs,
            operationClass);
        if (!string.Equals(preparedIntentToken, execution.CanonicalIntentToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("canonical_token_mismatch");
        }

        var preparationToken = $"prepared-{preparedIntentToken}";

        _pendingTerminalSpecs[execution.ExecutionId.Value] = new PreparedTerminalSpec(
            execution.Proposal.RunId,
            execution.Proposal.ToolId,
            resolvedExecutable,
            execution.Proposal.ToolId == "terminal.run"
                ? request.Executable!
                : request.ShellRuntime ?? "powershell",
            executionArgv,
            workingDirectory.CanonicalPath,
            workingDirectory.Classification,
            request.NetworkIntent,
            request.TimeoutMs,
            MapTerminalAccessKind(operationClass),
            execution.Proposal.ToolCallId,
            execution.Proposal.ModelOrder,
            preparationToken);

        return new PreparedToolExecution(
            execution.ExecutionId,
            execution.Proposal.ToolCallId,
            execution.Proposal.ModelOrder,
            _pendingTerminalSpecs[execution.ExecutionId.Value].AccessKind,
            preparationToken,
            Array.Empty<PreparedPayload>());
    }

    private async Task<ToolExecutionReceipt> ExecuteTerminalAsync(
        PreparedToolExecution execution,
        JsonObject args,
        Func<ToolOutputChunk, ValueTask> publishDelta,
        CancellationToken cancellationToken)
    {
        if (!_pendingTerminalSpecs.TryRemove(execution.ExecutionId.Value, out var spec))
        {
            return FailedReceipt(execution, "decision_stale");
        }

        if (execution.ToolCallId != spec.ToolCallId
            || execution.ModelOrder != spec.ModelOrder
            || execution.AccessKind != spec.AccessKind
            || !string.Equals(execution.PreparationToken, spec.PreparationToken, StringComparison.Ordinal))
        {
            return FailedReceipt(execution, "decision_stale");
        }

        // Execution-time re-resolution: never start when the prepared identity no longer
        // matches the current filesystem reality.
        var workingDirectory = ClassifyWorkingDirectory(spec.WorkingDirectory);
        if (workingDirectory.ErrorCode is not null
            || !string.Equals(workingDirectory.CanonicalPath, spec.WorkingDirectory, StringComparison.OrdinalIgnoreCase)
            || workingDirectory.Classification != spec.WorkingDirectoryClass)
        {
            return FailedReceipt(execution, "decision_stale");
        }

        string resolvedExecutable;
        try
        {
            resolvedExecutable = spec.ToolId == "terminal.run"
                ? ResolveExecutable(spec.ResolutionInput, spec.WorkingDirectory)
                : ResolveShellExecutable(spec.ResolutionInput);
        }
        catch (UnauthorizedAccessException)
        {
            return FailedReceipt(execution, "decision_stale");
        }

        if (!string.Equals(resolvedExecutable, spec.ResolvedExecutable, StringComparison.OrdinalIgnoreCase))
        {
            return FailedReceipt(execution, "decision_stale");
        }

        var mutationCapable = spec.AccessKind == ToolAccessKind.Mutation;
        ThrowIfExecutionCancelled(execution.ExecutionId, cancellationToken);
        var active = new ActiveTerminalExecution(spec.RunId, execution.ExecutionId);
        _activeTerminalExecutions[execution.ExecutionId.Value] = active;
        try
        {
            using var job = new RuAgent.Hosting.OwnedProcessTree();
            ThrowIfExecutionCancelled(execution.ExecutionId, cancellationToken);
            var process = StartTerminalProcess(resolvedExecutable, spec);
            if (process is null)
            {
                return FailedReceipt(execution, "process_start_failed");
            }

            active.Process = process;
            job.Assign(process);

            var drain = new TerminalDrainState();
            var stdoutBuffer = new GatewayHeadTailBuffer();
            var stderrBuffer = new GatewayHeadTailBuffer();
            var stdoutTask = DrainStreamAsync(
                process.StandardOutput,
                ToolOutputStream.Stdout,
                stdoutBuffer,
                drain,
                active,
                publishDelta);
            var stderrTask = DrainStreamAsync(
                process.StandardError,
                ToolOutputStream.Stderr,
                stderrBuffer,
                drain,
                active,
                publishDelta);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            ToolExecutionOutcome outcome;
            string code;
            var timedOut = false;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, active.Token);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(spec.TimeoutMs, 1, TerminalLimits.HardMaxTimeoutMs)));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Distinguish an external cancellation from our own timeout window.
                timedOut = !cancellationToken.IsCancellationRequested && !active.CancellationRequested;
                await KillTreeAsync(process, job).ConfigureAwait(false);
            }

            // Classify only after the process is gone: an external cancel or our own tree
            // kill makes the process exit normally, so cancellation is checked first.
            if (cancellationToken.IsCancellationRequested || active.CancellationRequested)
            {
                outcome = ToolExecutionOutcome.Cancelled;
                code = "process_cancelled";
            }
            else if (timedOut)
            {
                outcome = ToolExecutionOutcome.TimedOut;
                code = "process_timed_out";
            }
            else
            {
                outcome = process.ExitCode == 0 ? ToolExecutionOutcome.Succeeded : ToolExecutionOutcome.Failed;
                code = process.ExitCode == 0 ? "ok" : "process_exit_nonzero";
            }

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            stopwatch.Stop();

            var output = new JsonObject
            {
                ["exit_code"] = process.HasExited ? process.ExitCode : null,
                ["duration_ms"] = (int)Math.Min(int.MaxValue, stopwatch.Elapsed.TotalMilliseconds),
                ["working_directory"] = ToWorkspaceRelative(spec.WorkingDirectory),
                ["stdout_head"] = stdoutBuffer.Head,
                ["stdout_tail"] = stdoutBuffer.Tail,
                ["stderr_head"] = stderrBuffer.Head,
                ["stderr_tail"] = stderrBuffer.Tail,
                ["stdout_bytes"] = stdoutBuffer.TotalBytes,
                ["stderr_bytes"] = stderrBuffer.TotalBytes,
                ["stdout_dropped_bytes"] = Math.Max(0, stdoutBuffer.TotalBytes - stdoutBuffer.RetainedBytes),
                ["stderr_dropped_bytes"] = Math.Max(0, stderrBuffer.TotalBytes - stderrBuffer.RetainedBytes),
                ["frames_dropped"] = drain.FramesDropped,
                ["truncated"] = stdoutBuffer.TotalBytes > stdoutBuffer.RetainedBytes
                    || stderrBuffer.TotalBytes > stderrBuffer.RetainedBytes,
            };
            var outputBytes = Encoding.UTF8.GetBytes(output.ToJsonString());
            return new ToolExecutionReceipt(
                execution.ExecutionId,
                execution.ToolCallId,
                outcome,
                code,
                null,
                sideEffectsPossible: mutationCapable
                    || outcome is ToolExecutionOutcome.Cancelled or ToolExecutionOutcome.TimedOut,
                modelContent: outputBytes,
                contentEncoding: HarnessPayloadEncoding.Json);
        }
        finally
        {
            _activeTerminalExecutions.TryRemove(execution.ExecutionId.Value, out _);
        }
    }

    private static System.Diagnostics.Process? StartTerminalProcess(
        string resolvedExecutable,
        PreparedTerminalSpec spec)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = resolvedExecutable,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in spec.Argv)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // ProcessStartInfo inherits the entire host environment by default. Clear it
        // before copying the allowlisted, secret-filtered snapshot so a token added to
        // the host after BuildChildEnvironment cannot leak into the child.
        startInfo.Environment.Clear();
        foreach (var pair in BuildChildEnvironment())
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        try
        {
            return System.Diagnostics.Process.Start(startInfo);
        }
        catch (Exception error) when (
            error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task KillTreeAsync(
        System.Diagnostics.Process process,
        RuAgent.Hosting.OwnedProcessTree job)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception error) when (
            error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        job.Dispose();
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private async Task DrainStreamAsync(
        System.IO.StreamReader reader,
        ToolOutputStream stream,
        GatewayHeadTailBuffer buffer,
        TerminalDrainState drain,
        ActiveTerminalExecution active,
        Func<ToolOutputChunk, ValueTask> publishDelta)
    {
        var charBuffer = new char[4_096];
        while (true)
        {
            var count = await reader.ReadAsync(charBuffer).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            var text = new string(charBuffer, 0, count);
            buffer.Append(text);
            var chunkSequence = drain.NextSequence();
            try
            {
                await publishDelta(new ToolOutputChunk(
                    active.ExecutionId,
                    chunkSequence,
                    stream,
                    text)).ConfigureAwait(false);
            }
            catch (Exception error) when (
                error is OperationCanceledException or InvalidOperationException)
            {
                drain.DropFrame();
            }
        }
    }

    private sealed class TerminalDrainState
    {
        private long _sequence;
        private long _dropped;

        public long FramesDropped => Interlocked.Read(ref _dropped);

        public long NextSequence() => Interlocked.Increment(ref _sequence);

        public void DropFrame() => Interlocked.Increment(ref _dropped);
    }

    internal static IReadOnlyDictionary<string, string> BuildChildEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && !IsSecretEnvironmentName(name))
            {
                environment[name] = entry.Value as string ?? string.Empty;
            }
        }

        environment["GIT_TERMINAL_PROMPT"] = "0";
        environment["GCM_INTERACTIVE"] = "Never";
        environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        environment["DOTNET_NOLOGO"] = "1";
        return environment;
    }

    private static bool IsSecretEnvironmentName(string name)
    {
        if (name.StartsWith("RU_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("WORKER_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("BROKER_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var flattened = name.Replace("_", string.Empty).Replace("-", string.Empty);
        foreach (var fragment in SecretEnvironmentNameFragments)
        {
            if (flattened.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly IReadOnlyList<string> SecretEnvironmentNameFragments =
    [
        "token", "secret", "password", "passwd", "apikey", "providerkey",
        "credential", "connectionstring", "privatekey", "accesskey",
        "refreshkey", "authkey",
    ];

    private static IReadOnlyList<string> SanitizeSearchDirectories()
    {
        var rawPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return rawPath
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Path.IsPathRooted)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static readonly string[] WindowsExecutableExtensions = [".com", ".exe", ".bat", ".cmd"];

    private string ResolveExecutable(string executable, string canonicalWorkingDirectory)
    {
        if (string.IsNullOrWhiteSpace(executable) || executable.Contains('\0'))
        {
            throw new UnauthorizedAccessException("executable_invalid");
        }

        if (Path.IsPathRooted(executable))
        {
            if (!File.Exists(executable))
            {
                throw new UnauthorizedAccessException("executable_not_found");
            }

            return CanonicalizeExistingPath(executable);
        }

        if (executable.Contains('/') || executable.Contains('\\'))
        {
            var combined = Path.GetFullPath(Path.Combine(canonicalWorkingDirectory, executable));
            if (!File.Exists(combined))
            {
                throw new UnauthorizedAccessException("executable_not_found");
            }

            return CanonicalizeExistingPath(combined);
        }

        foreach (var directory in _sanitizedSearchDirectories)
        {
            foreach (var extension in WindowsExecutableExtensions)
            {
                var candidate = Path.Combine(directory, executable + extension);
                if (File.Exists(candidate))
                {
                    return CanonicalizeExistingPath(candidate);
                }
            }
        }

        throw new UnauthorizedAccessException("executable_not_found");
    }

    private string ResolveShellExecutable(string runtime)
    {
        var executableName = runtime.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
            ? "pwsh.exe"
            : "powershell.exe";
        foreach (var directory in _sanitizedSearchDirectories)
        {
            var candidate = Path.Combine(directory, executableName);
            if (File.Exists(candidate))
            {
                return CanonicalizeExistingPath(candidate);
            }
        }

        var fallback = runtime.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetEnvironmentVariable("ProgramFiles")
            : Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            var knownPath = runtime.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(fallback, "PowerShell", "7", executableName)
                : Path.Combine(fallback, "WindowsPowerShell", "v1.0", executableName);
            if (File.Exists(knownPath))
            {
                return CanonicalizeExistingPath(knownPath);
            }
        }

        throw new UnauthorizedAccessException("executable_not_found");
    }

    private TerminalWorkingDirectory ClassifyWorkingDirectory(string requested)
    {
        if (requested.Contains('\0') || requested.Length > TerminalLimits.MaxWorkingDirectoryChars)
        {
            return TerminalWorkingDirectory.Invalid("cwd_invalid");
        }

        string candidate;
        try
        {
            candidate = Path.IsPathRooted(requested)
                ? Path.GetFullPath(requested)
                : Path.GetFullPath(Path.Combine(_canonicalRoot, requested));
        }
        catch (Exception error) when (
            error is ArgumentException or IOException or System.Security.SecurityException)
        {
            return TerminalWorkingDirectory.Invalid("cwd_invalid");
        }

        if (!Directory.Exists(candidate))
        {
            return TerminalWorkingDirectory.Invalid("working_directory_invalid", candidate);
        }

        string finalPath;
        try
        {
            finalPath = CanonicalizeExistingDirectory(candidate);
        }
        catch (Exception error) when (
            error is UnauthorizedAccessException or IOException or ArgumentException)
        {
            return TerminalWorkingDirectory.Invalid("working_directory_invalid", candidate);
        }

        var classification = IsProtectedWorkingDirectory(finalPath) || IsProtectedWorkspaceDirectory(finalPath)
            ? TerminalWorkingDirectoryClass.Protected
            : IsInsideWorkspace(finalPath)
                ? TerminalWorkingDirectoryClass.InsideWorkspace
                : TerminalWorkingDirectoryClass.OutsideWorkspace;
        return new TerminalWorkingDirectory(finalPath, classification, null);
    }

    private bool IsInsideWorkspace(string canonicalPath)
    {
        var rootWithSeparator = _canonicalRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _canonicalRoot
            : _canonicalRoot + Path.DirectorySeparatorChar;
        return canonicalPath.Equals(_canonicalRoot, StringComparison.OrdinalIgnoreCase)
            || canonicalPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsProtectedWorkspaceDirectory(string canonicalPath)
    {
        if (!IsInsideWorkspace(canonicalPath))
        {
            return false;
        }

        return IsProtectedPath(canonicalPath);
    }

    private static string CanonicalizeExistingDirectory(string path) =>
        OperatingSystem.IsWindows() ? ResolveFinalPath(path) : Path.GetFullPath(path);

    private static string CanonicalizeExistingPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            return ResolveFinalPath(fullPath);
        }

        FileSystemInfo info = Directory.Exists(fullPath)
            ? new DirectoryInfo(fullPath)
            : new FileInfo(fullPath);
        return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? fullPath;
    }

    private static bool IsProtectedWorkingDirectory(string canonicalPath)
    {
        if (string.IsNullOrEmpty(canonicalPath) || !OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var finalPath = ResolveFinalPath(canonicalPath);
            foreach (var protectedLocation in ProtectedLocations())
            {
                var baseDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectedLocation));
                if (finalPath.Equals(baseDirectory, StringComparison.OrdinalIgnoreCase)
                    || finalPath.StartsWith(baseDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception error) when (
            error is UnauthorizedAccessException or IOException or ArgumentException)
        {
            return true;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle OpenDirectoryHandle(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle hFile,
        StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    private const uint QueryOnlyDesiredAccess = 0;
    private const uint FileShareReadWrite = 0x3;
    private const uint OpenExistingDisposition = 0x3;
    private const uint BackupSemanticsFlag = 0x02000000;

    private static string ResolveFinalPath(string candidate)
    {
        using var handle = OpenDirectoryHandle(
            ExtendedDevicePath(candidate),
            QueryOnlyDesiredAccess,
            FileShareReadWrite,
            IntPtr.Zero,
            OpenExistingDisposition,
            BackupSemanticsFlag,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new UnauthorizedAccessException("terminal_cwd_not_allowed");
        }

        var buffer = new StringBuilder(1_024);
        var required = GetFinalPathByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (required > (uint)buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)required));
            required = GetFinalPathByHandle(handle, buffer, required, 0);
        }

        if (required == 0)
        {
            throw new UnauthorizedAccessException("terminal_cwd_not_allowed");
        }

        return StripDevicePrefix(buffer.ToString());
    }

    private static string ExtendedDevicePath(string candidate)
    {
        if (candidate.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            return candidate;
        }

        return candidate.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase)
            ? @"\\?\UNC\" + candidate[2..]
            : @"\\?\" + candidate;
    }

    private static string StripDevicePrefix(string finalPath)
    {
        if (finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + finalPath[@"\\?\UNC\".Length..];
        }

        return finalPath.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            ? finalPath[@"\\?\".Length..]
            : finalPath;
    }

    private static IEnumerable<string> ProtectedLocations()
    {
        yield return ProtectedLocationValue("WINDIR", Environment.SpecialFolder.Windows);
        yield return ProtectedLocationValue("ProgramFiles", Environment.SpecialFolder.ProgramFiles);
    }

    private static string ProtectedLocationValue(string variable, Environment.SpecialFolder fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? Environment.GetFolderPath(fallback) : value;
    }

    private string ToWorkspaceRelative(string canonicalPath)
    {
        var relative = Path.GetRelativePath(_canonicalRoot, canonicalPath);
        return relative == "." ? "." : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private string ToToolPath(string canonicalPath)
    {
        var normalized = Path.GetFullPath(canonicalPath);
        return IsInsideWorkspacePath(normalized)
            ? ToWorkspaceRelative(normalized)
            : normalized.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static ToolAccessKind MapTerminalAccessKind(TerminalOperationClass operationClass) =>
        operationClass == TerminalOperationClass.ReadOnly
            ? ToolAccessKind.ReadOnly
            : ToolAccessKind.Mutation;

    private string ComputeTerminalIntentToken(string toolId, string toolVersion, JsonElement arguments)
    {
        var parsed = ParseTerminalArguments(toolId, arguments);
        if (parsed.Request is null)
        {
            return ComputeInvalidTerminalIntentToken(toolId, toolVersion, parsed.ErrorCode);
        }

        var request = parsed.Request;
        var workingDirectory = ClassifyWorkingDirectory(request.WorkingDirectory);
        if (workingDirectory.ErrorCode is not null)
        {
            return ComputeInvalidTerminalIntentToken(toolId, toolVersion, workingDirectory.ErrorCode);
        }

        try
        {
            var resolvedExecutable = toolId == "terminal.run"
                ? ResolveExecutable(request.Executable!, workingDirectory.CanonicalPath)
                : ResolveShellExecutable(request.ShellRuntime ?? "powershell");
            var analysis = HostTerminalClassifier.Analyze(toolId, request.Executable, request.RawArgv);
            return BuildTerminalIntentToken(
                toolId,
                toolVersion,
                resolvedExecutable,
                request.ExecutionArgv(),
                workingDirectory.CanonicalPath,
                request.NetworkIntent,
                request.TimeoutMs,
                analysis.OperationClass);
        }
        catch (UnauthorizedAccessException error)
        {
            return ComputeInvalidTerminalIntentToken(toolId, toolVersion, error.Message);
        }
    }

    private string BuildTerminalIntentToken(
        string toolId,
        string toolVersion,
        string resolvedExecutable,
        IReadOnlyList<string> argv,
        string canonicalWorkingDirectory,
        bool networkIntent,
        int timeoutMs,
        TerminalOperationClass operationClass)
    {
        var hash = TerminalIntentFingerprint.ComputeForCapability(
            toolId,
            toolVersion,
            resolvedExecutable,
            argv,
            canonicalWorkingDirectory,
            networkIntent,
            timeoutMs,
            operationClass);
        return $"canonical-terminal-{hash}";
    }

    private static string ComputeInvalidTerminalIntentToken(
        string toolId,
        string toolVersion,
        string? errorCode)
    {
        var invalidHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes($"invalid-v2|{toolId}|{toolVersion}|{errorCode}"))).ToLowerInvariant();
        return $"canonical-terminal-{invalidHash}";
    }

    private static TerminalArgumentParseResult ParseTerminalArguments(string toolId, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return TerminalArgumentParseResult.Deny("schema_invalid");
        }

        return toolId == "terminal.shell"
            ? ParseShellArguments(arguments)
            : ParseRunArguments(arguments);
    }

    private static TerminalArgumentParseResult ParseRunArguments(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("executable", out var executableNode)
            || executableNode.ValueKind != JsonValueKind.String)
        {
            return TerminalArgumentParseResult.Deny("executable_invalid");
        }

        var executable = executableNode.GetString();
        if (string.IsNullOrWhiteSpace(executable)
            || executable.Length > TerminalLimits.MaxExecutableChars
            || executable.Contains('\0'))
        {
            return TerminalArgumentParseResult.Deny("executable_invalid");
        }

        var argv = Array.Empty<string>();
        if (arguments.TryGetProperty("arguments", out var argvNode) && argvNode.ValueKind != JsonValueKind.Null)
        {
            if (argvNode.ValueKind != JsonValueKind.Array)
            {
                return TerminalArgumentParseResult.Deny("schema_invalid");
            }

            var list = new List<string>();
            foreach (var item in argvNode.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    return TerminalArgumentParseResult.Deny("schema_invalid");
                }

                var element = item.GetString() ?? string.Empty;
                if (element.Contains('\0'))
                {
                    return TerminalArgumentParseResult.Deny("argument_nul_rejected");
                }

                if (element.Length > TerminalLimits.MaxArgumentChars)
                {
                    return TerminalArgumentParseResult.Deny("argument_too_long");
                }

                list.Add(element);
            }

            if (list.Count > TerminalLimits.MaxArguments)
            {
                return TerminalArgumentParseResult.Deny("argv_too_long");
            }

            foreach (var element in list)
            {
                if (DeniedArgumentTokens.TryGetValue(element, out var deniedCode))
                {
                    return TerminalArgumentParseResult.Deny(deniedCode);
                }
            }

            argv = list.ToArray();
        }

        return FinishParse(arguments, "terminal.run", executable, argv, shellRuntime: null);
    }

    private static TerminalArgumentParseResult ParseShellArguments(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("script", out var scriptNode) || scriptNode.ValueKind != JsonValueKind.String)
        {
            return TerminalArgumentParseResult.Deny("script_invalid");
        }

        var script = scriptNode.GetString();
        if (string.IsNullOrWhiteSpace(script))
        {
            return TerminalArgumentParseResult.Deny("script_invalid");
        }

        if (script.Length > TerminalLimits.MaxScriptChars)
        {
            return TerminalArgumentParseResult.Deny("script_too_long");
        }

        if (script.Contains('\0'))
        {
            return TerminalArgumentParseResult.Deny("script_nul_rejected");
        }

        string? shellRuntime = null;
        if (arguments.TryGetProperty("shell", out var shellNode) && shellNode.ValueKind == JsonValueKind.String)
        {
            shellRuntime = shellNode.GetString();
            if (shellRuntime is not null
                && !shellRuntime.Equals("powershell", StringComparison.OrdinalIgnoreCase)
                && !shellRuntime.Equals("pwsh", StringComparison.OrdinalIgnoreCase))
            {
                return TerminalArgumentParseResult.Deny("unsupported_shell_runtime");
            }
        }

        return FinishParse(arguments, "terminal.shell", null, [script], shellRuntime);
    }

    private static TerminalArgumentParseResult FinishParse(
        JsonElement arguments,
        string toolId,
        string? executable,
        string[] argv,
        string? shellRuntime)
    {
        var workingDirectory = ".";
        if (arguments.TryGetProperty("working_directory", out var wdNode) && wdNode.ValueKind == JsonValueKind.String)
        {
            var value = wdNode.GetString();
            if (string.IsNullOrWhiteSpace(value) || value.Length > TerminalLimits.MaxWorkingDirectoryChars)
            {
                return TerminalArgumentParseResult.Deny("cwd_invalid");
            }

            workingDirectory = value;
        }

        var timeoutMs = TerminalLimits.DefaultTimeoutMs;
        if (arguments.TryGetProperty("timeout_ms", out var timeoutNode)
            && (timeoutNode.ValueKind == JsonValueKind.Number))
        {
            int parsedTimeout;
            if (timeoutNode.TryGetInt32(out var intTimeout))
            {
                parsedTimeout = intTimeout;
            }
            else if (timeoutNode.TryGetInt64(out var longTimeout)
                && longTimeout is >= int.MinValue and <= int.MaxValue)
            {
                parsedTimeout = (int)longTimeout;
            }
            else
            {
                return TerminalArgumentParseResult.Deny("timeout_invalid");
            }

            timeoutMs = parsedTimeout;
        }

        if (timeoutMs < 1)
        {
            return TerminalArgumentParseResult.Deny("timeout_invalid");
        }

        if (timeoutMs > TerminalLimits.HardMaxTimeoutMs)
        {
            return TerminalArgumentParseResult.Deny("timeout_exceeds_hard_max");
        }

        var networkIntent = false;
        if (arguments.TryGetProperty("network_intent", out var networkNode)
            && networkNode.ValueKind == JsonValueKind.True)
        {
            networkIntent = true;
        }

        return new TerminalArgumentParseResult(
            new TerminalRequestShape(
                toolId,
                executable,
                argv,
                workingDirectory,
                networkIntent,
                timeoutMs,
                TerminalOperationClass.Unknown,
                shellRuntime),
            null);
    }

    private static readonly FrozenDictionary<string, string> DeniedArgumentTokens =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["--tty"] = "interactive_flag_rejected",
            ["--interactive"] = "interactive_flag_rejected",
            ["/interactive"] = "interactive_flag_rejected",
            ["sudo"] = "password_prompt_rejected",
            ["su"] = "password_prompt_rejected",
            ["runas"] = "password_prompt_rejected",
            ["--ask-pass"] = "password_prompt_rejected",
            ["--password"] = "password_prompt_rejected",
            ["--prompt"] = "password_prompt_rejected",
            ["--daemon"] = "background_daemon_rejected",
            ["--detach"] = "background_daemon_rejected",
            ["-daemon"] = "background_daemon_rejected",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static ToolExecutionReceipt FailedReceipt(PreparedToolExecution execution, string code)
    {
        var errorJson = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = code }));
        return new ToolExecutionReceipt(
            execution.ExecutionId,
            execution.ToolCallId,
            ToolExecutionOutcome.Failed,
            code,
            null,
            sideEffectsPossible: false,
            modelContent: errorJson,
            contentEncoding: HarnessPayloadEncoding.Json);
    }

    /// <summary>
    /// Host-side mirror of the Broker command analyzer registry (version 1).
    /// Purely syntactic: no regex parser claims an arbitrary shell script safe, and
    /// unrecognized executables always produce Unknown, which asks in Standard.
    /// </summary>
    internal static class HostTerminalClassifier
    {
        public static (TerminalOperationClass OperationClass, string ReasonCode) Analyze(
            string toolId,
            string? executable,
            IReadOnlyList<string> argv)
        {
            if (toolId == "terminal.shell")
            {
                return (TerminalOperationClass.Unknown, "terminal_requires_rule");
            }

            var baseName = BaseName(executable ?? string.Empty);
            return baseName switch
            {
                "git" => AnalyzeGit(argv),
                "dotnet" => AnalyzeDotnet(argv),
                "python" or "python3" or "py" or "pytest" or "ruff" or "mypy" or "pip" or "pip3" =>
                    AnalyzePythonFamily(baseName, argv),
                "rg" => AnalyzeRipgrep(argv),
                _ => (TerminalOperationClass.Unknown, "unrecognized_executable"),
            };
        }

        private static (TerminalOperationClass, string) AnalyzeGit(IReadOnlyList<string> argv)
        {
            var index = 0;
            while (index < argv.Count && argv[index].StartsWith('-'))
            {
                if (string.Equals(argv[index], "--no-optional-locks", StringComparison.Ordinal))
                {
                    index++;
                    break;
                }

                return (TerminalOperationClass.Unknown, "unsupported_global_flags");
            }

            if (index >= argv.Count)
            {
                return (TerminalOperationClass.Unknown, "missing_verb");
            }

            var verb = argv[index];
            var rest = argv.Skip(index + 1).ToArray();
            if (rest.Any(argument => argument.StartsWith("--output", StringComparison.Ordinal)))
            {
                return (TerminalOperationClass.GitMutation, "git_output_redirect");
            }

            switch (verb)
            {
                case "status":
                case "diff":
                case "log":
                case "show":
                case "rev-parse":
                case "describe":
                case "shortlog":
                case "reflog":
                case "ls-files":
                case "grep":
                case "cat-file":
                    return (TerminalOperationClass.ReadOnly, "git_read_only");
                case "branch":
                    return rest.All(flag => string.Equals(flag, "--show-current", StringComparison.Ordinal))
                        ? (TerminalOperationClass.ReadOnly, "git_read_only")
                        : (TerminalOperationClass.GitMutation, "git_mutation");
                case "push":
                case "fetch":
                case "pull":
                case "clone":
                case "ls-remote":
                case "remote":
                    return (TerminalOperationClass.ExternalMutation, "git_network_external");
                default:
                    return IsKnownGitMutation(verb)
                        ? (TerminalOperationClass.GitMutation, "git_mutation")
                        : (TerminalOperationClass.Unknown, "unknown_git_verb");
            }
        }

        private static bool IsKnownGitMutation(string verb) => verb switch
        {
            "commit" or "add" or "rm" or "mv" or "merge" or "rebase" or "reset" or "checkout" or
            "switch" or "restore" or "clean" or "stash" or "tag" or "cherry-pick" or "revert" or
            "am" or "apply" or "bisect" or "config" or "worktree" or "submodule" or "gc" or
            "prune" or "replace" or "notes" or "filter-branch" or "format-patch" => true,
            _ => false,
        };

        private static (TerminalOperationClass, string) AnalyzeDotnet(IReadOnlyList<string> argv)
        {
            if (argv.Count == 0)
            {
                return (TerminalOperationClass.Unknown, "missing_verb");
            }

            return argv[0] switch
            {
                "test" or "vstest" => (TerminalOperationClass.Test, "dotnet_test"),
                "build" => (TerminalOperationClass.Build, "dotnet_build"),
                "--version" or "--info" or "--list-sdks" or "--list-runtimes" or "-h" or "--help" =>
                    (TerminalOperationClass.ReadOnly, "dotnet_diagnostics"),
                "restore" or "publish" or "push" or "pack" or "new" or "add" or "remove" or
                "tool" or "workload" or "store" or "format" =>
                    (TerminalOperationClass.ExternalMutation, "dotnet_external_mutation"),
                _ => (TerminalOperationClass.Unknown, "unknown_dotnet_verb"),
            };
        }

        private static (TerminalOperationClass, string) AnalyzePythonFamily(
            string baseName,
            IReadOnlyList<string> argv)
        {
            switch (baseName)
            {
                case "pytest":
                    return AnalyzePytest(argv);
                case "ruff":
                    return AnalyzeRuff(argv);
                case "mypy":
                    return (TerminalOperationClass.Lint, "python_lint");
                case "pip" or "pip3":
                    return (TerminalOperationClass.ExternalMutation, "python_pip_install");
                default:
                    return AnalyzePythonModule(argv);
            }
        }

        private static (TerminalOperationClass, string) AnalyzePythonModule(IReadOnlyList<string> argv)
        {
            for (var index = 0; index + 1 < argv.Count; index++)
            {
                if (!string.Equals(argv[index], "-m", StringComparison.Ordinal))
                {
                    continue;
                }

                return argv[index + 1] switch
                {
                    "pytest" => AnalyzePytest(argv.Skip(index + 2).ToArray()),
                    "ruff" => AnalyzeRuff(argv.Skip(index + 2).ToArray()),
                    "mypy" => (TerminalOperationClass.Lint, "python_lint"),
                    "pip" or "pip3" => (TerminalOperationClass.ExternalMutation, "python_pip_install"),
                    _ => (TerminalOperationClass.Unknown, "python_module_execution_asks"),
                };
            }

            return (TerminalOperationClass.Unknown, "python_arbitrary_execution_asks");
        }

        private static (TerminalOperationClass, string) AnalyzePytest(IReadOnlyList<string> argv)
        {
            for (var index = 0; index < argv.Count; index++)
            {
                var argument = argv[index];
                if (argument is "--pdb" or "--trace")
                {
                    return (TerminalOperationClass.Unknown, "pytest_interactive_asks");
                }

                if (argument == "-p")
                {
                    if (index + 1 >= argv.Count
                        || !string.Equals(argv[index + 1], "no:cacheprovider", StringComparison.Ordinal))
                    {
                        return (TerminalOperationClass.Unknown, "pytest_interactive_asks");
                    }

                    index++;
                }
            }

            return (TerminalOperationClass.Test, "python_test");
        }

        private static (TerminalOperationClass, string) AnalyzeRuff(IReadOnlyList<string> argv)
        {
            if (argv.Count == 0 || !string.Equals(argv[0], "check", StringComparison.Ordinal))
            {
                return (TerminalOperationClass.Unknown, "ruff_subcommand_asks");
            }

            if (argv.Any(argument => argument is "--fix" or "--unsafe-fixes" or "--fix-only"))
            {
                return (TerminalOperationClass.ExternalMutation, "ruff_fix_mutates");
            }

            return (TerminalOperationClass.Lint, "python_lint");
        }

        private static (TerminalOperationClass, string) AnalyzeRipgrep(IReadOnlyList<string> argv)
        {
            for (var index = 0; index < argv.Count; index++)
            {
                var argument = argv[index];
                if (argument is "-r" || argument.StartsWith("--replace", StringComparison.Ordinal))
                {
                    return (TerminalOperationClass.Unknown, "ripgrep_replacement_requires_approval");
                }

                if (argument is "--pre" or "--pre-glob"
                    || argument.StartsWith("--pre=", StringComparison.Ordinal))
                {
                    return (TerminalOperationClass.Unknown, "ripgrep_preprocessor_requires_approval");
                }

                if (argument is "--replace" or "--pre" or "--pre-glob")
                {
                    index++;
                }
            }

            return (TerminalOperationClass.ReadOnly, "ripgrep_search");
        }

        private static string BaseName(string resolvedExecutable)
        {
            var name = Path.GetFileName(resolvedExecutable.TrimEnd());
            foreach (var extension in new[] { ".exe", ".cmd", ".bat" })
            {
                if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    return name[..^extension.Length];
                }
            }

            return name;
        }
    }

    private sealed record TerminalArgumentParseResult(
        TerminalRequestShape? Request,
        string? ErrorCode)
    {
        public static TerminalArgumentParseResult Deny(string code) => new(null, code);
    }

    private sealed record TerminalRequestShape(
        string ToolId,
        string? Executable,
        IReadOnlyList<string> RawArgv,
        string WorkingDirectory,
        bool NetworkIntent,
        int TimeoutMs,
        TerminalOperationClass OperationClass,
        string? ShellRuntime)
    {
        public IReadOnlyList<string> ExecutionArgv() =>
            ToolId == "terminal.shell"
                ? new[] { "-NoProfile", "-NonInteractive", "-Command" }.Concat(RawArgv).ToArray()
                : RawArgv.ToArray();
    }

    private sealed record PreparedTerminalSpec(
        RunId RunId,
        string ToolId,
        string ResolvedExecutable,
        string ResolutionInput,
        IReadOnlyList<string> Argv,
        string WorkingDirectory,
        TerminalWorkingDirectoryClass WorkingDirectoryClass,
        bool NetworkIntent,
        int TimeoutMs,
        ToolAccessKind AccessKind,
        ToolCallId ToolCallId,
        int ModelOrder,
        string PreparationToken);

    private enum TerminalWorkingDirectoryClass
    {
        InsideWorkspace,
        OutsideWorkspace,
        Protected,
    }

    private sealed record TerminalWorkingDirectory(
        string CanonicalPath,
        TerminalWorkingDirectoryClass Classification,
        string? ErrorCode)
    {
        public static TerminalWorkingDirectory Invalid(string errorCode, string canonicalPath = "") =>
            new(canonicalPath, TerminalWorkingDirectoryClass.InsideWorkspace, errorCode);
    }

    private sealed class ActiveTerminalExecution
    {
        private readonly CancellationTokenSource _cancellation = new();

        public ActiveTerminalExecution(RunId runId, ExecutionId executionId)
        {
            RunId = runId;
            ExecutionId = executionId;
        }

        public RunId RunId { get; }

        public ExecutionId ExecutionId { get; }

        public System.Diagnostics.Process? Process { get; set; }

        public bool CancellationRequested => _cancellation.IsCancellationRequested;

        public CancellationToken Token => _cancellation.Token;

        public void Cancel()
        {
            _cancellation.Cancel();
            try
            {
                if (Process is { HasExited: false } process)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception error) when (
                error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }
    }

    private sealed class GatewayHeadTailBuffer(int capacityChars = 8_000)
    {
        private readonly object _lock = new();
        private string _head = string.Empty;
        private string _tail = string.Empty;

        public string Head
        {
            get { lock (_lock) { return _head; } }
        }

        public string Tail
        {
            get { lock (_lock) { return _tail; } }
        }

        public long TotalBytes { get; private set; }

        public long RetainedBytes
        {
            get
            {
                lock (_lock)
                {
                    return Encoding.UTF8.GetByteCount(_head) + Encoding.UTF8.GetByteCount(_tail);
                }
            }
        }

        public void Append(string text)
        {
            var bytes = Encoding.UTF8.GetByteCount(text);
            lock (_lock)
            {
                TotalBytes += bytes;
                if (_head.Length < capacityChars)
                {
                    var headBudget = capacityChars - _head.Length;
                    _head += headBudget >= text.Length ? text : text[..headBudget];
                }

                _tail = _tail.Length + text.Length <= capacityChars
                    ? _tail + text
                    : (_tail + text)[^capacityChars..];
            }
        }
    }

    private sealed class GatewayIdGenerator : IIdGenerator
    {
        private readonly IIdGenerator _inner = new CryptographicIdGenerator();

        public RunId NewRunId() => _inner.NewRunId();
        public ModelStepId NewModelStepId() => _inner.NewModelStepId();
        public ToolCallId NewToolCallId() => _inner.NewToolCallId();
        public ExecutionId NewExecutionId() => _inner.NewExecutionId();
        public ApprovalId NewApprovalId() => _inner.NewApprovalId();
        public TrustRequestId NewTrustRequestId() => _inner.NewTrustRequestId();
        public PolicyRuleId NewPolicyRuleId() => _inner.NewPolicyRuleId();
        public RuAgent.Harness.Core.EventId NewEventId() => _inner.NewEventId();
        public PayloadId NewPayloadId() => _inner.NewPayloadId();
    }
}
