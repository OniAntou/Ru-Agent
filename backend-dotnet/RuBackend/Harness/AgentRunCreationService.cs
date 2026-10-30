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
using static RuBackend.Harness.AgentV1Shared;

namespace RuBackend.Harness;

public sealed class AgentRunCreationService
{
    private readonly IHarnessEventStore _store;
    private readonly ISessionStore _sessions;
    private readonly AgentSessionPermissionStore _permissionStore;
    private readonly AgentRunWorkspaceAdmissionService _workspaceAdmission;
    private readonly IAgentRuntimeRootRegistry _runtimeRoots;
    private readonly IIdGenerator _ids;
    private readonly ITimeProvider _time;
    private readonly AgentV1SubagentGuard _subagentGuard;
    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<AgentRunCreationService>? _logger;
    private readonly Hooks.RunLifecycleHooks? _lifecycleHooks;

    /// <summary>The selected provider catalog's declaration for image input.</summary>
    public Func<string, CancellationToken, Task<bool?>>? DeclaredVision { get; set; }
    public Func<string, string, CancellationToken, Task<bool?>>? DeclaredProviderVision { get; set; }

    public AgentRunCreationService(IHarnessEventStore store, ISessionStore sessions,
        AgentSessionPermissionStore permissionStore, AgentRunWorkspaceAdmissionService workspaceAdmission,
        IAgentRuntimeRootRegistry runtimeRoots, IIdGenerator ids, ITimeProvider time,
        AgentV1SubagentGuard subagentGuard, ISettingsStore settingsStore,
        ILogger<AgentRunCreationService>? logger = null,
        Hooks.RunLifecycleHooks? lifecycleHooks = null)
    {
        _lifecycleHooks = lifecycleHooks;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _permissionStore = permissionStore ?? throw new ArgumentNullException(nameof(permissionStore));
        _workspaceAdmission = workspaceAdmission ?? throw new ArgumentNullException(nameof(workspaceAdmission));
        _runtimeRoots = runtimeRoots ?? throw new ArgumentNullException(nameof(runtimeRoots));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _subagentGuard = subagentGuard ?? throw new ArgumentNullException(nameof(subagentGuard));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _logger = logger;
    }

    // Run creation
    public async Task<AgentServiceResult> CreateRunAsync(CreateAgentRunRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BoundedError(400, "invalid_request", "Request body required.");
        }

        RunProviderIntent? providerIntent = null;
        if (request.ProviderSelection is { } requestedProvider)
        {
            try { providerIntent = new RunProviderIntent(requestedProvider.Backend, requestedProvider.Model); }
            catch (ArgumentException) { return BoundedError(400, "invalid_provider_intent", "Provider and model must be bounded nonempty values."); }
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

            var settings = ProviderIntentSettings.Apply(await _settingsStore.ReadAsync(cancellationToken).ConfigureAwait(false), providerIntent);
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

            // A provider's own declaration outranks the name check: a gateway model
            // can carry no vendor token at all, and reading its name as non-vision
            // rejected a model that had published modalities ["text","image"].
            bool? declared = providerIntent is not null && DeclaredProviderVision is not null
                ? await DeclaredProviderVision(settings.Backend, model, cancellationToken).ConfigureAwait(false)
                : DeclaredVision is null ? null : await DeclaredVision(model, cancellationToken).ConfigureAwait(false);

            if (!ModelImage.IsVisionModel(model, declared))
            {
                return BoundedError(400, "vision_unsupported", $"Model '{model}' does not support vision.");
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

        if (await _subagentGuard.IsInternalSessionAsync(sessionIdRaw, cancellationToken).ConfigureAwait(false))
        {
            return BoundedError(409, "subagent_session_internal", "Internal sessions cannot create root runs.");
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

        // Session-backed preset ladder: plan / standard / full_access. Empty
        // means standard; the request string validates only and never elevates
        // a run — the stored session selection (see below) decides the preset.
        AgentPermissionPreset preset = AgentPermissionPreset.Standard;
        if (!string.IsNullOrWhiteSpace(request.Preset)
            && !AgentPolicyProfileCatalog.TryNormalizePresetInput(request.Preset, out _))
        {
            return BoundedError(400, "invalid_preset", "preset must be plan, standard, or full_access.");
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

        var workspaceResolution = await _workspaceAdmission.ResolveAndRegisterAsync(workspacePath, cancellationToken)
            .ConfigureAwait(false);
        if (workspaceResolution.Error is not null)
        {
            return workspaceResolution.Error;
        }

        var workspaceRegistration = workspaceResolution.Admission!;

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

        // The stored session selection is authoritative: a run keeps the
        // stored preset. The request preset string alone never elevates
        // or restricts a run.
        if (existingSelection is not null)
        {
            preset = existingSelection.Preset;
        }

        var workspaceAdmission = await _workspaceAdmission
            .AdmitRunAsync(workspaceRegistration, preset, cancellationToken)
            .ConfigureAwait(false);
        if (workspaceAdmission.Error is not null)
        {
            return workspaceAdmission.Error;
        }

        var admittedWorkspace = workspaceAdmission.Admission!;
        now = admittedWorkspace.CreatedAt;
        var runId = admittedWorkspace.RunId;
        var workspaceIdentity = admittedWorkspace.Identity;
        var canonicalWorkspacePath = admittedWorkspace.CanonicalRoot;
        var pendingTrustRequest = admittedWorkspace.PendingTrustRequest;
        var isTrusted = admittedWorkspace.IsTrusted;
        var policySnapshot = admittedWorkspace.PolicySnapshot;

        var skillInvocation = await SkillInvocation.ResolveAsync(canonicalWorkspacePath, prompt,
            static () => PythonAvailability.IsAvailable, cancellationToken).ConfigureAwait(false);
        if (skillInvocation.Code is not null)
            return BoundedError(400, skillInvocation.Code, skillInvocation.Message ?? "Skill invocation failed.");

        // Before anything of the run is written, so a refused prompt leaves no run behind.
        if (_lifecycleHooks is not null
            && await _lifecycleHooks.SubmitPromptAsync(prompt, sessionIdRaw, canonicalWorkspacePath, isTrusted, cancellationToken)
                .ConfigureAwait(false) is { } refusal)
        {
            return BoundedError(422, "prompt_blocked_by_hook", refusal);
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
        HarnessPayloadIntent? skillPayload = null;
        if (skillInvocation.EncodedSnapshot is { } skillSnapshot)
        {
            var bytes = Encoding.UTF8.GetBytes(skillSnapshot);
            skillPayload = new HarnessPayloadIntent(_ids.NewPayloadId(), HarnessPayloadKind.SkillInvocation,
                HarnessPayloadAudience.ModelContext, HarnessPayloadEncoding.Utf8Text, bytes.Length, bytes);
            events.Add(new HarnessEventIntent(_ids.NewEventId(), HarnessEventKind.RunNoteAdded,
                payloadId: skillPayload.PayloadId, safeCode: "skill_invoked"));
        }
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
        if (skillPayload is not null) payloads.Add(skillPayload);
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
        var providerPart = providerIntent is null ? string.Empty
            : "|provider-intent-v1:" + ComputeSha256(JsonSerializer.Serialize(new[] { providerIntent.Backend, providerIntent.Model }));
        var normalizedRequestSha = ComputeSha256($"{sessionIdRaw}|{workspaceIdentity.WorkspaceId.Value}|{prompt}|{preset}|{effectiveVersion}{imagesPart}{providerPart}");
        var creationIdentity = new RunCreationIdentity(idempotencySha, normalizedRequestSha, effectiveVersion);
        var newRun = new NewAgentRun(run, events, payloads, outbox, creationIdentity, providerIntent);

        CreateRunResult storeResult;
        try
        {
            storeResult = await _store.TryCreateRunAsync(newRun, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessStoreException ex)
        {
            _logger?.LogWarning(ex, "create run failed");
            return BoundedError(400, ex.Code ?? "harness_error", "Unable to create run.");
        }

        if (storeResult.Outcome == CreateRunOutcome.Created)
        {
            _runtimeRoots.Register(storeResult.Run.RunId, canonicalWorkspacePath, policySnapshot);
            var resp = isTrusted
                ? MapCreateRun(storeResult.Run)
                : MapCreateRun(storeResult.Run, pendingTrustRequest?.RequestId.Value, workspaceIdentity.DisplayIdentityHash);
            return new AgentServiceResult(201, resp);
        }

        if (storeResult.Outcome == CreateRunOutcome.AlreadyExists)
        {
            if (string.Equals(storeResult.Run.PolicySnapshotSha256, policySnapshot.Sha256, StringComparison.Ordinal)
                && storeResult.Run.PolicySnapshotVersion == policySnapshot.Version)
            {
                _runtimeRoots.Register(storeResult.Run.RunId, canonicalWorkspacePath, policySnapshot);
            }
            var resp = isTrusted
                ? MapCreateRun(storeResult.Run)
                : MapCreateRun(storeResult.Run, pendingTrustRequest?.RequestId.Value, workspaceIdentity.DisplayIdentityHash);
            return new AgentServiceResult(200, resp);
        }

        var code = storeResult.ErrorCode ?? "conflict";
        return code switch
        {
            "idempotency_conflict" => BoundedError(409, "idempotency_conflict", "Idempotency key conflict."),
            "session_busy" => BusyConflict("workspace_busy", "Session busy.", storeResult.Run),
            "preset_mismatch" => BoundedError(409, "preset_mismatch", "Preset mismatch."),
            "preset_selection_required" => BoundedError(409, "preset_mismatch", "Preset selection required."),
            "preset_version_conflict" => BoundedError(409, "preset_mismatch", "Preset version conflict."),
            "session_deleted" => BoundedError(409, "session_deleted", "Session was deleted."),
            "workspace_not_found" => BoundedError(404, "workspace_not_found", "Workspace not found."),
            _ => BoundedError(409, code, "Create conflict."),
        };
    }

    private static CreateAgentRunResponse MapCreateRun(
        AgentRun run,
        string? trustRequestId = null,
        string? displayIdentityHash = null) => new(
        RunId: run.RunId.Value,
        SessionId: run.SessionId.Value,
        WorkspaceId: run.WorkspaceId.Value,
        Status: MapRunState(run.Status),
        Preset: AgentPermissionPresetMapper.ToWireValue(run.Preset),
        CreatedAt: run.CreatedAt.ToUniversalTime().ToString("O"),
        UpdatedAt: run.UpdatedAt.ToUniversalTime().ToString("O"),
        HistoryWaitKind: MapHistoryWaitKind(run.HistoryWaitKind),
        PolicySnapshotVersion: run.PolicySnapshotVersion,
        PolicySnapshotSha256: run.PolicySnapshotSha256,
        Version: run.Version,
        LastSequence: run.LastSequence,
        TerminalAt: run.TerminalAt?.ToUniversalTime().ToString("O"),
        TerminalErrorCode: run.TerminalErrorCode,
        TrustRequestId: trustRequestId,
        DisplayIdentityHash: displayIdentityHash);

}
