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

public sealed record AgentServiceResult(int StatusCode, object? Body);

public sealed class AgentV1Service
{
    private readonly IHarnessEventStore _store;
    private readonly HarnessSqliteOptions _options;
    private readonly WindowsWorkspaceIdentityResolver _resolver;
    private readonly SqliteWorkspaceStore _workspaceStore;
    private readonly WorkspaceTrustRequestStore _trustStore;
    private readonly WorkspaceTrustService _trustService;
    private readonly IIdGenerator _ids;
    private readonly ITimeProvider _time;
    private readonly HarnessPayloadStore _payloadStore;
    private readonly ISessionStore _sessions;
    private readonly AgentSessionPermissionStore _permissionStore;
    private readonly IHarnessKeyStore _keyStore;
    private readonly IAgentRuntimeRootRegistry? _runtimeRoots;
    private readonly HarnessPolicySnapshotService? _policySnapshotService;
    private readonly RuBackend.Harness.Reconciliation.WorkspaceStateSnapshotService? _snapshots;
    private readonly ReconciliationInspectionGate? _inspectionGate;
    private readonly RuBackend.Harness.Reconciliation.IAgentApprovalGrants? _approvalGrants;
    private readonly IAgentBrokerClient? _agentBrokerClient;
    private readonly Broker.BrokerToolGatewayFactory? _brokerGateways;
    private readonly WorkspaceCoordinatorStore? _workspaceCoordinatorStore;
    private readonly WorkspaceJournalProtector? _workspaceJournalProtector;
    private readonly ISettingsStore? _settingsStore;
    private readonly Func<string, bool> _isVisionModel;

    private const int ApprovalToolIdMaxLength = 128;
    private const int ApprovalToolVersionMaxLength = 32;
    private const int ApprovalPathMaxLength = 4096;
    private const int ApprovalExecutableMaxLength = 256;
    private const int ApprovalCwdMaxLength = 4096;
    private const int ApprovalArgumentMaxLength = 4096;
    private const int ApprovalScriptMaxLength = 16_000;
    private const int MaxCursorChars = 512;
    private const int MaxCursorBytes = 384;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool IsVisionModel(string? model) => ModelImage.IsVisionModel(model);

    public AgentV1Service(
        IHarnessEventStore store,
        HarnessSqliteOptions options,
        WindowsWorkspaceIdentityResolver resolver,
        SqliteWorkspaceStore workspaceStore,
        WorkspaceTrustRequestStore trustStore,
        WorkspaceTrustService trustService,
        IIdGenerator ids,
        ITimeProvider time,
        HarnessPayloadStore payloadStore,
        ISessionStore sessions,
        AgentSessionPermissionStore permissionStore,
        IHarnessKeyStore keyStore,
        IAgentRuntimeRootRegistry? runtimeRoots = null,
        RuBackend.Harness.Reconciliation.WorkspaceStateSnapshotService? snapshots = null,
        ReconciliationInspectionGate? inspectionGate = null,
        RuBackend.Harness.Reconciliation.IAgentApprovalGrants? approvalGrants = null,
        HarnessPolicySnapshotService? policySnapshotService = null,
        IAgentBrokerClient? agentBrokerClient = null,
        Broker.BrokerToolGatewayFactory? brokerGateways = null,
        WorkspaceCoordinatorStore? workspaceCoordinatorStore = null,
        WorkspaceJournalProtector? workspaceJournalProtector = null,
        ISettingsStore? settingsStore = null,
        Func<string, bool>? isVisionModel = null)
    {
        _store = store;
        _options = options;
        _resolver = resolver;
        _workspaceStore = workspaceStore;
        _trustStore = trustStore;
        _trustService = trustService;
        _ids = ids;
        _time = time;
        _payloadStore = payloadStore;
        _sessions = sessions;
        _permissionStore = permissionStore;
        _keyStore = keyStore;
        _runtimeRoots = runtimeRoots;
        _policySnapshotService = policySnapshotService;
        _snapshots = snapshots;
        _inspectionGate = inspectionGate;
        _approvalGrants = approvalGrants;
        _agentBrokerClient = agentBrokerClient;
        _brokerGateways = brokerGateways;
        _workspaceCoordinatorStore = workspaceCoordinatorStore;
        _workspaceJournalProtector = workspaceJournalProtector;
        _settingsStore = settingsStore;
        _isVisionModel = isVisionModel ?? ModelImage.IsVisionModel;
    }

    // Compatibility overload for tests that construct with old signature (fallback to ephemeral)
    public AgentV1Service(
        IHarnessEventStore store,
        HarnessSqliteOptions options,
        WindowsWorkspaceIdentityResolver resolver,
        SqliteWorkspaceStore workspaceStore,
        WorkspaceTrustRequestStore trustStore,
        WorkspaceTrustService trustService,
        IIdGenerator ids,
        ITimeProvider time,
        HarnessPayloadStore payloadStore,
        ISessionStore sessions,
        AgentSessionPermissionStore permissionStore)
        : this(store, options, resolver, workspaceStore, trustStore, trustService, ids, time, payloadStore, sessions, permissionStore, new EphemeralKeyStoreFallback())
    {
    }

    private sealed class EphemeralKeyStoreFallback : IHarnessKeyStore
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public ValueTask<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken)
        {
            var copy = new byte[32];
            Buffer.BlockCopy(_key, 0, copy, 0, 32);
            return ValueTask.FromResult(copy);
        }
    }

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

            long totalDecodedBytes = 0;
            foreach (var url in request.ImageAttachments)
            {
                if (string.IsNullOrWhiteSpace(url))
                {
                    return BoundedError(400, "invalid_image_mime", "Image attachment cannot be empty.");
                }

                if (url.Length > 5_600_000)
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

                for (var i = 0; i < b64.Length; i++)
                {
                    var ch = b64[i];
                    if (!((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '+' || ch == '/' || ch == '='))
                    {
                        return BoundedError(400, "invalid_image_base64", "Base64 data contains invalid characters or whitespace.");
                    }
                }

                byte[] decoded;
                try
                {
                    decoded = Convert.FromBase64String(b64);
                }
                catch (FormatException)
                {
                    return BoundedError(400, "invalid_image_base64", "Base64 data is malformed.");
                }

                if (decoded.Length > 4 * 1024 * 1024)
                {
                    return BoundedError(413, "image_too_large", "Image decoded size exceeds 4MB.");
                }

                totalDecodedBytes += decoded.Length;
            }

            if (totalDecodedBytes > 20 * 1024 * 1024)
            {
                return BoundedError(413, "image_too_large", "Total images decoded size exceeds 20MB.");
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
        AgentPermissionPreset preset;
        if (presetRaw == "standard")
        {
            preset = AgentPermissionPreset.Standard;
        }
        else if (presetRaw == "full_access")
        {
            preset = AgentPermissionPreset.FullAccess;
        }
        else
        {
            return BoundedError(400, "invalid_preset", "preset must be standard or full_access.");
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
        catch (Exception)
        {
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
        catch (Exception)
        {
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

        long effectiveVersion;
        var existingSelection = await _permissionStore.GetAsync(new SessionId(sessionIdRaw), cancellationToken).ConfigureAwait(false);
        if (existingSelection is null)
        {
            if (preset == AgentPermissionPreset.FullAccess)
            {
                return BoundedError(409, "preset_mismatch", "Full Access requires preset selection.");
            }

            if (clientVersion is null)
            {
                effectiveVersion = 1;
            }
            else if (clientVersion == 1)
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
                var storedHash = await FindIdempotentNormalizedHashAsync(sessionIdRaw, idemShaCheck, cancellationToken).ConfigureAwait(false);
                if (storedHash is not null)
                {
                    effectiveVersion = existingSelection.Version;
                }
                else if (preset == AgentPermissionPreset.Standard && existingSelection.Preset == AgentPermissionPreset.Standard)
                {
                    effectiveVersion = existingSelection.Version;
                }
                else
                {
                    return BoundedError(409, "preset_mismatch", "preset_selection_version required.");
                }
            }
            else
            {
                if (existingSelection.Preset != preset || existingSelection.Version != clientVersion.Value)
                {
                    return BoundedError(409, "preset_mismatch", "preset/version mismatch.");
                }

                effectiveVersion = existingSelection.Version;
            }
        }

        var isTrusted = await _workspaceStore.IsTrustedAsync(workspaceIdentity, preset, cancellationToken).ConfigureAwait(false);

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

        var now = _time.UtcNow;
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
            "workspace_busy" => BoundedError(409, "workspace_busy", "Workspace busy."),
            "session_busy" => BoundedError(409, "workspace_busy", "Session busy."),
            "preset_mismatch" => BoundedError(409, "preset_mismatch", "Preset mismatch."),
            "preset_selection_required" => BoundedError(409, "preset_mismatch", "Preset selection required."),
            "preset_version_conflict" => BoundedError(409, "preset_mismatch", "Preset version conflict."),
            "workspace_not_found" => BoundedError(404, "workspace_not_found", "Workspace not found."),
            _ => BoundedError(409, code, "Create conflict."),
        };
    }

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

    public AgentServiceResult PhaseNotAvailable() => BoundedError(409, "phase_not_available", "Not available in this phase.");

    // Policy endpoints
    public async Task<AgentServiceResult> ListPoliciesAsync(string? scopeRaw, string? workspaceIdRaw, CancellationToken cancellationToken)
    {
        var scope = scopeRaw?.Trim().ToLowerInvariant();
        if (scope is not null && scope != "global" && scope != "project" && scope != "run" && scope != "once")
        {
            return BoundedError(400, "invalid_scope", "scope must be global, project, run, or once.");
        }

        var store = new SqlitePolicyRuleStore(_options, _keyStore);
        try
        {
            if (string.Equals(scope, "global", StringComparison.Ordinal))
            {
                var globals = await store.ListGlobalAsync(cancellationToken).ConfigureAwait(false);
                var items = globals.Select(MapPolicyRule).ToList();
                return new AgentServiceResult(200, new { items });
            }

            if (string.Equals(scope, "project", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(workspaceIdRaw))
                {
                    return BoundedError(400, "invalid_workspace", "workspace_id required for project scope.");
                }

                var trimmedWorkspaceId = workspaceIdRaw.Trim();
                if (!IsWorkspaceId(trimmedWorkspaceId))
                {
                    return BoundedError(400, "invalid_workspace", "workspace_id invalid.");
                }

                var wsId = new WorkspaceId(trimmedWorkspaceId);
                var list = await store.ListProjectAsync(wsId, cancellationToken).ConfigureAwait(false);
                var items = list.Select(MapPolicyRule).ToList();
                return new AgentServiceResult(200, new { items });
            }

            if (string.Equals(scope, "run", StringComparison.Ordinal) || string.Equals(scope, "once", StringComparison.Ordinal))
            {
                return BoundedError(400, "invalid_scope", "run/once listing not supported via this endpoint.");
            }

            // default: list global
            var g = await store.ListGlobalAsync(cancellationToken).ConfigureAwait(false);
            var defaultItems = g.Select(MapPolicyRule).ToList();
            return new AgentServiceResult(200, new { items = defaultItems });
        }
        catch (HarnessStoreException ex)
        {
            return BoundedError(500, ex.Code ?? "policy_list_failed", ex.Message);
        }
    }

    public async Task<AgentServiceResult> CreatePolicyAsync(JsonElement body, bool isInteractive, CancellationToken cancellationToken)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return BoundedError(400, "invalid_request", "Body must be object.");
        }

        // Older clients wrapped the rule in a `rule` property.  Accept that
        // shape only when it is the sole top-level property, otherwise a
        // caller could smuggle ignored fields alongside the actual rule.
        if (body.TryGetProperty("rule", out var wrappedRule))
        {
            if (wrappedRule.ValueKind != JsonValueKind.Object
                || body.EnumerateObject().Any(property => !string.Equals(property.Name, "rule", StringComparison.Ordinal)))
            {
                return BoundedError(400, "invalid_request", "rule must be the only top-level property.");
            }

            body = wrappedRule.Clone();
        }

        // Check for session_preset kind.  Unknown kinds must not silently fall
        // through to policy-rule parsing because that makes a typo change the
        // meaning of the request.
        if (body.TryGetProperty("kind", out var kindProp))
        {
            if (kindProp.ValueKind != JsonValueKind.String)
            {
                return BoundedError(400, "invalid_kind", "kind must be policy_rule or session_preset.");
            }

            var kind = kindProp.GetString()?.Trim().ToLowerInvariant();
            if (kind == "session_preset")
            {
                return await HandleSessionPresetAsync(body, isInteractive, cancellationToken).ConfigureAwait(false);
            }

            if (kind != "policy_rule")
            {
                return BoundedError(400, "invalid_kind", "kind must be policy_rule or session_preset.");
            }
        }

        // Otherwise treat as policy rule creation (kind may be omitted for
        // compatibility with the original endpoint).
        // Validate scope
        if (!body.TryGetProperty("scope", out var scopeProp) || scopeProp.ValueKind != JsonValueKind.String)
        {
            return BoundedError(400, "invalid_scope", "scope required.");
        }

        var scopeRaw = scopeProp.GetString()!.Trim().ToLowerInvariant();
        if (scopeRaw != "global" && scopeRaw != "project" && scopeRaw != "run" && scopeRaw != "once")
        {
            return BoundedError(400, "invalid_scope", "scope must be global, project, run, or once.");
        }

        if (scopeRaw == "global" && !isInteractive)
        {
            return BoundedError(403, "interactive_required", "Global rule requires interactive confirmation.");
        }

        if (!body.TryGetProperty("decision", out var decProp) || decProp.ValueKind != JsonValueKind.String)
        {
            return BoundedError(400, "invalid_decision", "decision required.");
        }

        var decRaw = decProp.GetString()!.Trim().ToLowerInvariant();
        if (decRaw != "allow" && decRaw != "ask" && decRaw != "deny")
        {
            return BoundedError(400, "invalid_decision", "decision must be allow, ask, or deny.");
        }

        if (!body.TryGetProperty("matcher", out var matcherProp) || matcherProp.ValueKind != JsonValueKind.Object)
        {
            return BoundedError(400, "invalid_matcher", "matcher required.");
        }

        var allowedMatcherFields = new HashSet<string>(StringComparer.Ordinal) { "tool_id", "tool_version", "canonical_path_pattern", "resolved_executable", "argv_pattern", "cwd_pattern", "operation_class", "network_intent" };
        foreach (var prop in matcherProp.EnumerateObject())
        {
            if (!allowedMatcherFields.Contains(prop.Name))
            {
                return BoundedError(400, "unknown_field", "Unknown matcher field.");
            }
        }

        var allowedTop = new HashSet<string>(StringComparer.Ordinal) { "kind", "scope", "decision", "matcher", "workspace_id", "run_id", "creation_order", "expires_at" };
        foreach (var prop in body.EnumerateObject())
        {
            if (!allowedTop.Contains(prop.Name))
            {
                return BoundedError(400, "unknown_field", "Unknown policy field.");
            }
        }

        string? toolId = null;
        if (!TryReadPolicyString(matcherProp, "tool_id", 128, out toolId))
        {
            return BoundedError(400, "invalid_matcher", "tool_id must be a non-empty string or null.");
        }

        string? toolVersion = null;
        if (!TryReadPolicyString(matcherProp, "tool_version", 32, out toolVersion))
        {
            return BoundedError(400, "invalid_matcher", "tool_version must be a non-empty string or null.");
        }

        string? pathPattern = null;
        if (!TryReadPolicyString(matcherProp, "canonical_path_pattern", 4096, out pathPattern))
        {
            return BoundedError(400, "invalid_matcher", "canonical_path_pattern must be a non-empty string or null.");
        }

        string? resolvedExe = null;
        if (!TryReadPolicyString(matcherProp, "resolved_executable", 256, out resolvedExe))
        {
            return BoundedError(400, "invalid_matcher", "resolved_executable must be a non-empty string or null.");
        }

        IReadOnlyList<string>? argvPattern = null;
        if (matcherProp.TryGetProperty("argv_pattern", out var argvProp))
        {
            if (argvProp.ValueKind == JsonValueKind.Array)
            {
                if (argvProp.GetArrayLength() > 64)
                {
                    return BoundedError(400, "invalid_matcher", "argv_pattern has too many entries.");
                }

                var list = new List<string>();
                foreach (var el in argvProp.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.String)
                    {
                        return BoundedError(400, "invalid_matcher", "argv_pattern must be string array.");
                    }

                    var value = el.GetString();
                    if (value is null || value.Length == 0 || value.Length > 4096 || string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
                    {
                        return BoundedError(400, "invalid_matcher", "argv_pattern contains an invalid entry.");
                    }

                    list.Add(value);
                }

                argvPattern = list;
            }
            else if (argvProp.ValueKind != JsonValueKind.Null)
            {
                return BoundedError(400, "invalid_matcher", "argv_pattern must be array or null.");
            }
        }

        string? cwdPattern = null;
        if (!TryReadPolicyString(matcherProp, "cwd_pattern", 4096, out cwdPattern))
        {
            return BoundedError(400, "invalid_matcher", "cwd_pattern must be a non-empty string or null.");
        }

        string? opClass = null;
        if (!TryReadPolicyString(matcherProp, "operation_class", 64, out opClass))
        {
            return BoundedError(400, "invalid_matcher", "operation_class must be a non-empty string or null.");
        }

        string? netIntent = null;
        if (!TryReadPolicyString(matcherProp, "network_intent", 64, out netIntent))
        {
            return BoundedError(400, "invalid_matcher", "network_intent must be a non-empty string or null.");
        }

        string? workspaceIdRaw = null;
        if (!TryReadPolicyString(body, "workspace_id", 64, out workspaceIdRaw))
        {
            return BoundedError(400, "invalid_workspace", "workspace_id must be a string or null.");
        }

        string? runIdRaw = null;
        if (!TryReadPolicyString(body, "run_id", 64, out runIdRaw))
        {
            return BoundedError(400, "invalid_run_id", "run_id must be a string or null.");
        }

        long creationOrder = 0;
        if (body.TryGetProperty("creation_order", out var creationProp))
        {
            if (creationProp.ValueKind != JsonValueKind.Number
                || !creationProp.TryGetInt64(out creationOrder)
                || creationOrder < 0)
            {
                return BoundedError(400, "invalid_creation_order", "creation_order must be a non-negative integer.");
            }
        }

        DateTimeOffset? expiresAt = null;
        if (body.TryGetProperty("expires_at", out var expiresProp))
        {
            if (expiresProp.ValueKind == JsonValueKind.Null)
            {
                expiresAt = null;
            }
            else if (expiresProp.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(
                    expiresProp.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsedExpiry)
                || parsedExpiry == default)
            {
                return BoundedError(400, "invalid_expiry", "expires_at must be an ISO-8601 timestamp or null.");
            }
            else
            {
                expiresAt = parsedExpiry.ToUniversalTime();
            }
        }

        PolicyRuleScope scopeEnum = scopeRaw switch
        {
            "global" => PolicyRuleScope.Global,
            "project" => PolicyRuleScope.Project,
            "run" => PolicyRuleScope.Run,
            "once" => PolicyRuleScope.Once,
            _ => throw new InvalidOperationException()
        };

        PolicyDecision decisionEnum = decRaw switch
        {
            "allow" => PolicyDecision.Allow,
            "ask" => PolicyDecision.Ask,
            "deny" => PolicyDecision.Deny,
            _ => throw new InvalidOperationException()
        };

        WorkspaceId? wsId = null;
        RunId? runId = null;
        if (scopeEnum == PolicyRuleScope.Project)
        {
            if (string.IsNullOrWhiteSpace(workspaceIdRaw)) return BoundedError(400, "invalid_workspace", "workspace_id required for project scope.");
            if (!IsWorkspaceId(workspaceIdRaw.Trim())) return BoundedError(400, "invalid_workspace", "workspace_id invalid.");
            if (runIdRaw is not null) return BoundedError(400, "invalid_scope", "project scope must not have run_id.");
            wsId = new WorkspaceId(workspaceIdRaw.Trim());
            var ws = await _workspaceStore.GetAsync(wsId.Value, cancellationToken).ConfigureAwait(false);
            if (ws is null) return BoundedError(404, "workspace_not_found", "Workspace not found.");
        }
        else if (scopeEnum == PolicyRuleScope.Run || scopeEnum == PolicyRuleScope.Once)
        {
            if (string.IsNullOrWhiteSpace(runIdRaw)) return BoundedError(400, "invalid_run_id", "run_id required.");
            if (!IsRunId(runIdRaw.Trim())) return BoundedError(400, "invalid_run_id", "run_id invalid.");
            if (workspaceIdRaw is not null) return BoundedError(400, "invalid_scope", "run/once scope must not have workspace_id.");
            runId = new RunId(runIdRaw.Trim());
            var run = await _store.GetRunAsync(runId.Value, cancellationToken).ConfigureAwait(false);
            if (run is null) return BoundedError(404, "run_not_found", "Run not found.");
        }
        else if (scopeEnum == PolicyRuleScope.Global)
        {
            if (workspaceIdRaw is not null || runIdRaw is not null) return BoundedError(400, "invalid_scope", "global scope must not have workspace/run.");
        }

        PolicyMatcher matcher;
        try
        {
            matcher = new PolicyMatcher(toolId, toolVersion, pathPattern, resolvedExe, argvPattern, cwdPattern, opClass, netIntent);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return BoundedError(400, "invalid_matcher", "Policy matcher is invalid.");
        }

        var ruleId = _ids.NewPolicyRuleId();
        var rule = new PolicyRule(ruleId, scopeEnum, decisionEnum, matcher, creationOrder, expiresAt);
        var store = new SqlitePolicyRuleStore(_options, _keyStore);
        try
        {
            if (scopeEnum == PolicyRuleScope.Global) await store.CreateAsync(rule, null, null, _time.UtcNow, cancellationToken).ConfigureAwait(false);
            else if (scopeEnum == PolicyRuleScope.Project) await store.CreateAsync(rule, wsId, null, _time.UtcNow, cancellationToken).ConfigureAwait(false);
            else await store.CreateAsync(rule, null, runId, _time.UtcNow, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessStoreException ex)
        {
            return BoundedError(400, ex.Code, "Unable to create policy.");
        }

        var respMatcher = new
        {
            tool_id = toolId,
            tool_version = toolVersion,
            canonical_path_pattern = pathPattern,
            resolved_executable = resolvedExe,
            argv_pattern = argvPattern,
            cwd_pattern = cwdPattern,
            operation_class = opClass,
            network_intent = netIntent
        };
        return new AgentServiceResult(201, new { rule_id = ruleId.Value, scope = scopeRaw, decision = decRaw, matcher = respMatcher, creation_order = creationOrder, expires_at = expiresAt?.ToString("O", CultureInfo.InvariantCulture), version = 1 });
    }

    private async Task<AgentServiceResult> HandleSessionPresetAsync(JsonElement body, bool isInteractive, CancellationToken cancellationToken)
    {
        try
        {
            var allowedFields = new HashSet<string>(StringComparer.Ordinal)
            {
                "kind", "session_id", "preset", "expected_version"
            };
            foreach (var property in body.EnumerateObject())
            {
                if (!allowedFields.Contains(property.Name))
                {
                    return BoundedError(400, "unknown_field", "Unknown session preset field.");
                }
            }

            if (!body.TryGetProperty("session_id", out var sessProp) || sessProp.ValueKind != JsonValueKind.String)
            {
                return BoundedError(400, "invalid_session_id", "session_id required.");
            }

            var sessionIdRaw = sessProp.GetString()!.Trim();
            if (!IsSessionId(sessionIdRaw))
            {
                return BoundedError(400, "invalid_session_id", "session_id invalid.");
            }

            if (!body.TryGetProperty("preset", out var presetProp) || presetProp.ValueKind != JsonValueKind.String)
            {
                return BoundedError(400, "invalid_preset", "preset required.");
            }

            var presetRaw = presetProp.GetString()!.Trim().ToLowerInvariant();
            AgentPermissionPreset preset;
            if (presetRaw == "standard") preset = AgentPermissionPreset.Standard;
            else if (presetRaw == "full_access" || presetRaw == "full") preset = AgentPermissionPreset.FullAccess;
            else return BoundedError(400, "invalid_preset", "preset must be standard or full_access.");

            long expectedVersion = 0;
            if (body.TryGetProperty("expected_version", out var verProp))
            {
                if (verProp.ValueKind == JsonValueKind.Number && verProp.TryGetInt64(out var v)) expectedVersion = v;
                else if (verProp.ValueKind == JsonValueKind.String && long.TryParse(verProp.GetString(), out var sv)) expectedVersion = sv;
                else if (verProp.ValueKind != JsonValueKind.Null) return BoundedError(400, "invalid_preset_version", "expected_version invalid.");
            }

            if (expectedVersion < 0)
            {
                return BoundedError(400, "invalid_preset_version", "expected_version must be non-negative.");
            }

            if (preset == AgentPermissionPreset.FullAccess && !isInteractive)
            {
                return BoundedError(403, "interactive_required", "Full Access requires interactive confirmation.");
            }

            var sessionDoc = await _sessions.GetAsync(sessionIdRaw, cancellationToken).ConfigureAwait(false);
            if (sessionDoc is null)
            {
                return BoundedError(404, "session_not_found", "Session not found.");
            }

            var now = _time.UtcNow;
            var result = await _permissionStore.SetInteractivelyAsync(new SessionId(sessionIdRaw), preset, expectedVersion, now, cancellationToken).ConfigureAwait(false);
            if (result.Outcome == SessionPermissionOutcome.VersionConflict)
            {
                return BoundedError(409, "preset_version_conflict", "preset_version_conflict");
            }

            if (result.Selection is null)
            {
                return BoundedError(500, "preset_update_failed", "Failed to update preset.");
            }

            var sel = result.Selection;
            return new AgentServiceResult(200, new { session_id = sel.SessionId.Value, preset = sel.Preset == AgentPermissionPreset.Standard ? "standard" : "full_access", version = sel.Version, updated_at = sel.UpdatedAt.ToString("O") });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HarnessStoreException ex)
        {
            return BoundedError(400, ex.Code, "Unable to update session permissions.");
        }
        catch (Exception)
        {
            return BoundedError(500, "preset_internal_error", "Unable to update session permissions.");
        }
    }

    public async Task<AgentServiceResult> DeletePolicyAsync(string ruleIdRaw, bool isInteractive, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ruleIdRaw) || !IsPolicyRuleId(ruleIdRaw.Trim()))
        {
            return BoundedError(400, "invalid_rule_id", "rule_id invalid.");
        }

        var trimmed = ruleIdRaw.Trim();
        var ruleId = new PolicyRuleId(trimmed);
        var store = new SqlitePolicyRuleStore(_options, _keyStore);

        // Determine if global and check interactive
        bool isGlobal = false;
        try
        {
            var globals = await store.ListGlobalAsync(cancellationToken).ConfigureAwait(false);
            isGlobal = globals.Any(r => r.RuleId.Value == trimmed);
            if (!isGlobal)
            {
                // also need to check if rule exists at all; query raw to find scope
                // For project/run we can query via direct SQL to see existence
                await using var conn = await OpenHarnessConnectionAsync(cancellationToken).ConfigureAwait(false);
                await using var checkCmd = conn.CreateCommand();
                checkCmd.CommandText = "SELECT 1 FROM agent_global_rules WHERE rule_id = $id UNION ALL SELECT 1 FROM agent_project_rules WHERE rule_id = $id UNION ALL SELECT 1 FROM agent_run_rules WHERE rule_id = $id LIMIT 1";
                checkCmd.Parameters.AddWithValue("$id", trimmed);
                var exists = await checkCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (exists is null)
                {
                    return BoundedError(404, "rule_not_found", "Rule not found.");
                }

                // If not global but exists, we already know interactive not required
            }
            else
            {
                if (!isInteractive)
                {
                    return BoundedError(403, "interactive_required", "Global rule deletion requires interactive confirmation.");
                }
            }
        }
        catch (HarnessStoreException ex)
        {
            return BoundedError(500, ex.Code ?? "policy_delete_failed", ex.Message);
        }

        if (isGlobal)
        {
            await store.DeleteGlobalAsync(ruleId, cancellationToken).ConfigureAwait(false);
            return new AgentServiceResult(200, new { rule_id = trimmed, deleted = true });
        }

        // Try project and run deletions (by rule_id, store handles)
        // We already confirmed existence via SQL above, so try both
        await store.DeleteProjectAsync(ruleId, cancellationToken).ConfigureAwait(false);
        await store.DeleteRunAsync(ruleId, cancellationToken).ConfigureAwait(false);
        // Also attempt global delete in case race
        await store.DeleteGlobalAsync(ruleId, cancellationToken).ConfigureAwait(false);
        return new AgentServiceResult(200, new { rule_id = trimmed, deleted = true });
    }

    private static object MapPolicyRule(PolicyRule rule)
    {
        var scope = rule.Scope switch
        {
            PolicyRuleScope.Global => "global",
            PolicyRuleScope.Project => "project",
            PolicyRuleScope.Run => "run",
            PolicyRuleScope.Once => "once",
            _ => "unknown"
        };
        var decision = rule.Decision switch
        {
            PolicyDecision.Allow => "allow",
            PolicyDecision.Ask => "ask",
            PolicyDecision.Deny => "deny",
            _ => "unknown"
        };
        var m = rule.Matcher;
        return new
        {
            rule_id = rule.RuleId.Value,
            scope,
            decision,
            matcher = new
            {
                tool_id = m.ToolId,
                tool_version = m.ToolVersion,
                canonical_path_pattern = m.CanonicalPathPattern,
                resolved_executable = m.ResolvedExecutable,
                argv_pattern = m.ArgvPattern,
                cwd_pattern = m.CwdPattern,
                operation_class = m.OperationClass,
                network_intent = m.NetworkIntent
            },
            creation_order = rule.CreationOrder,
            expires_at = rule.ExpiresAt?.ToString("O"),
            version = 1
        };
    }

    // Host-side one-time approval ledger for HTTP approvals (first-writer wins).
    // Entries are only a retry guard; the durable approval event/run state remains
    // authoritative. Expire old entries so a host that processes many approvals
    // cannot retain an unbounded amount of memory until restart.
    private static readonly ConcurrentDictionary<string, DateTimeOffset> HostApprovalLedger = new(StringComparer.Ordinal);
    private static readonly TimeSpan HostApprovalLedgerTtl = TimeSpan.FromMinutes(30);

    internal static void ClearHostApprovalsForTests() => HostApprovalLedger.Clear();

    private static void PruneHostApprovalLedger(DateTimeOffset now)
    {
        // Pruning on every approval would add avoidable contention to the normal
        // path. Once the bounded retry ledger grows, remove entries older than the
        // same TTL used by pending approval requests.
        if (HostApprovalLedger.Count < 1024)
        {
            return;
        }

        var cutoff = now - HostApprovalLedgerTtl;
        foreach (var entry in HostApprovalLedger)
        {
            if (entry.Value <= cutoff)
            {
                HostApprovalLedger.TryRemove(new KeyValuePair<string, DateTimeOffset>(entry.Key, entry.Value));
            }
        }
    }

    public async Task<AgentServiceResult> HandleApprovalAsync(string runIdRaw, string approvalIdRaw, JsonElement? body, CancellationToken cancellationToken)
    {
        if (!IsRunId(runIdRaw))
        {
            return BoundedError(400, "invalid_run_id", "run_id invalid.");
        }

        if (string.IsNullOrWhiteSpace(approvalIdRaw) || approvalIdRaw.Length > 128)
        {
            return BoundedError(400, "invalid_approval_id", "approval_id invalid.");
        }

        // Parse scope/decision from body
        string? scopeRaw = null;
        string? decisionRaw = null;
        string? commandRaw = null;
        if (body.HasValue && body.Value.ValueKind == JsonValueKind.Object)
        {
            if (body.Value.TryGetProperty("scope", out var scopeProp) && scopeProp.ValueKind == JsonValueKind.String)
            {
                scopeRaw = scopeProp.GetString();
            }

            if (body.Value.TryGetProperty("decision", out var decProp) && decProp.ValueKind == JsonValueKind.String)
            {
                decisionRaw = decProp.GetString();
            }

            if (body.Value.TryGetProperty("command", out var cmdProp) && cmdProp.ValueKind == JsonValueKind.String)
            {
                commandRaw = cmdProp.GetString();
            }
            else if (body.Value.TryGetProperty("approved", out var apprProp) && apprProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                commandRaw = apprProp.GetBoolean() ? "allow_once" : "deny";
            }

            // Also support scope-based command via scope field directly (takes precedence over decision)
            if (scopeRaw is not null && string.IsNullOrWhiteSpace(commandRaw))
            {
                // scope alone implies allow with that scope, unless decision is deny
                if (decisionRaw is not null && decisionRaw.Trim().Equals("deny", StringComparison.OrdinalIgnoreCase))
                {
                    commandRaw = "deny";
                }
                else
                {
                    commandRaw = scopeRaw.Trim().ToLowerInvariant() switch
                    {
                        "once" => "allow_once",
                        "run" => "allow_run",
                        "project" => "allow_project",
                        "global" => "global",
                        _ => null
                    };
                }
            }

            if (string.IsNullOrWhiteSpace(commandRaw) && decisionRaw is not null)
            {
                commandRaw = decisionRaw.Trim().Equals("allow", StringComparison.OrdinalIgnoreCase)
                    ? "allow_once"
                    : decisionRaw;
            }
        }

        if (string.IsNullOrWhiteSpace(commandRaw) && string.IsNullOrWhiteSpace(scopeRaw))
        {
            return BoundedError(400, "invalid_approval_command", "Approval command is required.");
        }

        if (scopeRaw is not null
            && scopeRaw.Trim().ToLowerInvariant() is not ("once" or "run" or "project" or "global"))
        {
            return BoundedError(400, "invalid_approval_command", "Approval scope is invalid.");
        }

        if (!string.IsNullOrWhiteSpace(scopeRaw) && scopeRaw!.Trim().Equals("global", StringComparison.OrdinalIgnoreCase))
        {
            return BoundedError(403, "approval_cannot_create_global", "Approval cannot create global rule.");
        }

        if (commandRaw is not null && commandRaw.Trim().Equals("global", StringComparison.OrdinalIgnoreCase))
        {
            return BoundedError(403, "approval_cannot_create_global", "Approval cannot create global rule.");
        }

        commandRaw = (commandRaw ?? "allow_once").Trim().ToLowerInvariant();
        bool isDeny = commandRaw is "deny" or "reject";
        bool isAllowRun = commandRaw is "allow_run" or "allowrun";
        bool isAllowProject = commandRaw is "allow_project" or "allowproject";
        var isAllowOnce = commandRaw is "allow_once" or "allowonce" or "once";
        if (!isDeny && !isAllowRun && !isAllowProject && !isAllowOnce)
        {
            return BoundedError(400, "invalid_approval_command", "Approval command is invalid.");
        }

        var runId = new RunId(runIdRaw);
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return BoundedError(404, "run_not_found", "Run not found.");
        }

        // A durable approval request is consumable only while the run is still
        // waiting for that approval.  Relying solely on an in-memory ledger would
        // allow a replay after a host restart to resolve an already-used request.
        if (run.State != HarnessRunState.WaitingApproval)
        {
            return BoundedError(409, "approval_stale", "Approval is missing, expired, or already used.");
        }

        // The approval ID must correspond to a persisted approval.requested event for this
        // run while it is actually waiting; fabricated or mismatched IDs are rejected as
        // approval_stale. This check is authoritative and survives process restarts because
        // the event stream is durable. HostApprovalLedger below is only the atomic
        // first-writer-wins guard for HTTP retries.
        ApprovalId? latestPendingId = null;
        HarnessEvent? pendingApprovalEvent = null;
        var proposedEvents = new Dictionary<string, HarnessEvent>(StringComparer.Ordinal);
        await foreach (var envelope in _store.ReadEventsAsync(runId, 0, cancellationToken).ConfigureAwait(false))
        {
            if (envelope.Event.Kind == "tool.proposed"
                && envelope.Event.ToolCallId is { } proposedToolCall)
            {
                proposedEvents[proposedToolCall.Value] = envelope.Event;
            }
            else if (envelope.Event.Kind == "approval.requested"
                && envelope.Event.ApprovalId is { } id)
            {
                latestPendingId = id;
                pendingApprovalEvent = envelope.Event;
            }
        }

        if (latestPendingId is null || !string.Equals(latestPendingId.Value.Value, approvalIdRaw, StringComparison.Ordinal))
        {
            return BoundedError(409, "approval_stale", "Approval is missing, expired, or already used.");
        }

        if (pendingApprovalEvent is null
            || _time.UtcNow - pendingApprovalEvent.OccurredAt > TimeSpan.FromMinutes(30))
        {
            return BoundedError(409, "approval_stale", "Approval is missing, expired, or already used.");
        }

        ToolApprovalBinding? approvalBinding = null;
        if (pendingApprovalEvent?.ToolCallId is { } pendingToolCallId
            && proposedEvents.TryGetValue(pendingToolCallId.Value, out var proposedEvent)
            && proposedEvent.PayloadId is { } proposalPayloadId)
        {
            approvalBinding = await BuildApprovalBindingAsync(
                    run,
                    pendingToolCallId,
                    proposalPayloadId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (approvalBinding is null)
        {
            return BoundedError(409, "approval_stale", "Approval binding is missing or invalid.");
        }

        // One-time consumption: first writer wins. The durable checks above prevent
        // a replay after restart; this ledger only closes the concurrent HTTP race.

        var key = $"{runIdRaw}|{approvalIdRaw}";
        var ledgerNow = _time.UtcNow;
        PruneHostApprovalLedger(ledgerNow);
        if (!HostApprovalLedger.TryAdd(key, ledgerNow))
        {
            return BoundedError(409, "approval_stale", "approval already consumed");
        }

        if (isDeny)
        {
            var denyTransition = AgentRunStateMachine.TryTransition(run.Status, HarnessRunTransition.ResolveApproval);
            if (!denyTransition.Accepted)
            {
                HostApprovalLedger.TryRemove(key, out _);
                return BoundedError(409, denyTransition.ErrorCode ?? "invalid_transition", "Approval resolve rejected.");
            }

            var denyNow = _time.UtcNow;
            var deniedRun = AdvanceRun(run, denyTransition.Next, run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow, 1, denyNow);
            var denyEvt = new HarnessEventIntent(_ids.NewEventId(), HarnessEventKind.RunStateChanged, safeCode: "approval_resolved");
            var denyCommit = new HarnessCommit(runId, run.Version, deniedRun, new[] { denyEvt }, Array.Empty<HarnessPayloadIntent>(), Array.Empty<ConversationOutboxIntent>());
            HarnessCommitResult denyResult;
            try
            {
                denyResult = await _store.CommitAsync(denyCommit, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                HostApprovalLedger.TryRemove(key, out _);
                throw;
            }
            if (denyResult.Outcome != HarnessCommitOutcome.Committed)
            {
                HostApprovalLedger.TryRemove(key, out _);
                return BoundedError(409, "commit_conflict", "Commit conflict.");
            }

            // Best-effort cross-system cleanup: the Broker pending approval expires
            // on its own, so a failed cleanup must not fail the durable denial.
            if (_brokerGateways is { IsConfigured: true })
            {
                try
                {
                    await _brokerGateways.RespondApprovalAsync(
                        runIdRaw,
                        approvalIdRaw,
                        "deny",
                        cancellationToken).ConfigureAwait(false);
                }
                catch (ToolTransportException)
                {
                }
            }

            return new AgentServiceResult(200, new
            {
                approval_id = approvalIdRaw,
                status = "denied",
                error_code = "approval_denied",
                safe_summary = "approval denied",
                run_state = MapRunState(denyResult.Run!.Status),
            });
        }

        bool ruleSaveFailed = false;
        string? ruleId = null;
        string? brokerAuthorizationToken = null;
        var externalDirectoryRequested = string.Equals(
            pendingApprovalEvent?.SafeCode,
            "external_directory_required",
            StringComparison.Ordinal);
        string? externalDirectory = isAllowRun || isAllowProject
            ? TryGetExternalDirectory(run, approvalBinding, externalDirectoryRequested)
            : null;
        // The Broker is the execution authority: without consuming its pending
        // approval first there is no way for the resumed step to execute, so a
        // failed bridge fails closed while the run is still waiting.
        if (_brokerGateways is { IsConfigured: true })
        {
            var brokerCommand = isAllowRun ? "allow_run" : isAllowProject ? "allow_project" : "allow_once";
            bool bridged;
            try
            {
                (bridged, brokerAuthorizationToken) = await _brokerGateways
                    .RespondApprovalAsync(runIdRaw, approvalIdRaw, brokerCommand, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ToolTransportException)
            {
                bridged = false;
            }

            if (!bridged)
            {
                HostApprovalLedger.TryRemove(key, out _);
                return BoundedError(
                    409,
                    "approval_stale",
                    "Approval could not be authorized by the execution authority.");
            }
        }

        if (isAllowRun || isAllowProject)
        {
            try
            {
                var cmd = isAllowRun ? RuAgent.Harness.Core.Approval.ApprovalCommand.AllowRun : RuAgent.Harness.Core.Approval.ApprovalCommand.AllowProject;
                var approvalService = new HarnessApprovalService(_options, _keyStore, _ids, _time);
                var outcome = await approvalService.ProcessAsync(
                    approvalBinding,
                    cmd,
                    run.WorkspaceId,
                    runId,
                    cancellationToken,
                    externalDirectory,
                    externalDirectoryRequested).ConfigureAwait(false);
                ruleSaveFailed = outcome.RuleSaveFailed;
                ruleId = outcome.RuleId;
            }
            catch (OperationCanceledException)
            {
                HostApprovalLedger.TryRemove(key, out _);
                throw;
            }
            catch
            {
                ruleSaveFailed = true;
            }
        }

        var allowTransition = AgentRunStateMachine.TryTransition(run.Status, HarnessRunTransition.ResolveApproval);
        if (!allowTransition.Accepted)
        {
            HostApprovalLedger.TryRemove(key, out _);
            return BoundedError(409, allowTransition.ErrorCode ?? "invalid_transition", "Approval resolve rejected.");
        }

        var allowNow = _time.UtcNow;
        var nextRun = AdvanceRun(run, allowTransition.Next, run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow, 1, allowNow);
        var evt = new HarnessEventIntent(_ids.NewEventId(), HarnessEventKind.RunStateChanged, safeCode: "approval_resolved");
        var commit = new HarnessCommit(runId, run.Version, nextRun, new[] { evt }, Array.Empty<HarnessPayloadIntent>(), Array.Empty<ConversationOutboxIntent>());
        HarnessCommitResult commitResult;
        try
        {
            commitResult = await _store.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            HostApprovalLedger.TryRemove(key, out _);
            throw;
        }
        if (commitResult.Outcome != HarnessCommitOutcome.Committed)
        {
            HostApprovalLedger.TryRemove(key, out _);
            return BoundedError(409, "commit_conflict", "Commit conflict.");
        }

        // Bind the process-local allow-once bridge to the exact tool/version/
        // argument hash that produced this approval. The resumed model step may
        // allocate a new provider call id, but it must re-emit this same intent.
        _approvalGrants?.GrantNext(runId.Value, approvalBinding.IntentHash);
        if (externalDirectory is not null)
        {
            try
            {
                // The durable project rule covers future runs. The process-local
                // directory grant is needed immediately because the active run's
                // policy snapshot is intentionally immutable.
                _approvalGrants?.GrantExternalDirectory(runId.Value, externalDirectory);
            }
            catch (Exception error) when (error is ArgumentException or DirectoryNotFoundException)
            {
                // A directory can disappear between approval binding and grant
                // registration. Keep the run fail-closed; its next proposal will
                // request approval again rather than turning this into a 500.
            }
        }

        var matcherPreview = new
        {
            tool_id = approvalBinding.ToolId,
            tool_version = approvalBinding.ToolVersion,
            canonical_path = approvalBinding.CanonicalPath,
            resolved_executable = approvalBinding.ResolvedExecutable,
            argv_pattern = approvalBinding.Argv,
            cwd_pattern = approvalBinding.Cwd,
            operation_class = approvalBinding.OperationClass,
            network_intent = approvalBinding.NetworkIntent,
            scope = isAllowRun ? "run" : isAllowProject ? "project" : "once"
        };

        var resultBody = new
        {
            approval_id = approvalIdRaw,
            status = "authorized",
            authorization_token = brokerAuthorizationToken
                ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            expires_at = _time.UtcNow.AddSeconds(60).ToString("O"),
            matcher_preview = matcherPreview,
            rule_saved = !ruleSaveFailed && (isAllowRun || isAllowProject),
            reusable_rule_not_saved = ruleSaveFailed,
            rule_id = ruleId,
            safe_summary = "approval authorized",
            run_state = MapRunState(commitResult.Run!.Status),
        };

        return new AgentServiceResult(200, resultBody);
    }

    private IHarnessKeyStore GetKeyStore() => _keyStore;

    private async Task<ToolApprovalBinding?> BuildApprovalBindingAsync(
        AgentRun run,
        ToolCallId toolCallId,
        PayloadId payloadId,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = await _payloadStore
                .ReadForClientAsync(run.RunId, payloadId, cancellationToken)
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(payload.Plaintext);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tool_id", out var toolIdNode)
                || toolIdNode.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("tool_version", out var versionNode)
                || versionNode.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("arguments", out var arguments)
                || arguments.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var toolId = ReadString(root, "tool_id", ApprovalToolIdMaxLength);
            var toolVersion = ReadString(root, "tool_version", ApprovalToolVersionMaxLength);
            if (toolId is null || toolVersion is null)
            {
                return null;
            }

            var canonicalPath = ReadString(arguments, "path", ApprovalPathMaxLength);
            var requestedExecutable = ReadString(arguments, "executable", ApprovalExecutableMaxLength);
            var shellRuntime = ReadString(arguments, "shell", 32) ?? "powershell";
            var argv = ReadStringArray(arguments, "arguments");
            if (string.Equals(toolId, "terminal.shell", StringComparison.Ordinal))
            {
                if (!string.Equals(shellRuntime, "powershell", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(shellRuntime, "pwsh", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var script = ReadString(arguments, "script", ApprovalScriptMaxLength);
                if (script is null)
                {
                    return null;
                }

                requestedExecutable = shellRuntime;
                argv = ["-NoProfile", "-NonInteractive", "-Command", script];
            }
            else if (string.Equals(toolId, "terminal.run", StringComparison.Ordinal)
                && requestedExecutable is null)
            {
                return null;
            }

            if (toolId is "files.read" or "files.write" or "files.edit" or "files.delete"
                && canonicalPath is null)
            {
                return null;
            }

            var cwd = ReadString(arguments, "working_directory", ApprovalCwdMaxLength);
            if (toolId is "terminal.run" or "terminal.shell")
            {
                if (_runtimeRoots is null || !_runtimeRoots.TryGetRoot(run.RunId, out var rootPath)
                    || string.IsNullOrWhiteSpace(rootPath))
                {
                    return null;
                }

                var trustedRoot = WindowsWorkspaceIdentityResolver.GetCanonicalRoot(rootPath);
                var requestedCwd = cwd ?? ".";
                var candidateCwd = Path.IsPathRooted(requestedCwd)
                    ? Path.GetFullPath(requestedCwd)
                    : Path.GetFullPath(Path.Combine(trustedRoot, requestedCwd));
                if (!Directory.Exists(candidateCwd))
                {
                    return null;
                }

                // The gateway stores the final directory identity in its policy
                // context. Resolve the same identity here so a reusable approval
                // cannot be silently bound to a lexical path or a retargeted link.
                cwd = WindowsWorkspaceIdentityResolver.GetCanonicalRoot(candidateCwd);
            }

            var operationClass = toolId switch
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
                "terminal.run" or "terminal.shell" => "terminal",
                _ => "mutation",
            };
            var networkIntent = "none";
            if (arguments.TryGetProperty("network_intent", out var network))
            {
                if (network.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return null;
                }

                networkIntent = network.ValueKind == JsonValueKind.True ? "declared" : "none";
            }
            else if (arguments.TryGetProperty("network", out var legacyNetwork))
            {
                if (legacyNetwork.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return null;
                }

                networkIntent = legacyNetwork.ValueKind == JsonValueKind.True ? "declared" : "none";
            }
            var intentHash = HarnessIntentCanonicalizer.ComputeHash(
                toolId,
                toolVersion,
                arguments);

            return new ToolApprovalBinding(
                run.RunId,
                toolCallId,
                toolId,
                toolVersion,
                canonicalPath,
                requestedExecutable,
                argv,
                cwd,
                operationClass,
                networkIntent,
                run.PolicySnapshotVersion,
                intentHash,
                _time.UtcNow.AddMinutes(30));
        }
        catch (Exception error) when (error is JsonException
            or InvalidOperationException
            or ArgumentException
            or IOException
            or HarnessStoreException
            or CryptographicException)
        {
            return null;
        }
    }

    private string? TryGetExternalDirectory(
        AgentRun run,
        ToolApprovalBinding binding,
        bool externalDirectoryRequested)
    {
        if (!externalDirectoryRequested)
        {
            return null;
        }

        if (_runtimeRoots is null
            || !_runtimeRoots.TryGetRoot(run.RunId, out var rootPath)
            || string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }

        try
        {
            var trustedRoot = WindowsWorkspaceIdentityResolver.GetCanonicalRoot(rootPath);
            string? candidate;
            if (binding.OperationClass == "terminal")
            {
                candidate = binding.Cwd;
            }
            else if (binding.CanonicalPath is not null)
            {
                candidate = Path.IsPathRooted(binding.CanonicalPath)
                    ? Path.GetFullPath(binding.CanonicalPath)
                    : Path.GetFullPath(Path.Combine(trustedRoot, binding.CanonicalPath));
            }
            else
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(candidate))
            {
                return null;
            }

            string directory;
            if (Directory.Exists(candidate))
            {
                directory = candidate;
            }
            else
            {
                var parent = Directory.GetParent(candidate)?.FullName;
                if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
                {
                    return null;
                }

                directory = parent;
            }

            var canonicalDirectory = WindowsWorkspaceIdentityResolver.GetCanonicalRoot(directory);
            if (IsWithinPath(canonicalDirectory, trustedRoot)
                || !ExternalDirectoryPolicy.TryCreatePattern(canonicalDirectory, out _))
            {
                return null;
            }

            return canonicalDirectory;
        }
        catch (Exception error) when (error is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return null;
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

    private static string? ReadString(JsonElement objectNode, string propertyName, int maxLength)
    {
        if (!objectNode.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"{propertyName} must be a string", propertyName);
        }

        var text = value.GetString();
        if (text is null
            || string.IsNullOrWhiteSpace(text)
            || text.Length > maxLength
            || text.Contains('\0'))
        {
            throw new ArgumentException($"{propertyName} is invalid", propertyName);
        }

        return text;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement objectNode, string propertyName)
    {
        if (!objectNode.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<string>();
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("arguments array is invalid", propertyName);
        }

        var values = new List<string>();
        if (value.GetArrayLength() > RuAgent.Protocol.TerminalLimits.MaxArguments)
        {
            throw new ArgumentException("arguments array is too large", propertyName);
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text)
            {
                throw new ArgumentException("arguments array is invalid", propertyName);
            }

            if (text.Length > ApprovalArgumentMaxLength || text.Contains('\0'))
            {
                throw new ArgumentException("arguments array is invalid", propertyName);
            }

            values.Add(text);
        }

        return values;
    }

    private sealed class EphemeralKeyStore : IHarnessKeyStore
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public ValueTask<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken)
        {
            var copy = new byte[32];
            Buffer.BlockCopy(_key, 0, copy, 0, 32);
            return ValueTask.FromResult(copy);
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
    public async Task<AgentServiceResult> ReconcileAsync(
        string runIdRaw,
        string? outcome,
        string? snapshotHash,
        CancellationToken cancellationToken)
    {
        if (!IsRunId(runIdRaw))
        {
            return BoundedError(400, "invalid_run_id", "run_id invalid.");
        }

        if (!ReconciliationCommand.TryParse(outcome, out var command))
        {
            return BoundedError(400, "invalid_outcome", "outcome must be applied, not_applied, or unknown_continue.");
        }

        if (string.IsNullOrWhiteSpace(snapshotHash)
            || snapshotHash.Length != 64
            || !snapshotHash.All(char.IsAsciiHexDigit))
        {
            return BoundedError(400, "invalid_snapshot_hash", "snapshot_hash must be a sha-256 hex digest.");
        }

        if (_snapshots is null || _inspectionGate is null)
        {
            return BoundedError(409, "reconciliation_unavailable", "Reconciliation services are not available.");
        }

        var runId = new RunId(runIdRaw);
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return BoundedError(404, "run_not_found", "Run not found.");
        }

        if (run.State != HarnessRunState.ReconciliationRequired)
        {
            return BoundedError(409, "invalid_run_state", "Run is not awaiting reconciliation.");
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

        var freshHash = await _snapshots.ComputeFreshHashAsync(runId, cancellationToken).ConfigureAwait(false);
        if (freshHash is null || !string.Equals(freshHash, snapshotHash, StringComparison.Ordinal))
        {
            return new AgentServiceResult(409, new
            {
                error_code = "reconcile_stale",
                message = "Workspace snapshot changed; re-read before deciding.",
                snapshot_hash = freshHash,
                intent_code = intentCode,
            });
        }

        var transition = AgentRunStateMachine.TryTransition(
            run.Status,
            HarnessRunTransition.ApplyReconciliation);
        if (!transition.Accepted)
        {
            return BoundedError(409, transition.ErrorCode ?? "invalid_transition", "Reconcile rejected.");
        }

        if (command.Kind == ReconciliationCommandKind.UnknownContinue)
        {
            _inspectionGate.Activate(runId);
        }

        var now = _time.UtcNow;
        var nextRun = AdvanceRun(run, transition.Next, run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow, 1, now);
        var evt = new HarnessEventIntent(_ids.NewEventId(), HarnessEventKind.ReconciliationResolved, safeCode: command.ToWireToken());
        var commit = new HarnessCommit(runId, run.Version, nextRun, new[] { evt }, Array.Empty<HarnessPayloadIntent>(), Array.Empty<ConversationOutboxIntent>());
        var result = await _store.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
        if (result.Outcome != HarnessCommitOutcome.Committed)
        {
            return BoundedError(409, "commit_conflict", "Commit conflict.");
        }

        return new AgentServiceResult(200, new
        {
            run_id = runIdRaw,
            state = MapRunState(result.Run!.Status),
            resolution = command.ToWireToken(),
            intent_code = intentCode,
        });
    }

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

        var transition = AgentRunStateMachine.TryTransition(run.Status, HarnessRunTransition.Continue);
        if (!transition.Accepted)
        {
            return BoundedError(409, transition.ErrorCode ?? "invalid_transition", "Continue rejected.");
        }

        var now = _time.UtcNow;
        var nextRun = AdvanceRun(run, transition.Next, run.BudgetWindow + 1, 0, 0, 1, now);

        var evt = new HarnessEventIntent(_ids.NewEventId(), HarnessEventKind.ContinueRequested, safeCode: "continued");
        var commit = new HarnessCommit(runId, run.Version, nextRun, new[] { evt }, Array.Empty<HarnessPayloadIntent>(), Array.Empty<ConversationOutboxIntent>());
        var result = await _store.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
        if (result.Outcome != HarnessCommitOutcome.Committed)
        {
            return BoundedError(409, "commit_conflict", "Commit conflict.");
        }

        return new AgentServiceResult(200, MapRun(result.Run!));
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

    public async Task<AgentServiceResult> DispatchCommandAsync(
        string runIdRaw,
        DispatchAgentCommandRequest? request,
        CancellationToken cancellationToken)
    {
        if (!IsRunId(runIdRaw))
        {
            return BoundedError(400, "invalid_run_id", "run_id invalid.");
        }

        if (request is null || request.Text is null)
        {
            return BoundedError(400, "invalid_request", "Command text is required.");
        }

        var parsed = AgentActionRouter.Parse(request.Text);
        if (!parsed.IsCommand)
        {
            return BoundedError(400, "command_invalid", "A typed Agent command is required.");
        }

        if (!parsed.Success || parsed.Command is null)
        {
            return BoundedError(
                400,
                parsed.Code ?? "command_invalid",
                parsed.SafeMessage);
        }

        var runId = new RunId(runIdRaw);
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return BoundedError(404, "run_not_found", "Run not found.");
        }

        var command = parsed.Command;
        if (run.State.IsTerminal()
            && command.Kind is AgentCommandKind.Compact or AgentCommandKind.Skill)
        {
            return BoundedError(409, "run_terminal", "Terminal runs cannot execute Agent commands.");
        }

        switch (command.Kind)
        {
            case AgentCommandKind.Help:
                return CommandSucceeded(
                    runIdRaw,
                    "help",
                    new
                    {
                        commands = new[]
                        {
                            new { name = "/compact", arguments = "normal|aggressive", description = "Compact the run context." },
                            new { name = "/help", arguments = "", description = "List typed Agent commands." },
                            new { name = "/skills", arguments = "", description = "List skills granted to this session." },
                            new { name = "/skill", arguments = "<id> <json-object>", description = "Run one granted declarative skill." },
                        },
                    },
                    run.Version);

            case AgentCommandKind.Skills:
                return await DispatchSkillsAsync(run, runIdRaw, cancellationToken).ConfigureAwait(false);

            case AgentCommandKind.Compact:
            {
                var compact = await CompactAsync(
                    runIdRaw,
                    new CompactAgentRunRequest(command.Strategy),
                    cancellationToken).ConfigureAwait(false);
                if (compact.StatusCode != 200)
                {
                    return compact;
                }

                var runVersion = compact.Body is CompactAgentRunResponse response
                    ? response.RunVersion
                    : run.Version;
                return CommandSucceeded(runIdRaw, "compact", compact.Body, runVersion);
            }

            case AgentCommandKind.Skill:
                return await DispatchSkillAsync(run, runIdRaw, command, cancellationToken).ConfigureAwait(false);

            default:
                return BoundedError(400, "command_invalid", "Unsupported Agent command.");
        }
    }

    private async Task<AgentServiceResult> DispatchSkillsAsync(
        AgentRun run,
        string runIdRaw,
        CancellationToken cancellationToken)
    {
        if (_agentBrokerClient is null)
        {
            return BoundedError(409, "broker_not_ready", "The Agent Broker is not ready.");
        }

        IReadOnlyList<AgentSkillDescriptor> skills;
        try
        {
            skills = await _agentBrokerClient
                .ListSkillsAsync(run.SessionId.Value, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AgentBrokerException exception)
        {
            return BrokerCommandError(exception.Code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return BoundedError(502, "broker_unavailable", "The Agent Broker could not be reached.");
        }

        var items = skills
            .OrderBy(skill => skill.SkillId, StringComparer.Ordinal)
            .ThenBy(skill => skill.Version, StringComparer.Ordinal)
            .Select(skill => new
            {
                skill_id = skill.SkillId,
                version = skill.Version,
                required_scopes = skill.RequiredScopes.Order(StringComparer.Ordinal).ToArray(),
                required_tools = skill.RequiredTools.Order(StringComparer.Ordinal).ToArray(),
                trust_class = skill.TrustClass,
            })
            .ToArray();
        return CommandSucceeded(runIdRaw, "skills", new { items }, run.Version);
    }

    private async Task<AgentServiceResult> DispatchSkillAsync(
        AgentRun run,
        string runIdRaw,
        AgentCommand command,
        CancellationToken cancellationToken)
    {
        if (_agentBrokerClient is null)
        {
            return BoundedError(409, "broker_not_ready", "The Agent Broker is not ready.");
        }

        IReadOnlyList<AgentSkillDescriptor> skills;
        try
        {
            skills = await _agentBrokerClient
                .ListSkillsAsync(run.SessionId.Value, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AgentBrokerException exception)
        {
            return BrokerCommandError(exception.Code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return BoundedError(502, "broker_unavailable", "The Agent Broker could not be reached.");
        }

        var descriptor = skills
            .Where(skill => string.Equals(skill.SkillId, command.SkillId, StringComparison.Ordinal))
            .OrderByDescending(skill => ParseSkillVersion(skill.Version))
            .ThenByDescending(skill => skill.Version, StringComparer.Ordinal)
            .FirstOrDefault();
        if (descriptor is null || command.JsonInput is null || command.SkillId is null)
        {
            return BoundedError(403, "skill_not_granted", "The requested skill is not granted.");
        }

        AgentSkillExecutionResult result;
        try
        {
            result = await _agentBrokerClient
                .ExecuteSkillAsync(
                    run.SessionId.Value,
                    command.SkillId,
                    descriptor.Version,
                    command.JsonInput,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AgentBrokerException exception)
        {
            return BrokerCommandError(exception.Code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return BoundedError(502, "broker_unavailable", "The Agent Broker could not be reached.");
        }

        var data = new
        {
            skill_id = result.SkillId,
            version = result.Version,
            safe_summary = result.SafeSummary,
            receipt_id = result.ReceiptId,
            step_count = result.StepCount,
            output = result.Output,
        };
        var response = new DispatchAgentCommandResponse(
            runIdRaw,
            result.Status,
            result.ErrorCode ?? "skill",
            data,
            run.Version);

        return result.Status switch
        {
            ToolResultStatuses.Succeeded => new AgentServiceResult(200, response),
            ToolResultStatuses.ApprovalRequired => new AgentServiceResult(409, response),
            ToolResultStatuses.Denied => new AgentServiceResult(403, response),
            ToolResultStatuses.Cancelled => new AgentServiceResult(409, response),
            ToolResultStatuses.TimedOut => new AgentServiceResult(504, response),
            _ => new AgentServiceResult(502, response),
        };
    }

    private static AgentServiceResult CommandSucceeded(
        string runId,
        string code,
        object? data,
        long runVersion) => new(
        200,
        new DispatchAgentCommandResponse(
            runId,
            ToolResultStatuses.Succeeded,
            code,
            data,
            runVersion));

    private static AgentServiceResult BrokerCommandError(string code) => code switch
    {
        "skill_not_granted" or "scope_denied" or "session_expired" =>
            BoundedError(403, code, "The Agent Broker denied the skill request."),
        "approval_required" => BoundedError(409, code, "The skill requires user approval."),
        "broker_not_ready" or "broker_unavailable" =>
            BoundedError(409, "broker_not_ready", "The Agent Broker is not ready."),
        _ => BoundedError(502, "broker_unavailable", "The Agent Broker rejected the request."),
    };

    private static Version ParseSkillVersion(string value) =>
        Version.TryParse(value, out var version) ? version : new Version(0, 0, 0);

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

    private static AgentRun ReadAgentRun(SqliteDataReader reader, int startIndex = 0)
    {
        var state = (HarnessRunState)reader.GetInt32(startIndex + 3);
        HistoryWaitKind? historyWaitKind = reader.IsDBNull(startIndex + 4) ? null : (HistoryWaitKind)reader.GetInt32(startIndex + 4);
        return new AgentRun(
            new RunId(reader.GetString(startIndex)),
            new SessionId(reader.GetString(startIndex + 1)),
            new WorkspaceId(reader.GetString(startIndex + 2)),
            new HarnessRunStatus(state, historyWaitKind),
            reader.GetInt64(startIndex + 5),
            reader.GetInt64(startIndex + 6),
            reader.GetInt32(startIndex + 7),
            reader.GetInt32(startIndex + 8),
            reader.GetInt32(startIndex + 9),
            (AgentPermissionPreset)reader.GetInt32(startIndex + 10),
            reader.GetInt64(startIndex + 11),
            reader.GetString(startIndex + 12),
            DateTimeOffset.Parse(reader.GetString(startIndex + 13), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(reader.GetString(startIndex + 14), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
            reader.IsDBNull(startIndex + 15) ? null : DateTimeOffset.Parse(reader.GetString(startIndex + 15), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
            reader.IsDBNull(startIndex + 16) ? null : reader.GetString(startIndex + 16));
    }

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

    // Helpers
    private static AgentServiceResult BoundedError(int status, string code, string message) => new(status, new AgentErrorResponse(code, message));

    /// <summary>
    /// Explicit snake_case state names shared by every API surface; enum ToString would
    /// collapse word boundaries ("waiting_history" → "waitinghistory").
    /// </summary>
    public static string MapHistoryWaitKind(HistoryWaitKind? kind) => kind switch
    {
        HistoryWaitKind.UserBeforeFirstStep => "user_before_first_step",
        HistoryWaitKind.FinalBeforeCompletion => "final_before_completion",
        null => "none",
        _ => "unknown",
    };

    public static string MapRunState(HarnessRunStatus status)
    {
        if (status.HistoryWaitKind is not null)
        {
            return status.HistoryWaitKind == HistoryWaitKind.UserBeforeFirstStep
                ? "waiting_history_user"
                : "waiting_history_final";
        }

        return status.State switch
        {
            HarnessRunState.Created => "created",
            HarnessRunState.WaitingWorkspaceTrust => "waiting_workspace_trust",
            HarnessRunState.WaitingHistory => "waiting_history",
            HarnessRunState.Running => "running",
            HarnessRunState.WaitingApproval => "waiting_approval",
            HarnessRunState.WaitingUser => "waiting_user",
            HarnessRunState.ReconciliationRequired => "reconciliation_required",
            HarnessRunState.Completed => "completed",
            HarnessRunState.Failed => "failed",
            HarnessRunState.Cancelled => "cancelled",
            HarnessRunState.Interrupted => "interrupted",
            _ => "unknown",
        };
    }

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
        Preset: run.Preset == AgentPermissionPreset.Standard ? "standard" : "full_access",
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

    private static string ComputeSha256(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputePolicySnapshotSha(AgentPermissionPreset preset, WorkspaceIdentity identity)
    {
        var input = $"{preset}|{identity.WorkspaceId.Value}|empty";
        return ComputeSha256(input);
    }

    private static bool IsSessionId(string value) => value.Length == 32 && IsLowerHex(value);

    private static bool IsWorkspaceId(string value) => value.StartsWith("ws_", StringComparison.Ordinal)
        && value.Length == 35
        && IsLowerHex(value.AsSpan(3));

    private static bool IsRunId(string value) => value.StartsWith("run_", StringComparison.Ordinal) && value.Length == 36 && IsLowerHex(value.AsSpan(4));

    private static bool IsPayloadId(string value) => value.StartsWith("payload_", StringComparison.Ordinal) && value.Length == 40 && IsLowerHex(value.AsSpan(8));

    private static bool IsTrustRequestId(string value) => value.StartsWith("trust_", StringComparison.Ordinal) && value.Length == 38 && IsLowerHex(value.AsSpan(6));

    private static bool IsPolicyRuleId(string value) => value.StartsWith("rule_", StringComparison.Ordinal) && value.Length == 37 && IsLowerHex(value.AsSpan(5));

    private static bool IsLowerHex(string value) => IsLowerHex(value.AsSpan());

    private static bool IsLowerHex(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static string ResolveWorkspaceFile(string root, string relativePath)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var target = Path.GetFullPath(Path.Combine(canonicalRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase)
            && !target.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new HarnessStoreException("workspace_path_outside_root");
        }

        return target;
    }

    private static bool ContainsReparsePointBoundary(string root, string target)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var current = Path.GetFullPath(target);
        while (true)
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }

            if (string.Equals(current, canonicalRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent)
                || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)
                || !parent.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            current = parent;
        }
    }

    private static bool TryReadRestoreContent(ReadOnlySpan<byte> plaintext, out byte[] content)
    {
        content = Array.Empty<byte>();
        try
        {
            using var document = JsonDocument.Parse(plaintext.ToArray());
            if (!document.RootElement.TryGetProperty("content_base64", out var encoded)
                || encoded.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var value = encoded.GetString();
            if (string.IsNullOrWhiteSpace(value) || value.Length > 22_369_624)
            {
                return false;
            }

            content = Convert.FromBase64String(value);
            return content.Length <= 16 * 1024 * 1024;
        }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            return false;
        }
    }

    private static bool IsValidIsoTimestamp(string value)
    {
        return DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out _);
    }

    private static bool TryReadPolicyString(JsonElement obj, string propertyName, int maxLength, out string? value)
    {
        value = null;
        if (!obj.TryGetProperty(propertyName, out var property))
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var raw = property.GetString();
        if (raw is null
            || raw.Length == 0
            || raw.Length > maxLength
            || string.IsNullOrWhiteSpace(raw)
            || raw.Contains('\0'))
        {
            return false;
        }

        value = raw;
        return true;
    }

    private static string EncodeCursor(string createdAt, string runId)
    {
        var raw = $"{createdAt}|{runId}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    private static (string CreatedAt, string RunId)? TryDecodeCursor(string cursor)
    {
        if (cursor.Length > MaxCursorChars)
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(cursor);
            if (bytes.Length == 0 || bytes.Length > MaxCursorBytes)
            {
                return null;
            }

            var raw = StrictUtf8.GetString(bytes);
            var parts = raw.Split('|');
            if (parts.Length != 2
                || parts[0].Length is 0 or > 64
                || parts[1].Length is 0 or > 64
                || parts[0].Contains('\0')
                || parts[1].Contains('\0')
                || !IsValidIsoTimestamp(parts[0])
                || !IsRunId(parts[1]))
            {
                return null;
            }

            return (parts[0], parts[1]);
        }
        catch
        {
            return null;
        }
    }
}


