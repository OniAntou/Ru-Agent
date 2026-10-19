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
            var coordinator = await ReadWorkspaceCoordinatorAsync(workspaceId, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Reads the coordinator policy for a GET without materializing a row: a read
    /// request must not write, so a workspace with no coordinator yet answers with
    /// the same defaults the write path would have created.
    /// </summary>
    private async Task<WorkspaceCoordinatorRecord> ReadWorkspaceCoordinatorAsync(
        WorkspaceId workspaceId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = """
                SELECT mode, max_concurrent_runs, lease_ttl_seconds,
                       merge_text_conflicts, external_writer_detection, commit_sequence,
                       policy_json, version, created_at_utc, updated_at_utc
                FROM workspace_coordinators
                WHERE workspace_id = $workspace_id
                """;
            read.Parameters.AddWithValue("$workspace_id", workspaceId.Value);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new WorkspaceCoordinatorRecord(
                    workspaceId.Value,
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3) == 1,
                    reader.GetInt32(4) == 1,
                    reader.GetInt64(5),
                    reader.GetString(6),
                    reader.GetInt64(7),
                    DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
            }
        }

        await using (var workspace = connection.CreateCommand())
        {
            workspace.CommandText = "SELECT 1 FROM agent_workspaces WHERE workspace_id = $workspace_id";
            workspace.Parameters.AddWithValue("$workspace_id", workspaceId.Value);
            if (await workspace.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            {
                throw new HarnessStoreException("workspace_not_found");
            }
        }

        var now = _time.UtcNow;
        return new WorkspaceCoordinatorRecord(
            workspaceId.Value,
            "shared",
            32,
            30,
            true,
            true,
            0,
            "{}",
            1,
            now,
            now);
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
    private static readonly string MissingFileSha256 = new('0', 64);
    private static readonly TimeSpan MinRestoreInterval = TimeSpan.FromSeconds(1); // Rate limit: max 1 restore/sec per workspace
    private static readonly ConcurrentDictionary<string, DateTimeOffset> _lastRestoreTime = new(StringComparer.Ordinal);

    private static bool TryReserveRestoreSlot(string workspaceId, DateTimeOffset now)
    {
        while (true)
        {
            if (!_lastRestoreTime.TryGetValue(workspaceId, out var current))
            {
                if (_lastRestoreTime.TryAdd(workspaceId, now))
                {
                    return true;
                }

                continue;
            }

            if (now - current < MinRestoreInterval)
            {
                return false;
            }

            if (_lastRestoreTime.TryUpdate(workspaceId, now, current))
            {
                return true;
            }
        }
    }

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

        // Rate limiting per workspace. The slot is reserved atomically before any
        // work starts, so two concurrent restores cannot both pass a stale check.
        var now = _time.UtcNow;
        if (!TryReserveRestoreSlot(workspaceIdRaw, now))
        {
            return new AgentServiceResult(429, new AgentErrorResponse("workspace_restore_rate_limited"));
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

            var targetExists = File.Exists(target);
            var currentSha = targetExists
                ? await ComputeFileSha256Async(target, cancellationToken).ConfigureAwait(false)
                : Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant();
            // The external-change journal records a missing file as 64 zeros, so a
            // client echoing that value must match a file that is still missing.
            var matchesMissingMarker = !targetExists
                && string.Equals(request.ExpectedCurrentSha256, MissingFileSha256, StringComparison.Ordinal);
            if (!matchesMissingMarker
                && !string.Equals(currentSha, request.ExpectedCurrentSha256, StringComparison.Ordinal))
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
                    // Best effort: a locked temp file stays behind as a
                    // `.ru-restore-*` sibling; nothing removes it automatically.
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

            await _workspaceCoordinatorStore.AppendChangeAsync(
                workspaceId,
                runId: null,
                restoreChangeId,
                envelope.Change.NormalizedPath,
                targetExists ? currentSha : MissingFileSha256,
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

    private async Task<(string? TrustRequestId, string? DisplayIdentityHash)> ResolveTrustChallengeAsync(
        AgentRun run,
        CancellationToken cancellationToken)
    {
        if (run.State != HarnessRunState.WaitingWorkspaceTrust)
        {
            return (null, null);
        }

        var registration = await _workspaceStore.GetAsync(run.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (registration is null)
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

        if (await IsInternalSubagentRunAsync(new RunId(runIdRaw), cancellationToken).ConfigureAwait(false))
            return BoundedError(409, "subagent_read_only", "Subagents are controlled by the main agent. Send your request to the parent.");

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

        var trustRequestId = new TrustRequestId(req.TrustRequestId);
        if (storedWs.TrustRequest.RequestId != trustRequestId)
        {
            return BoundedError(409, "trust_stale", "trust_stale");
        }

        if (!string.Equals(
                storedWs.TrustRequest.DisplayedIdentityHash,
                req.DisplayIdentityHash,
                StringComparison.Ordinal))
        {
            return BoundedError(409, "trust_stale", "trust_display_hash_mismatch");
        }

        if (run.State != HarnessRunState.WaitingWorkspaceTrust)
        {
            return new AgentServiceResult(200, new AgentRunTrustResponse(runIdRaw, req.TrustRequestId, MapRunState(run.Status)));
        }

        WorkspaceIdentity currentIdentity;
        try
        {
            currentIdentity = await _trustService.RevalidateIdentityAsync(storedWs, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HarnessStoreException ex)
        {
            _logger?.LogWarning(ex, "workspace trust identity revalidation failed");
            return BoundedError(409, ex.Code ?? "trust_stale", ex.Message);
        }

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

            HarnessCommitResult commitResult;
            try
            {
                commitResult = await _store.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HarnessStoreException ex)
            {
                _logger?.LogWarning(ex, "workspace trust run transition commit failed");
                return BoundedError(409, "commit_conflict", "Commit conflict.");
            }

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

            if (currentRun.State != HarnessRunState.WaitingWorkspaceTrust)
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

            HarnessCommitResult commitResult;
            try
            {
                commitResult = await _store.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HarnessStoreException ex)
            {
                _logger?.LogWarning(ex, "workspace trust run transition commit failed");
                return BoundedError(409, "commit_conflict", "Commit conflict.");
            }

            if (commitResult.Outcome != HarnessCommitOutcome.Committed)
            {
                return BoundedError(409, "commit_conflict", "Commit conflict.");
            }

            return new AgentServiceResult(200, new AgentRunTrustResponse(runIdRaw, req.TrustRequestId, "cancelled"));
        }
    }

    private static string ResolveWorkspaceFile(string root, string relativePath) =>
        AgentV1Shared.ResolveWorkspaceFile(root, relativePath);

    private static bool ContainsReparsePointBoundary(string root, string target) =>
        AgentV1Shared.ContainsReparsePointBoundary(root, target);

    private static bool TryReadRestoreContent(ReadOnlySpan<byte> plaintext, out byte[] content) =>
        AgentV1Shared.TryReadRestoreContent(plaintext, out content);
}
