using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence.Workspace;
using RuBackend.Harness.Workspace;

namespace RuBackend.Harness;

public sealed class HarnessRunService
{
    private readonly IHarnessEventStore _store;
    private readonly AgentRunCoordinator _coordinator;
    private readonly HarnessFeatureOptions _options;
    private readonly WindowsWorkspaceIdentityResolver _resolver;
    private readonly WorkspaceTrustService _trustService;
    private readonly IIdGenerator _ids;
    private readonly ITimeProvider _time;

    public HarnessRunService(
        IHarnessEventStore store,
        AgentRunCoordinator coordinator,
        HarnessFeatureOptions options,
        WindowsWorkspaceIdentityResolver resolver,
        WorkspaceTrustService trustService,
        IIdGenerator ids,
        ITimeProvider time)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _trustService = trustService ?? throw new ArgumentNullException(nameof(trustService));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task<CreateRunResult> CreateRunAsync(
        string sessionId,
        string workspacePath,
        string prompt,
        AgentPermissionPreset preset,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (prompt.Length > 16_000)
        {
            throw new ArgumentException("Prompt too long.", nameof(prompt));
        }

        var workspaceIdentity = await _resolver.ResolveAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var isTrusted = await _trustService.IsTrustedAsync(workspaceIdentity, preset, cancellationToken).ConfigureAwait(false);
        if (!isTrusted && IsProjectRootWorkspace(workspacePath))
        {
            isTrusted = true;
        }

        var now = _time.UtcNow;
        var version = _options.Version;
        // Snapshot version/hash: phase-1 uses version 1, empty rules, SHA computed via PolicySnapshotCanonicalizer.
        var snapshot = RuAgent.Harness.Core.Policy.PolicySnapshotCanonicalizer.Create(1, preset, workspaceIdentity, Array.Empty<RuAgent.Harness.Core.Policy.PolicyRule>(), now);

        var runId = _ids.NewRunId();
        var initialState = isTrusted ? new HarnessRunStatus(HarnessRunState.WaitingHistory, HistoryWaitKind.UserBeforeFirstStep) : new HarnessRunStatus(HarnessRunState.WaitingWorkspaceTrust);

        var sessionHex = ComputeSha256(sessionId)[..32];
        var run = new AgentRun(
            runId,
            new SessionId(sessionHex),
            workspaceIdentity.WorkspaceId,
            initialState,
            version: 0,
            lastSequence: 0,
            budgetWindow: 1,
            modelStepsInWindow: 0,
            toolCallsInWindow: 0,
            preset,
            policySnapshotVersion: snapshot.Version,
            policySnapshotSha256: snapshot.Sha256,
            createdAt: now,
            updatedAt: now);

        // Prepare prompt payload first so we can link it to the initial event.
        var promptBytes = Encoding.UTF8.GetBytes(prompt);
        var promptPayloadId = _ids.NewPayloadId();
        var promptPayload = new HarnessPayloadIntent(
            promptPayloadId,
            HarnessPayloadKind.UserPrompt,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            promptBytes.Length == 0 ? 1 : promptBytes.Length,
            promptBytes);

        // Prepare events/payloads for creation
        var events = new List<HarnessEventIntent>
        {
            new(_ids.NewEventId(), HarnessEventKind.RunStateChanged, payloadId: promptPayloadId, safeCode: isTrusted ? "waiting_history" : "waiting_workspace_trust"),
        };

        var payloads = new List<HarnessPayloadIntent> { promptPayload };
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
            events.Add(new HarnessEventIntent(_ids.NewEventId(), HarnessEventKind.RunStateChanged, safeCode: "waiting_history"));
        }

        var creationIdentity = new RunCreationIdentity(
            ComputeSha256(idempotencyKey),
            ComputeSha256($"{sessionId}|{workspacePath}|{prompt}|{preset}"),
            snapshot.Version);

        var newRun = new NewAgentRun(run, events, payloads, outbox, creationIdentity);
        return await _store.TryCreateRunAsync(newRun, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentRunStepResult> RunOneStepAsync(RunId runId, CancellationToken cancellationToken)
    {
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("run_not_found");

        if (run.State == HarnessRunState.WaitingWorkspaceTrust)
        {
            throw new InvalidOperationException("workspace_trust_required");
        }

        if (run.State != HarnessRunState.Running && run.State != HarnessRunState.WaitingHistory)
        {
            throw new InvalidOperationException("run_not_runnable");
        }

        // Coordinator will enforce history wait etc.
        return await _coordinator.RunOneStepAsync(runId, cancellationToken).ConfigureAwait(false);
    }

    public Task<AgentRun?> GetRunAsync(RunId runId, CancellationToken cancellationToken)
        => _store.GetRunAsync(runId, cancellationToken);

    public async Task<WorkspaceTrustStoreResult> DecideWorkspaceTrustAsync(
        string workspacePath,
        TrustRequestId requestId,
        string displayedHash,
        WorkspaceTrustDecision decision,
        CancellationToken cancellationToken)
    {
        var identity = await _resolver.ResolveAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var response = new WorkspaceTrustResponse(requestId, identity, displayedHash, decision);
        var now = _time.UtcNow;
        return await _trustService.DecideAsync(response, identity, now, cancellationToken).ConfigureAwait(false);
    }

    private static string ComputeSha256(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsProjectRootWorkspace(string workspacePath)
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
                var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
                var normalizedWorkspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
                if (string.Equals(normalizedWorkspace, normalizedCandidate, StringComparison.OrdinalIgnoreCase)
                    || normalizedWorkspace.StartsWith(normalizedCandidate + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar && normalizedWorkspace.StartsWith(normalizedCandidate + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
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
}
