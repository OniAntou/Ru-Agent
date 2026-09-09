using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Approval;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Reconciliation;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Policy;
using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Harness.Persistence.Workspace;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using RuBackend.Agent;
using RuBackend.Business.Sessions;
using RuBackend.Business.Settings;
using RuBackend.Harness.Policy;
using RuBackend.Harness.Workspace;
using RuBackend.Providers;
using RuAgent.Runtime.Adapters.Models;

namespace RuBackend.Harness;

public sealed partial class AgentV1Service
{
    public async Task<AgentServiceResult> GetWorkspaceConcurrencyAsync(string workspaceIdRaw, CancellationToken cancellationToken)
    {
        if (!IsWorkspaceId(workspaceIdRaw) || _workspaceCoordinatorStore is null)
        {
            return new AgentServiceResult(400, new AgentErrorResponse("invalid_workspace_id"));
        }

        try
        {
            var workspaceId = new WorkspaceId(workspaceIdRaw);
            var coordinator = await _workspaceCoordinatorStore.GetOrCreateAsync(workspaceId, cancellationToken).ConfigureAwait(false);
            var activeClaims = await _workspaceCoordinatorStore.CountActiveClaimsAsync(workspaceId, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            return new AgentServiceResult(
                200,
                new WorkspaceConcurrencyResponse(
                    workspaceIdRaw,
                    new WorkspaceConcurrencyPolicy(
                        coordinator.Mode,
                        coordinator.MaxConcurrentRuns,
                        coordinator.LeaseTtlSeconds,
                        coordinator.MergeTextConflicts,
                        coordinator.ExternalWriterDetection),
                    activeClaims,
                    coordinator.UpdatedAtUtc.ToString("O")));
        }
        catch (HarnessStoreException exception) when (exception.Code == "workspace_not_found")
        {
            return new AgentServiceResult(404, new AgentErrorResponse(exception.Code));
        }
    }

    public async Task<AgentServiceResult> UpdateWorkspaceConcurrencyAsync(
        string workspaceIdRaw,
        WorkspaceConcurrencyUpdateRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsWorkspaceId(workspaceIdRaw) || request is null || _workspaceCoordinatorStore is null)
        {
            return new AgentServiceResult(400, new AgentErrorResponse("invalid_workspace_concurrency_request"));
        }

        try
        {
            var workspaceId = new WorkspaceId(workspaceIdRaw);
            var current = await _workspaceCoordinatorStore.GetOrCreateAsync(workspaceId, cancellationToken).ConfigureAwait(false);
            var updated = await _workspaceCoordinatorStore.UpdatePolicyAsync(
                workspaceId,
                current.Version,
                request.Mode,
                request.MaxConcurrentRuns,
                request.LeaseTtlSeconds,
                request.MergeTextConflicts,
                request.ExternalWriterDetection,
                cancellationToken).ConfigureAwait(false);
            var activeClaims = await _workspaceCoordinatorStore.CountActiveClaimsAsync(workspaceId, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            return new AgentServiceResult(
                200,
                new WorkspaceConcurrencyResponse(
                    workspaceIdRaw,
                    new WorkspaceConcurrencyPolicy(
                        updated.Mode,
                        updated.MaxConcurrentRuns,
                        updated.LeaseTtlSeconds,
                        updated.MergeTextConflicts,
                        updated.ExternalWriterDetection),
                    activeClaims,
                    updated.UpdatedAtUtc.ToString("O")));
        }
        catch (HarnessStoreException exception) when (exception.Code is "workspace_not_found" or "invalid_workspace_concurrency_policy" or "workspace_concurrency_version_conflict")
        {
            var status = exception.Code == "workspace_not_found" ? 404 : 409;
            return new AgentServiceResult(status, new AgentErrorResponse(exception.Code));
        }
    }

    public async Task<AgentServiceResult> ListWorkspaceChangesAsync(
        string workspaceIdRaw,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!IsWorkspaceId(workspaceIdRaw) || _workspaceCoordinatorStore is null)
        {
            return new AgentServiceResult(400, new AgentErrorResponse("invalid_workspace_id"));
        }

        try
        {
            var workspaceId = new WorkspaceId(workspaceIdRaw);
            var changes = await _workspaceCoordinatorStore.ListChangesAsync(workspaceId, afterSequence, limit, cancellationToken).ConfigureAwait(false);
            return new AgentServiceResult(
                200,
                new WorkspaceChangesResponse(
                    changes.Select(change => new WorkspaceChangeItem(
                        change.ChangeId,
                        change.WorkspaceId,
                        change.NormalizedPath,
                        change.CommitSequence,
                        change.Status,
                        change.BeforeSha256,
                        change.AfterSha256,
                        change.CreatedAtUtc.ToString("O"))).ToArray(),
                    changes.Count == limit ? changes[^1].CommitSequence.ToString(CultureInfo.InvariantCulture) : null));
        }
        catch (HarnessStoreException exception) when (exception.Code == "invalid_workspace_changes_query")
        {
            return new AgentServiceResult(400, new AgentErrorResponse(exception.Code));
        }
    }

    public async Task<AgentServiceResult> ListWorkspaceActivityAsync(
        string workspaceIdRaw,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!IsWorkspaceId(workspaceIdRaw) || _workspaceCoordinatorStore is null)
        {
            return new AgentServiceResult(400, new AgentErrorResponse("invalid_workspace_id"));
        }

        try
        {
            var workspaceId = new WorkspaceId(workspaceIdRaw);
            var changes = await _workspaceCoordinatorStore.ListChangesAsync(workspaceId, afterSequence, limit, cancellationToken).ConfigureAwait(false);
            var activity = changes.Select(change =>
            {
                var external = change.RunId is null;
                return new WorkspaceActivityItem(
                    change.ChangeId,
                    change.WorkspaceId,
                    external
                        ? "workspace_external_change_detected"
                        : change.Status == "rejected" ? "workspace_conflict_detected" : "workspace_file_changed",
                    change.NormalizedPath,
                    external ? WorkspaceWriterKind.UnknownExternal : WorkspaceWriterKind.RuPeer,
                    change.CommitSequence,
                    change.CreatedAtUtc.ToString("O"),
                    null);
            }).ToArray();
            var nextCursor = changes.Count == limit
                ? changes[^1].CommitSequence.ToString(CultureInfo.InvariantCulture)
                : null;
            return new AgentServiceResult(200, new WorkspaceActivityResponse(activity, nextCursor));
        }
        catch (HarnessStoreException exception) when (exception.Code == "invalid_workspace_changes_query")
        {
            return new AgentServiceResult(400, new AgentErrorResponse(exception.Code));
        }
    }

    private const long MaxRestoreSizeBytes = 16 * 1024 * 1024; // 16MB cap per restore
    private static readonly TimeSpan MinRestoreInterval = TimeSpan.FromSeconds(1); // Rate limit: max 1 restore/sec per workspace
    private static readonly ConcurrentDictionary<string, DateTimeOffset> _lastRestoreTime = new(StringComparer.Ordinal);

    public async Task<AgentServiceResult> RestoreWorkspaceChangeAsync(
        string workspaceIdRaw,
        string changeId,
        WorkspaceChangeRestoreRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsWorkspaceId(workspaceIdRaw)
            || _workspaceCoordinatorStore is null
            || string.IsNullOrWhiteSpace(changeId)
            || changeId.Length > 256)
        {
            return new AgentServiceResult(400, new AgentErrorResponse("invalid_workspace_change"));
        }

        if (request is null
            || request.ExpectedCurrentSha256 is null
            || request.ExpectedCurrentSha256.Length != 64
            || !IsLowerHex(request.ExpectedCurrentSha256))
        {
            return new AgentServiceResult(400, new AgentErrorResponse("invalid_expected_current_sha256"));
        }

        // Rate limiting per workspace
        var now = _time.UtcNow;
        if (_lastRestoreTime.TryGetValue(workspaceIdRaw, out var lastTime))
        {
            if (now - lastTime < MinRestoreInterval)
            {
                return new AgentServiceResult(429, new AgentErrorResponse("workspace_restore_rate_limited"));
            }
        }

        try
        {
            var workspaceId = new WorkspaceId(workspaceIdRaw);
            var stored = await _workspaceStore.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
            if (stored is null)
            {
                return new AgentServiceResult(404, new AgentErrorResponse("workspace_not_found"));
            }

            var envelope = await _workspaceCoordinatorStore.GetChangeEnvelopeAsync(workspaceId, changeId, cancellationToken).ConfigureAwait(false);
            if (envelope is null)
            {
                return new AgentServiceResult(404, new AgentErrorResponse("workspace_change_not_found"));
            }

            if (envelope.Change.Status == "restored")
            {
                return new AgentServiceResult(200, new WorkspaceChangeRestoreResponse(changeId, "restored", envelope.Change.BeforeSha256 ?? string.Empty, changeId));
            }

            var root = await _trustService.DecryptCanonicalRootAsync(stored, cancellationToken).ConfigureAwait(false);
            var target = ResolveWorkspaceFile(root, envelope.Change.NormalizedPath);
            if (ContainsReparsePointBoundary(root, target))
            {
                throw new UnauthorizedAccessException("workspace_reparse_boundary");
            }

            var currentSha = File.Exists(target)
                ? await ComputeFileSha256Async(target, cancellationToken).ConfigureAwait(false)
                : Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant();
            if (!string.Equals(currentSha, request.ExpectedCurrentSha256, StringComparison.Ordinal))
            {
                return new AgentServiceResult(409, new AgentErrorResponse("workspace_restore_stale"));
            }

            var protector = _workspaceJournalProtector ?? new WorkspaceJournalProtector(_keyStore);
            var plaintext = await protector.DecryptAsync(workspaceId, changeId, envelope.Change.NormalizedPath, envelope.Journal, cancellationToken).ConfigureAwait(false);
            if (!TryReadRestoreContent(plaintext.Span, out var restoredBytes))
            {
                return new AgentServiceResult(409, new AgentErrorResponse("workspace_restore_unavailable"));
            }

            // Cap restore size
            if (restoredBytes.Length > MaxRestoreSizeBytes)
            {
                return new AgentServiceResult(413, new AgentErrorResponse("workspace_restore_too_large"));
            }

            var restoredSha = Convert.ToHexString(SHA256.HashData(restoredBytes)).ToLowerInvariant();
            var restoreChangeId = $"chg_restore_{Guid.NewGuid():N}";
            var restorePayload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                restore_of = changeId,
                restored_sha256 = restoredSha,
                content_base64 = Convert.ToBase64String(restoredBytes),
            });
            var encrypted = await protector.EncryptAsync(workspaceId, restoreChangeId, envelope.Change.NormalizedPath, restorePayload, cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (ContainsReparsePointBoundary(root, target))
            {
                throw new UnauthorizedAccessException("workspace_reparse_boundary");
            }

            var temporary = target + ".ru-restore-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllBytesAsync(temporary, restoredBytes, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
                catch (IOException)
                {
                    // The durable journal is authoritative; an orphaned temp
                    // file is harmless and is cleaned by the next retry/startup.
                }
            }

            // Re-validate after move to detect symlink swap (TOCTOU)
            var postMoveSha = File.Exists(target)
                ? await ComputeFileSha256Async(target, cancellationToken).ConfigureAwait(false)
                : Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant();
            if (!string.Equals(postMoveSha, restoredSha, StringComparison.Ordinal))
            {
                return new AgentServiceResult(409, new AgentErrorResponse("workspace_restore_verification_failed"));
            }

            _lastRestoreTime[workspaceIdRaw] = now;

            await _workspaceCoordinatorStore.AppendChangeAsync(
                workspaceId,
                runId: null,
                restoreChangeId,
                envelope.Change.NormalizedPath,
                currentSha,
                restoredSha,
                encrypted,
                "restored",
                _time.UtcNow,
                cancellationToken).ConfigureAwait(false);
            await _workspaceCoordinatorStore.MarkChangeRestoredAsync(workspaceId, changeId, cancellationToken).ConfigureAwait(false);
            return new AgentServiceResult(200, new WorkspaceChangeRestoreResponse(changeId, "restored", restoredSha, restoreChangeId));
        }
        catch (HarnessStoreException exception) when (exception.Code is "workspace_root_decrypt_failed" or "workspace_identity_changed" or "workspace_root_key_unavailable")
        {
            return new AgentServiceResult(409, new AgentErrorResponse("workspace_unavailable"));
        }
        catch (IOException)
        {
            return new AgentServiceResult(409, new AgentErrorResponse("workspace_restore_io_failed"));
        }
        catch (UnauthorizedAccessException)
        {
            return new AgentServiceResult(409, new AgentErrorResponse("workspace_restore_io_failed"));
        }
    }

    private static async Task<string> ComputeFileSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 128 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Untrusted runs expose the pending trust challenge so clients can approve the
    /// workspace they actually created; trusted or terminal runs carry no challenge.
    /// </summary>
    private async Task<(string? TrustRequestId, string? DisplayIdentityHash)> ResolveTrustChallengeAsync(
        AgentRun run,
        CancellationToken cancellationToken)
    {
        if (run.State != HarnessRunState.WaitingWorkspaceTrust)
        {
            return (null, null);
        }

        var registration = await _workspaceStore.GetAsync(run.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (registration is null || registration.TrustRequest.AppliedDecision is not null)
        {
            return (null, null);
        }

        return (registration.TrustRequest.RequestId.Value, registration.Identity.DisplayIdentityHash);
    }

    public async Task<AgentServiceResult> TrustAsync(string runIdRaw, TrustAgentRunRequest req, CancellationToken cancellationToken)
    {
        if (!IsRunId(runIdRaw))
        {
            return BoundedError(400, "invalid_run_id", "run_id invalid.");
        }

        if (req is null || string.IsNullOrWhiteSpace(req.TrustRequestId) || string.IsNullOrWhiteSpace(req.DisplayIdentityHash) || string.IsNullOrWhiteSpace(req.Decision))
        {
            return BoundedError(400, "invalid_trust_request", "trust request invalid.");
        }

        if (!IsTrustRequestId(req.TrustRequestId))
        {
            return BoundedError(400, "invalid_trust_request_id", "trust_request_id invalid.");
        }

        if (req.DisplayIdentityHash.Length != 64 || !IsLowerHex(req.DisplayIdentityHash))
        {
            return BoundedError(400, "invalid_display_hash", "display_identity_hash invalid.");
        }

        var decisionRaw = req.Decision.Trim().ToLowerInvariant();
        WorkspaceTrustDecision decision;
        if (decisionRaw == "approve")
        {
            decision = WorkspaceTrustDecision.Approve;
        }
        else if (decisionRaw == "deny")
        {
            decision = WorkspaceTrustDecision.Deny;
        }
        else
        {
            return BoundedError(400, "invalid_decision", "decision must be approve or deny.");
        }

        var runId = new RunId(runIdRaw);
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return BoundedError(404, "run_not_found", "Run not found.");
        }

        var storedWs = await _workspaceStore.GetAsync(run.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (storedWs is null)
        {
            return BoundedError(404, "workspace_not_found", "Workspace not found.");
        }

        var currentIdentity = storedWs.Identity;
        var trustRequestId = new TrustRequestId(req.TrustRequestId);
        var response = new WorkspaceTrustResponse(trustRequestId, currentIdentity, req.DisplayIdentityHash, decision);
        var now = _time.UtcNow;

        WorkspaceTrustStoreResult storeResult;
        try
        {
            storeResult = await _trustStore.ApplyAsync(response, currentIdentity, now, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessStoreException ex)
        {
            return BoundedError(409, ex.Code ?? "trust_stale", ex.Message);
        }

        if (!storeResult.Accepted)
        {
            return BoundedError(409, "trust_stale", storeResult.ErrorCode ?? "trust_stale");
        }

        if (storeResult.Idempotent)
        {
            var currentRun = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
            var status = currentRun is null ? "unknown" : MapRunState(currentRun.Status);
            return new AgentServiceResult(200, new AgentRunTrustResponse(runIdRaw, req.TrustRequestId, status));
        }

        if (decision == WorkspaceTrustDecision.Approve)
        {
            var currentRun = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
            if (currentRun is null)
            {
                return BoundedError(404, "run_not_found", "Run not found.");
            }

            if (currentRun.State != HarnessRunState.WaitingWorkspaceTrust)
            {
                return new AgentServiceResult(200, new AgentRunTrustResponse(runIdRaw, req.TrustRequestId, MapRunState(currentRun.Status)));
            }

            PayloadId? promptPayloadId = null;
            await using var conn = await OpenHarnessConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var payloadCmd = conn.CreateCommand();
            payloadCmd.CommandText = "SELECT payload_id FROM agent_payloads WHERE run_id = $run_id AND kind = $kind LIMIT 1";
            payloadCmd.Parameters.AddWithValue("$run_id", runIdRaw);
            payloadCmd.Parameters.AddWithValue("$kind", (int)HarnessPayloadKind.UserPrompt);
            var result = await payloadCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is string pid)
            {
                promptPayloadId = new PayloadId(pid);
            }

            if (promptPayloadId is null)
            {
                return BoundedError(500, "missing_prompt_payload", "Prompt payload missing.");
            }

            var transition = AgentRunStateMachine.TryTransition(currentRun.Status, HarnessRunTransition.ApproveWorkspaceTrust);
            if (!transition.Accepted)
            {
                return BoundedError(409, transition.ErrorCode ?? "invalid_transition", "Transition rejected.");
            }

            var nextRun = AdvanceRun(currentRun, transition.Next, currentRun.BudgetWindow, currentRun.ModelStepsInWindow, currentRun.ToolCallsInWindow, 1, now);

            var outboxIntent = new ConversationOutboxIntent(
                _ids.NewEventId(),
                currentRun.SessionId,
                promptPayloadId.Value,
                ConversationOutboxRole.User,
                HistoryWaitKind.UserBeforeFirstStep);

            var evt = new HarnessEventIntent(_ids.NewEventId(), HarnessEventKind.RunStateChanged, safeCode: "waiting_history");
            var commit = new HarnessCommit(
                currentRun.RunId,
                currentRun.Version,
                nextRun,
                new[] { evt },
                Array.Empty<HarnessPayloadIntent>(),
                new[] { outboxIntent });

            var commitResult = await _store.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
            if (commitResult.Outcome != HarnessCommitOutcome.Committed)
            {
                return BoundedError(409, "commit_conflict", "Commit conflict.");
            }

            return new AgentServiceResult(200, new AgentRunTrustResponse(runIdRaw, req.TrustRequestId, "waiting_history"));
        }
        else
        {
            var currentRun = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
            if (currentRun is null)
            {
                return BoundedError(404, "run_not_found", "Run not found.");
            }

            if (currentRun.State.IsTerminal())
            {
                return new AgentServiceResult(200, new AgentRunTrustResponse(runIdRaw, req.TrustRequestId, MapRunState(currentRun.Status)));
            }

            var transition = AgentRunStateMachine.TryTransition(currentRun.Status, HarnessRunTransition.DenyWorkspaceTrust);
            if (!transition.Accepted)
            {
                transition = AgentRunStateMachine.TryTransition(currentRun.Status, HarnessRunTransition.Cancel);
                if (!transition.Accepted)
                {
                    return BoundedError(409, transition.ErrorCode ?? "invalid_transition", "Transition rejected.");
                }
            }

            var nextRun = AdvanceRun(currentRun, transition.Next, currentRun.BudgetWindow, currentRun.ModelStepsInWindow, currentRun.ToolCallsInWindow, 1, now, terminalErrorCode: transition.Next.State == HarnessRunState.Cancelled ? "trust_denied" : null);

            var evt = new HarnessEventIntent(_ids.NewEventId(), HarnessEventKind.RunStateChanged, safeCode: "cancelled");

            var commit = new HarnessCommit(
                currentRun.RunId,
                currentRun.Version,
                nextRun,
                new[] { evt },
                Array.Empty<HarnessPayloadIntent>(),
                Array.Empty<ConversationOutboxIntent>());

            var commitResult = await _store.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
            if (commitResult.Outcome != HarnessCommitOutcome.Committed)
            {
                return BoundedError(409, "commit_conflict", "Commit conflict.");
            }

            return new AgentServiceResult(200, new AgentRunTrustResponse(runIdRaw, req.TrustRequestId, "cancelled"));
        }
    }

    private static bool IsWithinPath(string candidate, string root)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            || Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar
                && normalizedCandidate.StartsWith(
                    normalizedRoot + Path.AltDirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProjectRootWorkspace(string canonicalWorkspacePath)
    {
        var candidates = new List<string> { Directory.GetCurrentDirectory() };
        var envRoot = Environment.GetEnvironmentVariable("RU_PROJECT_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot))
        {
            candidates.Add(envRoot);
        }

        foreach (var candidate in candidates)
        {
            try
            {
                if (IsWithinPath(canonicalWorkspacePath, candidate))
                {
                    return true;
                }
            }
            catch
            {
                // Ignore invalid paths
            }
        }

        return false;
    }

    private static string ResolveWorkspaceFile(string root, string relativePath) =>
        AgentV1Shared.ResolveWorkspaceFile(root, relativePath);

    private static bool ContainsReparsePointBoundary(string root, string target) =>
        AgentV1Shared.ContainsReparsePointBoundary(root, target);

    private static bool TryReadRestoreContent(ReadOnlySpan<byte> plaintext, out byte[] content) =>
        AgentV1Shared.TryReadRestoreContent(plaintext, out content);
}
