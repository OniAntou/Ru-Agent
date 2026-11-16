using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Protocol.Harness;
using RuBackend.Harness.Subagents;
using static RuBackend.Harness.AgentV1Shared;

namespace RuBackend.Harness;

public sealed class AgentRunLifecycleService
{
    private readonly IHarnessEventStore _store;
    private readonly IAgentRuntimeRootRegistry _runtimeRoots;
    private readonly IIdGenerator _ids;
    private readonly ITimeProvider _time;
    private readonly AgentV1SubagentGuard _subagentGuard;
    private readonly AgentCommandsService _commands;
    private readonly SubagentCancellationCoordinator _subagents;

    public AgentRunLifecycleService(IHarnessEventStore store, IAgentRuntimeRootRegistry runtimeRoots,
        IIdGenerator ids, ITimeProvider time, AgentV1SubagentGuard subagentGuard,
        AgentCommandsService commands, SubagentCancellationCoordinator subagents)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _runtimeRoots = runtimeRoots ?? throw new ArgumentNullException(nameof(runtimeRoots));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _subagentGuard = subagentGuard ?? throw new ArgumentNullException(nameof(subagentGuard));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _subagents = subagents ?? throw new ArgumentNullException(nameof(subagents));
    }
    public async Task<AgentServiceResult> CancelAsync(string runIdRaw, CancellationToken cancellationToken)
    {
        if (!IsRunId(runIdRaw))
        {
            return BoundedError(400, "invalid_run_id", "run_id invalid.");
        }

        if (await _subagentGuard.IsInternalRunAsync(new RunId(runIdRaw), cancellationToken).ConfigureAwait(false))
            return BoundedError(409, "subagent_read_only", "Subagents are controlled by the main agent. Send your request to the parent.");

        var runId = new RunId(runIdRaw);
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return BoundedError(404, "run_not_found", "Run not found.");
        }

        if (await _subagents.CancelFamilyAsync(runId, cancellationToken).ConfigureAwait(false))
            run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false) ?? run;

        if (run.State.IsTerminal())
        {
            return new AgentServiceResult(200, AgentRunQueryService.MapRun(run));
        }

        var transition = AgentRunStateMachine.TryTransition(run.Status, HarnessRunTransition.Cancel);
        if (!transition.Accepted)
        {
            return BoundedError(409, transition.ErrorCode ?? "invalid_transition", "Cancel rejected.");
        }

        // Stop live terminal processes before committing the durable cancelled
        // projection. The runtime registry points at the same per-run coordinator
        // that owns the gateway/Job Object; merely changing SQLite state leaves a
        // child process running. The transition is validated first so a rejected
        // cancel cannot kill a healthy run's tools.
        await _runtimeRoots.CancelRunningToolsAsync(runId, cancellationToken).ConfigureAwait(false);

        // Stopping tools can let the worker commit its outcome before this request
        // resumes. Rebase the cancellation on that projection and preserve a run
        // another caller has already settled. Never repeat tool shutdown on conflict.
        const int maximumCommitAttempts = 8;
        for (var attempt = 0; attempt < maximumCommitAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
            if (run is null)
                return BoundedError(404, "run_not_found", "Run not found.");
            if (run.State.IsTerminal())
                return new AgentServiceResult(200, AgentRunQueryService.MapRun(run));

            transition = AgentRunStateMachine.TryTransition(run.Status, HarnessRunTransition.Cancel);
            if (!transition.Accepted)
                return BoundedError(409, transition.ErrorCode ?? "invalid_transition", "Cancel rejected.");

            var now = _time.UtcNow;
            var nextRun = AdvanceRun(run, transition.Next, run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow, 1, now, terminalErrorCode: "cancelled");
            var evt = new HarnessEventIntent(_ids.NewEventId(), HarnessEventKind.RunStateChanged, safeCode: "cancelled");
            var commit = new HarnessCommit(runId, run.Version, nextRun, new[] { evt }, Array.Empty<HarnessPayloadIntent>(), Array.Empty<ConversationOutboxIntent>());
            var result = await _store.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
            if (result.Outcome == HarnessCommitOutcome.Committed)
                return new AgentServiceResult(200, AgentRunQueryService.MapRun(result.Run!));
            if (result.Outcome == HarnessCommitOutcome.RunNotFound)
                return BoundedError(404, "run_not_found", "Run not found.");
        }

        return BoundedError(409, "commit_conflict", "Commit conflict.");
    }
}
