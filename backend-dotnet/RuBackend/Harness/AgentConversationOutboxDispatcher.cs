using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence;
using RuBackend.Business.Sessions;
using HarnessEventId = RuAgent.Harness.Core.EventId;

namespace RuBackend.Harness;

public enum AgentOutboxDispatchOutcome
{
    Idle,
    Delivered,
    RetryScheduled,
    RunFailed,
    TransitionRecovered,
}

public readonly record struct AgentOutboxDispatchResult(
    AgentOutboxDispatchOutcome Outcome,
    HarnessEventId? IntentId = null,
    int? AttemptCount = null,
    DateTimeOffset? NextAttemptAt = null);

public interface IAgentHistoryTransitionSink
{
    Task AcknowledgeAsync(
        RunId runId,
        HistoryWaitKind historyWaitKind,
        CancellationToken cancellationToken);

    Task FailAsync(
        RunId runId,
        string errorCode,
        CancellationToken cancellationToken);
}

public sealed class AgentRunHistoryTransitionSink : IAgentHistoryTransitionSink
{
    private readonly IAgentRuntimeCoordinatorFactory _factory;
    private readonly IAgentRuntimeRootRegistry _roots;

    public AgentRunHistoryTransitionSink(
        IAgentRuntimeCoordinatorFactory factory,
        IAgentRuntimeRootRegistry roots)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
    }

    public async Task AcknowledgeAsync(
        RunId runId,
        HistoryWaitKind historyWaitKind,
        CancellationToken cancellationToken)
    {
        if (!_roots.TryGetRoot(runId, out var workspaceRoot) || workspaceRoot is null
            || !_roots.TryGetPolicySnapshot(runId, out var policySnapshot) || policySnapshot is null)
        {
            // The transition stays durable in the outbox transition table and is retried
            // after reconciliation once this host holds the run's workspace root again.
            throw new InvalidOperationException("runtime_root_unknown");
        }

        var coordinator = await _factory.CreateAsync(workspaceRoot, policySnapshot, runId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        _ = await coordinator.AcknowledgeHistoryAsync(
                runId,
                historyWaitKind,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task FailAsync(
        RunId runId,
        string errorCode,
        CancellationToken cancellationToken)
    {
        if (!_roots.TryGetRoot(runId, out var workspaceRoot) || workspaceRoot is null
            || !_roots.TryGetPolicySnapshot(runId, out var policySnapshot) || policySnapshot is null)
        {
            throw new InvalidOperationException("runtime_root_unknown");
        }

        var coordinator = await _factory.CreateAsync(workspaceRoot, policySnapshot, runId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        _ = await coordinator.FailAsync(runId, errorCode, cancellationToken)
            .ConfigureAwait(false);
    }
}

public sealed class AgentConversationOutboxDispatcher
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly HarnessOutboxStore _outbox;
    private readonly HarnessPayloadStore _payloads;
    private readonly ISessionStore _sessions;
    private readonly IHarnessModelContextBootstrapper _contextBootstrapper;
    private readonly IAgentHistoryTransitionSink _transitions;
    private readonly TimeProvider _clock;
    private readonly AgentConversationActivityProjector? _activityProjector;

    public AgentConversationOutboxDispatcher(
        HarnessOutboxStore outbox,
        HarnessPayloadStore payloads,
        ISessionStore sessions,
        IHarnessModelContextBootstrapper contextBootstrapper,
        IAgentHistoryTransitionSink transitions,
        TimeProvider clock,
        AgentConversationActivityProjector? activityProjector = null)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _payloads = payloads ?? throw new ArgumentNullException(nameof(payloads));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _contextBootstrapper = contextBootstrapper
            ?? throw new ArgumentNullException(nameof(contextBootstrapper));
        _transitions = transitions ?? throw new ArgumentNullException(nameof(transitions));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _activityProjector = activityProjector;
    }

    public async Task<AgentOutboxDispatchResult> DispatchOnceAsync(
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().ToUniversalTime();
        var work = await _outbox.ReadNextWorkAsync(now, cancellationToken).ConfigureAwait(false);
        switch (work)
        {
            case OutboxWork.Idle:
                return new AgentOutboxDispatchResult(AgentOutboxDispatchOutcome.Idle);
            case OutboxWork.Transition(var transition):
                await ApplyTransitionAsync(transition, cancellationToken).ConfigureAwait(false);
                return new AgentOutboxDispatchResult(
                    AgentOutboxDispatchOutcome.TransitionRecovered,
                    transition.IntentId);
            case OutboxWork.Delivery(var claim):
                return await DeliverClaimAsync(claim, now, cancellationToken).ConfigureAwait(false);
            default:
                throw new InvalidOperationException("unknown_outbox_work");
        }
    }

    private async Task<AgentOutboxDispatchResult> DeliverClaimAsync(
        OutboxClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {

        HarnessPayloadContent payload;
        try
        {
            payload = await _payloads.ReadForClientAsync(
                    claim.RunId,
                    claim.PayloadId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HarnessStoreException)
        {
            return await FailAsync(
                    claim,
                    "outbox_payload_invalid",
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!TryReadVisibleText(claim.Role, payload, out var content))
        {
            return await FailAsync(
                    claim,
                    "outbox_payload_invalid",
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var append = await _sessions.AppendIfAbsentAsync(
                claim.SessionId.Value,
                new SessionMessage(
                    claim.Role == ConversationOutboxRole.User ? "user" : "ai",
                    content,
                    claim.IntentId.Value,
                    claim.RunId.Value),
                cancellationToken)
            .ConfigureAwait(false);
        switch (append)
        {
            case SessionAppendOutcome.TransientFailure:
                var retry = await _outbox.RetryAsync(
                        claim.IntentId,
                        claim.ClaimToken,
                        "session_store_transient",
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
                EnsureMutation(retry, OutboxMutationOutcome.RetryScheduled);
                return new AgentOutboxDispatchResult(
                    AgentOutboxDispatchOutcome.RetryScheduled,
                    claim.IntentId,
                    claim.AttemptCount,
                    retry.NextAttemptAt);
            case SessionAppendOutcome.SessionNotFound:
                return await FailAsync(
                        claim,
                        "session_not_found",
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
            case SessionAppendOutcome.InvalidMessage:
                return await FailAsync(
                        claim,
                        "session_message_conflict",
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
            case SessionAppendOutcome.Appended:
            case SessionAppendOutcome.AlreadyPresent:
                break;
            default:
                throw new InvalidOperationException("unknown_session_append_outcome");
        }

        if (claim.Role == ConversationOutboxRole.User)
        {
            var context = await _contextBootstrapper.CaptureAsync(
                    claim.RunId,
                    claim.SessionId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (context == ContextBootstrapOutcome.SessionNotFound)
            {
                return await FailAsync(
                        claim,
                        "session_not_found",
                    now,
                    cancellationToken)
                    .ConfigureAwait(false);
            }

            if (_activityProjector is not null)
            {
                await _activityProjector.ProjectUserAsync(
                        claim.IntentId,
                        claim.RunId,
                        claim.SessionId.Value,
                        content,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var acknowledged = await _outbox.AcknowledgeAsync(
                claim.IntentId,
                claim.ClaimToken,
                now,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureMutation(
            acknowledged,
            OutboxMutationOutcome.Acknowledged,
            OutboxMutationOutcome.AlreadyAcknowledged);
        await _transitions.AcknowledgeAsync(
                claim.RunId,
                claim.HistoryWaitKind,
                cancellationToken)
            .ConfigureAwait(false);
        return new AgentOutboxDispatchResult(
            AgentOutboxDispatchOutcome.Delivered,
            claim.IntentId,
            claim.AttemptCount);
    }

    private async Task<AgentOutboxDispatchResult> FailAsync(
        OutboxClaim claim,
        string errorCode,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken)
    {
        var failed = await _outbox.FailAsync(
                claim.IntentId,
                claim.ClaimToken,
                errorCode,
                failedAt,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureMutation(failed, OutboxMutationOutcome.Failed);
        await _transitions.FailAsync(claim.RunId, errorCode, cancellationToken)
            .ConfigureAwait(false);
        return new AgentOutboxDispatchResult(
            AgentOutboxDispatchOutcome.RunFailed,
            claim.IntentId,
            claim.AttemptCount);
    }

    private async Task ApplyTransitionAsync(
        OutboxRunTransition transition,
        CancellationToken cancellationToken)
    {
        if (transition.Kind == OutboxRunTransitionKind.AcknowledgeHistory)
        {
            await _transitions.AcknowledgeAsync(
                    transition.RunId,
                    transition.HistoryWaitKind,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await _transitions.FailAsync(
                transition.RunId,
                transition.ErrorCode ?? "outbox_failure_invalid",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool TryReadVisibleText(
        ConversationOutboxRole role,
        HarnessPayloadContent payload,
        out string content)
    {
        var expectedKind = role == ConversationOutboxRole.User
            ? HarnessPayloadKind.UserPrompt
            : HarnessPayloadKind.AssistantText;
        if (payload.Kind != expectedKind || payload.Encoding != HarnessPayloadEncoding.Utf8Text)
        {
            content = string.Empty;
            return false;
        }

        try
        {
            content = StrictUtf8.GetString(payload.Plaintext.Span);
            return true;
        }
        catch (DecoderFallbackException)
        {
            content = string.Empty;
            return false;
        }
    }

    private static void EnsureMutation(
        OutboxMutationResult result,
        params OutboxMutationOutcome[] expected)
    {
        if (!expected.Contains(result.Outcome))
        {
            throw new HarnessStoreException("outbox_mutation_conflict");
        }
    }
}
