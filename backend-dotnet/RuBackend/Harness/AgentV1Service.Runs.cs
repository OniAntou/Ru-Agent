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
    // Run creation
    public async Task<AgentServiceResult> CreateRunAsync(CreateAgentRunRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BoundedError(400, "invalid_request", "Request body required.");
        }

        var hasImages = request.ImageAttachments is { Count: > 0 };
        if (hasImages)
        {
            if (request.ImageAttachments!.Count > 4)
            {
                return BoundedError(400, "image_limit_exceeded", "Too many images. Maximum is 4.");
            }

            const int MaxImageDataUrlChars = 5_600_000;
            const long MaxSingleImageBytes = 4L * 1024 * 1024;
            const long MaxTotalImagesBytes = 20L * 1024 * 1024;

            long totalEstimatedBytes = 0;
            foreach (var url in request.ImageAttachments)
            {
                if (string.IsNullOrWhiteSpace(url))
                {
                    return BoundedError(400, "invalid_image_mime", "Image attachment cannot be empty.");
                }

                if (url.Length > MaxImageDataUrlChars)
                {
                    return BoundedError(413, "image_too_large", "Image attachment exceeds character limit.");
                }

                if (!url.StartsWith("data:image/", StringComparison.Ordinal))
                {
                    return BoundedError(400, "invalid_image_mime", "Image data URL must start with 'data:image/'.");
                }

                var semi = url.IndexOf(";base64,", StringComparison.Ordinal);
                if (semi < 0)
                {
                    return BoundedError(400, "invalid_image_base64", "Image data URL must contain ';base64,'.");
                }

                var mime = url.Substring(5, semi - 5);
                if (mime is not ("image/png" or "image/jpeg" or "image/webp"))
                {
                    return BoundedError(400, "invalid_image_mime", "Unsupported image mime type. Only image/png, image/jpeg, and image/webp are allowed.");
                }

                var b64 = url.Substring(semi + 8);
                if (b64.Length == 0)
                {
                    return BoundedError(400, "invalid_image_base64", "Base64 data cannot be empty.");
                }

                // Validate base64 charset without allocating
                for (var i = 0; i < b64.Length; i++)
                {
                    var ch = b64[i];
                    if (!((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '+' || ch == '/' || ch == '='))
                    {
                        return BoundedError(400, "invalid_image_base64", "Base64 data contains invalid characters or whitespace.");
                    }
                }

                // Estimate decoded size from base64 length: ceil(b64_len * 3 / 4)
                // Account for padding: each '=' reduces decoded size by 1
                var padding = 0;
                if (b64.EndsWith("==", StringComparison.Ordinal)) padding = 2;
                else if (b64.EndsWith("=", StringComparison.Ordinal)) padding = 1;
                var estimatedDecoded = (b64.Length * 3L) / 4 - padding;

                if (estimatedDecoded > MaxSingleImageBytes)
                {
                    return BoundedError(413, "image_too_large", "Image decoded size exceeds 4MB.");
                }

                totalEstimatedBytes += estimatedDecoded;
                if (totalEstimatedBytes > MaxTotalImagesBytes)
                {
                    return BoundedError(413, "image_too_large", "Total images decoded size exceeds 20MB.");
                }
            }

            if (_settingsStore is not null)
            {
                var settings = await _settingsStore.ReadAsync(cancellationToken).ConfigureAwait(false);
                string model;
                try
                {
                    var selection = ProviderSelection.From(settings);
                    model = selection.Model;
                }
                catch
                {
                    model = settings.Model;
                }

                if (!_isVisionModel(model))
                {
                    return BoundedError(400, "vision_unsupported", $"Model '{model}' does not support vision.");
                }
            }
        }

        var sessionIdRaw = request.SessionId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sessionIdRaw) || sessionIdRaw.Length > 64)
        {
            return BoundedError(400, "invalid_session_id", "session_id invalid.");
        }

        if (!IsSessionId(sessionIdRaw))
        {
            return BoundedError(400, "invalid_session_id", "session_id invalid.");
        }

        var workspacePath = request.WorkspacePath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(workspacePath) || workspacePath.Length > 4096)
        {
            return BoundedError(400, "invalid_workspace", "workspace_path invalid.");
        }

        var prompt = request.Prompt ?? string.Empty;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            if (hasImages)
            {
                prompt = string.Empty;
            }
            else
            {
                return BoundedError(400, "invalid_prompt", "prompt invalid.");
            }
        }
        else if (prompt.Length > 16000)
        {
            return BoundedError(400, "invalid_prompt", "prompt invalid.");
        }

        var presetRaw = request.Preset?.Trim().ToLowerInvariant() ?? string.Empty;
        // Compatibility metadata: empty means standard; legacy full_access values
        // are accepted but mapped to the same standard behavior — Full Access no
        // longer changes policy. Trusted flows through when the session selection
        // says so (see below); the request string alone never elevates a run.
        AgentPermissionPreset preset = AgentPermissionPreset.Standard;
        if (!string.IsNullOrEmpty(presetRaw) && presetRaw != "standard" && presetRaw != "full_access" && presetRaw != "full" && presetRaw != "trusted")
        {
            return BoundedError(400, "invalid_preset", "preset must be standard, full_access, or trusted.");
        }

        var idempotencyKey = request.IdempotencyKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 256)
        {
            return BoundedError(400, "invalid_idempotency_key", "idempotency_key invalid.");
        }

        long? clientVersion = request.PresetSelectionVersion;
        if (clientVersion is not null && clientVersion <= 0)
        {
            return BoundedError(400, "invalid_preset_version", "preset_selection_version must be positive.");
        }

        var sessionDoc = await _sessions.GetAsync(sessionIdRaw, cancellationToken).ConfigureAwait(false);
        if (sessionDoc is null)
        {
            return BoundedError(404, "session_not_found", "Session not found.");
        }

        WorkspaceIdentity workspaceIdentity;
        try
        {
            workspaceIdentity = await _resolver.ResolveAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger?.LogWarning(error, "workspace path could not be resolved as a workspace root");
            return BoundedError(400, "invalid_workspace", "workspace_path invalid.");
        }

        WorkspaceTrustRequest? pendingTrustRequest = null;
        string canonicalWorkspacePath;
        try
        {
            var trustReq = await _trustService.EnsureTrustRequestAsync(workspacePath, cancellationToken).ConfigureAwait(false);
            canonicalWorkspacePath = WindowsWorkspaceIdentityResolver.GetCanonicalRoot(workspacePath);
            var storedWs = await _workspaceStore.GetAsync(workspaceIdentity.WorkspaceId, cancellationToken).ConfigureAwait(false);
            if (storedWs is not null)
            {
                workspaceIdentity = storedWs.Identity;
                pendingTrustRequest = storedWs.TrustRequest;
            }
            else
            {
                pendingTrustRequest = trustReq;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger?.LogWarning(error, "workspace trust registration failed");
            return BoundedError(400, "invalid_workspace", "workspace registration failed.");
        }

        // Keep the exact final path that was trusted. Retaining a caller-supplied
        // symlink/junction here would allow it to be retargeted before the runtime
        // loop constructs its gateway.
        try
        {
            var finalIdentity = await _resolver.ResolveAsync(canonicalWorkspacePath, cancellationToken).ConfigureAwait(false);
            if (finalIdentity != workspaceIdentity)
            {
                return BoundedError(409, "workspace_changed", "Workspace identity changed during registration.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return BoundedError(409, "workspace_changed", "Workspace identity changed during registration.");
        }

        var now = _time.UtcNow;
        long effectiveVersion;
        var existingSelection = await _permissionStore.GetAsync(new SessionId(sessionIdRaw), cancellationToken).ConfigureAwait(false);
        // Single preset: keep the permission row as compatibility metadata.
        // Any mismatch resolves to the stored version; no preset upgrade path.
        if (existingSelection is null)
        {
            if (clientVersion is null || clientVersion == 1)
            {
                effectiveVersion = 1;
            }
            else
            {
                return BoundedError(409, "preset_mismatch", "preset_selection_version mismatch.");
            }
        }
        else
        {
            if (clientVersion is null)
            {
                var idemShaCheck = ComputeSha256(idempotencyKey);
                await FindIdempotentNormalizedHashAsync(sessionIdRaw, idemShaCheck, cancellationToken).ConfigureAwait(false);
                effectiveVersion = existingSelection.Version;
            }
            else
            {
                if (existingSelection.Version != clientVersion.Value)
                {
                    return BoundedError(409, "preset_mismatch", "preset/version mismatch.");
                }

                effectiveVersion = existingSelection.Version;
            }
        }

        // The stored session selection is authoritative: an interactively
        // confirmed Trusted session produces trusted snapshots. The request
        // string alone never elevates: without a matching stored selection the
        // run stays standard.
        if (existingSelection is not null && existingSelection.Preset == AgentPermissionPreset.Trusted)
        {
            preset = AgentPermissionPreset.Trusted;
        }

        var isTrusted = await _workspaceStore.IsTrustedAsync(workspaceIdentity, preset, cancellationToken).ConfigureAwait(false);
        bool isProjectRoot = IsProjectRootWorkspace(canonicalWorkspacePath);
        if (!isTrusted && isProjectRoot)
        {
            isTrusted = true;
            if (pendingTrustRequest is not null)
            {
                var trustResponse = new WorkspaceTrustResponse(
                    pendingTrustRequest.RequestId,
                    workspaceIdentity,
                    workspaceIdentity.DisplayIdentityHash,
                    WorkspaceTrustDecision.Approve);
                try
                {
                    await _trustStore.ApplyAsync(trustResponse, workspaceIdentity, now, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore concurrency or persistence store errors if already applied
                }
            }
        }

        // Materialize the durable coordinator before inserting the run. This
        // makes shared-mode policy visible to the event store for this very
        // first run and keeps the legacy exclusive fallback intact when the
        // coordinator is not registered (for compatibility test hosts).
        if (_workspaceCoordinatorStore is not null)
        {
            try
            {
                await _workspaceCoordinatorStore
                    .GetOrCreateAsync(workspaceIdentity.WorkspaceId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HarnessStoreException ex)
            {
                return BoundedError(409, ex.Code ?? "workspace_coordinator_unavailable", "Workspace coordination is unavailable.");
            }
        }

        now = _time.UtcNow;
        var runId = _ids.NewRunId();
        PolicySnapshot policySnapshot;
        try
        {
            policySnapshot = _policySnapshotService is null
                ? PolicySnapshotCanonicalizer.Create(
                    1,
                    preset,
                    workspaceIdentity,
                    Array.Empty<PolicyRule>(),
                    now)
                : await _policySnapshotService.LoadAsync(
                    workspaceIdentity,
                    preset,
                    runId,
                    cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessStoreException ex)
        {
            return BoundedError(409, ex.Code ?? "policy_snapshot_invalid", "Policy snapshot could not be loaded.");
        }

        var initialState = isTrusted
            ? new HarnessRunStatus(HarnessRunState.WaitingHistory, HistoryWaitKind.UserBeforeFirstStep)
            : new HarnessRunStatus(HarnessRunState.WaitingWorkspaceTrust);

        var sessionHarnessId = new SessionId(sessionIdRaw);
        var snapshotSha = policySnapshot.Sha256;

        var promptBytes = Encoding.UTF8.GetBytes(prompt);
        var promptPayloadId = _ids.NewPayloadId();
        var promptPayload = new HarnessPayloadIntent(
            promptPayloadId,
            HarnessPayloadKind.UserPrompt,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            Math.Max(promptBytes.Length, 1),
            promptBytes);

        var promptEventId = _ids.NewEventId();
        var events = new List<HarnessEventIntent>
        {
            new(promptEventId, HarnessEventKind.RunStateChanged, payloadId: promptPayloadId, safeCode: isTrusted ? "waiting_history" : "waiting_workspace_trust"),
        };
        var run = new AgentRun(
            runId,
            sessionHarnessId,
            workspaceIdentity.WorkspaceId,
            initialState,
            version: 0,
            lastSequence: events.Count,
            budgetWindow: 1,
            modelStepsInWindow: 0,
            toolCallsInWindow: 0,
            preset,
            policySnapshotVersion: policySnapshot.Version,
            policySnapshotSha256: snapshotSha,
            createdAt: now,
            updatedAt: now);

        var payloads = new List<HarnessPayloadIntent> { promptPayload };
        if (hasImages)
        {
            foreach (var url in request.ImageAttachments!)
            {
                var imageBytes = Encoding.UTF8.GetBytes(url);
                var imagePayloadId = _ids.NewPayloadId();
                payloads.Add(new HarnessPayloadIntent(
                    imagePayloadId,
                    HarnessPayloadKind.UserImage,
                    HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                    HarnessPayloadEncoding.Utf8Text,
                    imageBytes.Length,
                    imageBytes));
            }
        }

        IReadOnlyList<ConversationOutboxIntent> outbox = Array.Empty<ConversationOutboxIntent>();

        if (isTrusted)
        {
            var outboxIntent = new ConversationOutboxIntent(
                _ids.NewEventId(),
                run.SessionId,
                promptPayloadId,
                ConversationOutboxRole.User,
                HistoryWaitKind.UserBeforeFirstStep);
            outbox = new[] { outboxIntent };
        }
        else
        {
            outbox = Array.Empty<ConversationOutboxIntent>();
        }

        var idempotencySha = ComputeSha256(idempotencyKey);
        var imagesPart = hasImages
            ? "|" + string.Join(";", request.ImageAttachments!.Select(ComputeSha256))
            : string.Empty;
        var normalizedRequestSha = ComputeSha256($"{sessionIdRaw}|{workspaceIdentity.WorkspaceId.Value}|{prompt}|{preset}|{effectiveVersion}{imagesPart}");
        var creationIdentity = new RunCreationIdentity(idempotencySha, normalizedRequestSha, effectiveVersion);
        var newRun = new NewAgentRun(run, events, payloads, outbox, creationIdentity);

        CreateRunResult storeResult;
        try
        {
            storeResult = await _store.TryCreateRunAsync(newRun, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessStoreException ex)
        {
            return BoundedError(400, ex.Code ?? "harness_error", ex.Message);
        }

        if (storeResult.Outcome == CreateRunOutcome.Created)
        {
            RegisterRuntimeRoot(storeResult.Run, canonicalWorkspacePath, policySnapshot);
            var resp = isTrusted
                ? MapRun(storeResult.Run)
                : MapRun(storeResult.Run, pendingTrustRequest?.RequestId.Value, workspaceIdentity.DisplayIdentityHash);
            return new AgentServiceResult(201, resp);
        }

        if (storeResult.Outcome == CreateRunOutcome.AlreadyExists)
        {
            if (string.Equals(storeResult.Run.PolicySnapshotSha256, policySnapshot.Sha256, StringComparison.Ordinal)
                && storeResult.Run.PolicySnapshotVersion == policySnapshot.Version)
            {
                RegisterRuntimeRoot(storeResult.Run, canonicalWorkspacePath, policySnapshot);
            }
            var resp = isTrusted
                ? MapRun(storeResult.Run)
                : MapRun(storeResult.Run, pendingTrustRequest?.RequestId.Value, workspaceIdentity.DisplayIdentityHash);
            return new AgentServiceResult(200, resp);
        }

        var code = storeResult.ErrorCode ?? "conflict";
        return code switch
        {
            "idempotency_conflict" => BoundedError(409, "idempotency_conflict", "Idempotency key conflict."),
            "workspace_busy" => BusyConflict("workspace_busy", "Workspace busy.", storeResult.Run),
            "session_busy" => BusyConflict("workspace_busy", "Session busy.", storeResult.Run),
            "preset_mismatch" => BoundedError(409, "preset_mismatch", "Preset mismatch."),
            "preset_selection_required" => BoundedError(409, "preset_mismatch", "Preset selection required."),
            "preset_version_conflict" => BoundedError(409, "preset_mismatch", "Preset version conflict."),
            "workspace_not_found" => BoundedError(404, "workspace_not_found", "Workspace not found."),
            _ => BoundedError(409, code, "Create conflict."),
        };
    }

    public async Task<AgentServiceResult> GetRunAsync(string runIdRaw, CancellationToken cancellationToken)
    {
        if (!IsRunId(runIdRaw))
        {
            return BoundedError(400, "invalid_run_id", "run_id invalid.");
        }

        var run = await _store.GetRunAsync(new RunId(runIdRaw), cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return BoundedError(404, "run_not_found", "Run not found.");
        }

        var (trustRequestId, displayHash) = await ResolveTrustChallengeAsync(run, cancellationToken).ConfigureAwait(false);
        return new AgentServiceResult(200, MapRun(run, trustRequestId, displayHash));
    }

    /// <summary>
    /// Untrusted runs expose the pending trust challenge so clients can approve the
    /// workspace they actually created; trusted or terminal runs carry no challenge.
    /// </summary>

    public async Task<AgentServiceResult> ListSessionRunsAsync(string sessionIdRaw, int? limitRaw, string? cursor, CancellationToken cancellationToken)
    {
        if (!IsSessionId(sessionIdRaw))
        {
            return BoundedError(400, "invalid_session_id", "session_id invalid.");
        }

        var sessionDoc = await _sessions.GetAsync(sessionIdRaw, cancellationToken).ConfigureAwait(false);
        if (sessionDoc is null)
        {
            return BoundedError(404, "session_not_found", "Session not found.");
        }

        int limit = limitRaw ?? 50;
        if (limit is < 1 or > 100)
        {
            return BoundedError(400, "invalid_limit", "limit must be 1..100.");
        }

        string? anchorCreatedAt = null;
        string? anchorRunId = null;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            var decoded = TryDecodeCursor(cursor);
            if (decoded is null)
            {
                return BoundedError(400, "invalid_cursor", "cursor invalid.");
            }

            anchorCreatedAt = decoded.Value.CreatedAt;
            anchorRunId = decoded.Value.RunId;

            if (!IsRunId(anchorRunId!) || !IsValidIsoTimestamp(anchorCreatedAt!))
            {
                return BoundedError(400, "invalid_cursor", "cursor invalid.");
            }

            var anchorRun = await _store.GetRunAsync(new RunId(anchorRunId!), cancellationToken).ConfigureAwait(false);
            if (anchorRun is null || anchorRun.SessionId.Value != sessionIdRaw)
            {
                return BoundedError(400, "invalid_cursor", "cursor invalid.");
            }
        }

        var runs = new List<AgentRun>();
        await using var conn = await OpenHarnessConnectionAsync(cancellationToken).ConfigureAwait(false);
        var sql = new StringBuilder();
        sql.Append("SELECT run_id, session_id, workspace_id, state, history_wait_kind, version, last_sequence, budget_window, model_steps_in_window, tool_calls_in_window, preset, policy_snapshot_version, policy_snapshot_sha256, created_at_utc, updated_at_utc, terminal_at_utc, terminal_error_code FROM agent_runs WHERE session_id = $session_id ");
        if (anchorCreatedAt is not null && anchorRunId is not null)
        {
            sql.Append("AND ((created_at_utc < $anchor_created) OR (created_at_utc = $anchor_created AND run_id < $anchor_run_id)) ");
        }

        sql.Append("ORDER BY created_at_utc DESC, run_id DESC LIMIT $limit_plus_one");
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql.ToString();
        cmd.Parameters.AddWithValue("$session_id", sessionIdRaw);
        if (anchorCreatedAt is not null)
        {
            cmd.Parameters.AddWithValue("$anchor_created", anchorCreatedAt);
            cmd.Parameters.AddWithValue("$anchor_run_id", anchorRunId!);
        }

        cmd.Parameters.AddWithValue("$limit_plus_one", limit + 1);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            runs.Add(ReadAgentRun(reader));
        }

        string? nextCursor = null;
        if (runs.Count > limit)
        {
            var last = runs[limit - 1];
            nextCursor = EncodeCursor(last.CreatedAt.ToUniversalTime().ToString("O"), last.RunId.Value);
            runs = runs.Take(limit).ToList();
        }

        var items = runs.Select(run => MapRun(run)).ToList();
        var resp = new SessionRunsResponse(items, nextCursor);
        return new AgentServiceResult(200, resp);
    }

    public async Task<AgentServiceResult> GetPayloadAsync(string runIdRaw, string payloadIdRaw, CancellationToken cancellationToken)
    {
        if (!IsRunId(runIdRaw) || !IsPayloadId(payloadIdRaw))
        {
            return BoundedError(400, "invalid_id", "run or payload id invalid.");
        }

        var runId = new RunId(runIdRaw);
        var payloadId = new PayloadId(payloadIdRaw);
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return BoundedError(404, "run_not_found", "Run not found.");
        }

        await using var conn = await OpenHarnessConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT kind, audience FROM agent_payloads WHERE run_id = $run_id AND payload_id = $payload_id";
        checkCmd.Parameters.AddWithValue("$run_id", runIdRaw);
        checkCmd.Parameters.AddWithValue("$payload_id", payloadIdRaw);
        await using var reader = await checkCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return BoundedError(404, "payload_not_found", "Payload not found.");
        }

        var kind = (HarnessPayloadKind)reader.GetInt32(0);
        var audience = (HarnessPayloadAudience)reader.GetInt32(1);
        if (kind == HarnessPayloadKind.RecoveryOpaque)
        {
            return BoundedError(404, "payload_not_found", "Payload not found.");
        }

        if (!audience.HasFlag(HarnessPayloadAudience.AuthenticatedClient))
        {
            return BoundedError(404, "payload_not_found", "Payload not found.");
        }

        try
        {
            var content = await _payloadStore.ReadForClientAsync(runId, payloadId, cancellationToken).ConfigureAwait(false);
            var payloadJson = content.Encoding == HarnessPayloadEncoding.Opaque
                ? Convert.ToBase64String(content.Plaintext.ToArray())
                : Encoding.UTF8.GetString(content.Plaintext.Span);
            // Never silently truncate a payload: callers may persist the returned
            // value as a prompt or recovery artifact, so truncation would turn a
            // valid document/base64 blob into corrupt data.  Keep the response
            // bounded by rejecting oversized payloads explicitly instead.
            const int maxClientPayloadCharacters = 4 * 1024 * 1024;
            if (payloadJson.Length > maxClientPayloadCharacters)
            {
                return BoundedError(413, "payload_too_large", "Payload exceeds the client response limit.");
            }

            var body = new
            {
                payload_id = payloadIdRaw,
                run_id = runIdRaw,
                kind = kind.ToString().ToLowerInvariant(),
                encoding = content.Encoding.ToString().ToLowerInvariant(),
                content = payloadJson,
            };
            return new AgentServiceResult(200, body);
        }
        catch (HarnessStoreException ex) when (ex.Code is "payload_not_found" or "payload_client_forbidden" or "payload_recovery_forbidden")
        {
            return BoundedError(404, "payload_not_found", "Payload not found.");
        }
        catch (HarnessStoreException ex)
        {
            return BoundedError(500, ex.Code ?? "payload_error", ex.Message);
        }
    }

    /// <summary>
    /// Đọc usage payload (kind ModelUsage) cho SSE enrich. Fail-closed: mọi lỗi
    /// đều trả null, không throw, không log payload bytes.
    /// </summary>
    public async Task<RuAgent.Harness.Core.ModelUsage?> TryReadModelUsageAsync(string runIdRaw, string payloadIdRaw, CancellationToken cancellationToken)
    {
        try
        {
            if (!IsRunId(runIdRaw) || !IsPayloadId(payloadIdRaw)) return null;
            var content = await _payloadStore.ReadForClientAsync(new RunId(runIdRaw), new PayloadId(payloadIdRaw), cancellationToken).ConfigureAwait(false);
            if (content.Encoding == HarnessPayloadEncoding.Opaque) return null;
            if (content.Plaintext.Length > 4096) return null;
            using var doc = JsonDocument.Parse(content.Plaintext);
            return RuAgent.Harness.Core.ModelUsage.Read(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    public async Task<AgentServiceResult> CancelAsync(string runIdRaw, CancellationToken cancellationToken)
    {
        if (!IsRunId(runIdRaw))
        {
            return BoundedError(400, "invalid_run_id", "run_id invalid.");
        }

        var runId = new RunId(runIdRaw);
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return BoundedError(404, "run_not_found", "Run not found.");
        }

        if (run.State.IsTerminal())
        {
            return new AgentServiceResult(200, MapRun(run));
        }

        // Stop live terminal processes before committing the durable cancelled
        // projection. The runtime registry points at the same per-run coordinator
        // that owns the gateway/Job Object; merely changing SQLite state leaves a
        // child process running.
        if (_runtimeRoots is not null)
        {
            await _runtimeRoots.CancelRunningToolsAsync(runId, cancellationToken).ConfigureAwait(false);
        }

        var transition = AgentRunStateMachine.TryTransition(run.Status, HarnessRunTransition.Cancel);
        if (!transition.Accepted)
        {
            return BoundedError(409, transition.ErrorCode ?? "invalid_transition", "Cancel rejected.");
        }

        var now = _time.UtcNow;
        var nextRun = AdvanceRun(run, transition.Next, run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow, 1, now, terminalErrorCode: "cancelled");

        var evt = new HarnessEventIntent(_ids.NewEventId(), HarnessEventKind.RunStateChanged, safeCode: "cancelled");
        var commit = new HarnessCommit(runId, run.Version, nextRun, new[] { evt }, Array.Empty<HarnessPayloadIntent>(), Array.Empty<ConversationOutboxIntent>());
        var result = await _store.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
        if (result.Outcome != HarnessCommitOutcome.Committed)
        {
            return BoundedError(409, "commit_conflict", "Commit conflict.");
        }

        return new AgentServiceResult(200, MapRun(result.Run!));
    }

    /// <summary>
    /// Applies the typed user reconciliation decision. The snapshot hash the client
    /// presents must equal this host's freshly computed workspace/Git hash; anything
    /// else fails closed with reconcile_stale and leaves the run untouched.
    /// </summary>

    public async Task<AgentServiceResult> ContinueAsync(string runIdRaw, CancellationToken cancellationToken)
    {
        if (!IsRunId(runIdRaw))
        {
            return BoundedError(400, "invalid_run_id", "run_id invalid.");
        }

        var runId = new RunId(runIdRaw);
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return BoundedError(404, "run_not_found", "Run not found.");
        }

        if (run.State == HarnessRunState.ReconciliationRequired)
        {
            // A run parked on an ambiguous tool outcome has exactly one way out:
            // a reconciliation decision. The desktop client has no reconcile
            // endpoint of its own, and Continue is the only affordance it offers
            // here, so Continue *is* that decision — resolve it as a continue with
            // inspection rather than bouncing the user with invalid_run_transition.
            if (_snapshots is null || _inspectionGate is null)
            {
                return BoundedError(409, "reconciliation_unavailable", "Reconciliation services are not available.");
            }

            string? intentCode = null;
            await foreach (var envelope in _store.ReadEventsAsync(runId, 0, cancellationToken).ConfigureAwait(false))
            {
                if (envelope.Event.Kind == "reconciliation.required"
                    && envelope.Event.SafeCode is { } code)
                {
                    intentCode = code;
                }
            }

            return await ApplyReconciliationAsync(
                    run,
                    UnknownContinueCommand,
                    intentCode,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var transition = AgentRunStateMachine.TryTransition(run.Status, HarnessRunTransition.Continue);
        if (!transition.Accepted)
        {
            return BoundedError(409, transition.ErrorCode ?? "invalid_transition", "Continue rejected.");
        }

        // An explicit user Continue overrides the inspection gate: the user has
        // seen the ambiguous outcome and chosen to proceed. The override is
        // audited beside the continue request.
        var gateCleared = false;
        if (_inspectionGate is { } gate && gate.IsMutationDenied(runId))
        {
            gate.RecordInspection(runId);
            gateCleared = true;
        }

        var now = _time.UtcNow;
        var (nextRun, commitEvents) = PlanContinueCommit(run, transition.Next, gateCleared, _ids, now);

        var commit = new HarnessCommit(runId, run.Version, nextRun, commitEvents, Array.Empty<HarnessPayloadIntent>(), Array.Empty<ConversationOutboxIntent>());
        var result = await _store.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
        if (result.Outcome != HarnessCommitOutcome.Committed)
        {
            return BoundedError(409, "commit_conflict", "Commit conflict.");
        }

        return new AgentServiceResult(200, MapRun(result.Run!));
    }

    internal static (AgentRun NextRun, HarnessEventIntent[] Events) PlanContinueCommit(
        AgentRun run,
        HarnessRunStatus nextStatus,
        bool clearGate,
        IIdGenerator ids,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(ids);
        var events = new List<HarnessEventIntent>
        {
            new(ids.NewEventId(), HarnessEventKind.ContinueRequested, safeCode: "continued"),
        };
        if (clearGate)
        {
            events.Add(new(ids.NewEventId(), HarnessEventKind.ReconciliationResolved, safeCode: "gate_cleared_by_user"));
        }

        var next = AdvanceRun(run, nextStatus, run.BudgetWindow + 1, 0, 0, events.Count, now);
        return (next, events.ToArray());
    }

    public async Task<AgentServiceResult> CompactAsync(
        string runIdRaw,
        CompactAgentRunRequest? request,
        CancellationToken cancellationToken)
    {
        if (!IsRunId(runIdRaw))
        {
            return BoundedError(400, "invalid_run_id", "run_id invalid.");
        }

        var strategyRaw = request?.Strategy?.Trim().ToLowerInvariant();
        var strategy = strategyRaw switch
        {
            null or "" or "normal" => ContextCompactionStrategy.Normal,
            "aggressive" => ContextCompactionStrategy.Aggressive,
            _ => (ContextCompactionStrategy?)null,
        };
        if (strategy is null)
        {
            return BoundedError(
                400,
                "invalid_compaction_strategy",
                "strategy must be normal or aggressive.");
        }

        var runId = new RunId(runIdRaw);
        if (_runtimeRoots is null
            || !_runtimeRoots.TryGetCoordinator(runId, out var coordinator)
            || coordinator is null)
        {
            return BoundedError(
                409,
                "runtime_not_ready",
                "The run runtime is not ready for compaction.");
        }

        ContextCompactionResult result;
        try
        {
            result = await coordinator.CompactAsync(
                    runId,
                    strategy.Value,
                    ContextCompactionReason.Manual,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return BoundedError(404, "run_not_found", "Run not found.");
        }
        catch (InvalidOperationException exception) when (exception.Message == "run_not_found")
        {
            return BoundedError(404, "run_not_found", "Run not found.");
        }

        return result.Outcome switch
        {
            ContextCompactionOutcome.Committed => new AgentServiceResult(
                200,
                new CompactAgentRunResponse(
                    runIdRaw,
                    MapRunState(result.Run!.Status),
                    strategyRaw is "aggressive" ? "aggressive" : "normal",
                    result.CompactionId,
                    result.BeforeTokens,
                    result.AfterTokens,
                    result.Run.Version)),
            ContextCompactionOutcome.NoOp => new AgentServiceResult(
                200,
                new CompactAgentRunResponse(
                    runIdRaw,
                    "no_op",
                    strategyRaw is "aggressive" ? "aggressive" : "normal",
                    null,
                    null,
                    null,
                    result.Run?.Version ?? 0)),
            ContextCompactionOutcome.NotAvailable => BoundedError(
                409,
                "compact_not_available",
                "Context compaction is not available."),
            ContextCompactionOutcome.CommitConflict => BoundedError(
                409,
                "commit_conflict",
                "Commit conflict."),
            ContextCompactionOutcome.RunNotFound => BoundedError(
                404,
                "run_not_found",
                "Run not found."),
            _ => BoundedError(500, "compact_failed", "Compaction failed."),
        };
    }

    private async Task<SqliteConnection> OpenHarnessConnectionAsync(CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA foreign_keys = ON; PRAGMA busy_timeout = {_options.BusyTimeoutMilliseconds};";
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }

    private static AgentRun ReadAgentRun(SqliteDataReader reader, int startIndex = 0) =>
        RuAgent.Harness.Persistence.HarnessSqliteMapper.ReadRun(reader, startIndex);

    private static AgentRun AdvanceRun(AgentRun current, HarnessRunStatus nextStatus, int nextBudgetWindow, int nextModelSteps, int nextToolCalls, long appendedCount, DateTimeOffset updatedAt, string? terminalErrorCode = null)
    {
        DateTimeOffset? terminalAt = nextStatus.State.IsTerminal() ? updatedAt : null;
        return new AgentRun(
            current.RunId,
            current.SessionId,
            current.WorkspaceId,
            nextStatus,
            current.Version + 1,
            current.LastSequence + appendedCount,
            nextBudgetWindow,
            nextModelSteps,
            nextToolCalls,
            current.Preset,
            current.PolicySnapshotVersion,
            current.PolicySnapshotSha256,
            current.CreatedAt,
            updatedAt,
            terminalAt,
            terminalErrorCode);
    }

    private async Task<string?> FindIdempotentNormalizedHashAsync(string sessionId, string idempotencySha, CancellationToken ct)
    {
        await using var conn = await OpenHarnessConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT normalized_request_sha256 FROM agent_runs WHERE session_id = $sid AND idempotency_key_sha256 = $key LIMIT 1";
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$key", idempotencySha);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result as string;
    }

    // Helpers (slice 1: pure logic lives in AgentV1Shared; wrappers keep API stable)

    /// <summary>
    /// Registers the run's workspace root with the runtime loop so the headless driver
    /// can execute model/tool steps for it on this host process. Untrusted runs are
    /// registered too but only start driving once trust approval moves them to running.
    /// </summary>
    private void RegisterRuntimeRoot(AgentRun run, string workspacePath, PolicySnapshot policySnapshot) =>
        _runtimeRoots?.Register(run.RunId, workspacePath, policySnapshot);

    private static GetAgentRunResponse MapRun(
        AgentRun run,
        string? trustRequestId = null,
        string? displayIdentityHash = null) => new(
        RunId: run.RunId.Value,
        SessionId: run.SessionId.Value,
        WorkspaceId: run.WorkspaceId.Value,
        Status: MapRunState(run.Status),
        HistoryWaitKind: MapHistoryWaitKind(run.HistoryWaitKind),
        // Single preset: legacy stored FullAccess rows still project as full_access
        // for wire compatibility; new runs are always standard.
        Preset: run.Preset switch
        {
            AgentPermissionPreset.Standard => "standard",
            AgentPermissionPreset.FullAccess => "full_access",
            AgentPermissionPreset.Trusted => "trusted",
            _ => "standard",
        },
        PolicySnapshotVersion: run.PolicySnapshotVersion,
        PolicySnapshotSha256: run.PolicySnapshotSha256,
        Version: run.Version,
        LastSequence: run.LastSequence,
        CreatedAt: run.CreatedAt.ToUniversalTime().ToString("O"),
        UpdatedAt: run.UpdatedAt.ToUniversalTime().ToString("O"),
        TerminalAt: run.TerminalAt?.ToUniversalTime().ToString("O"),
        TerminalErrorCode: run.TerminalErrorCode,
        TrustRequestId: trustRequestId,
        DisplayIdentityHash: displayIdentityHash);

    private static string EncodeCursor(string createdAt, string runId) =>
        AgentV1Shared.EncodeCursor(createdAt, runId);

    private static (string CreatedAt, string RunId)? TryDecodeCursor(string cursor) =>
        AgentV1Shared.TryDecodeCursor(cursor);
}
