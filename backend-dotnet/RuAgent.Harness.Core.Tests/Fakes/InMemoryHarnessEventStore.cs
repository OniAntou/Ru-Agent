using System.Runtime.CompilerServices;
using RuAgent.Harness.Core.Persistence;

namespace RuAgent.Harness.Core.Tests.Fakes;

public sealed class InMemoryHarnessEventStore : IHarnessEventStore
{
    private readonly object _gate = new();
    private readonly Dictionary<RunId, AgentRun> _runs = new();
    private readonly Dictionary<RunId, List<HarnessEventEnvelope>> _events = new();
    private readonly IList<string>? _trace;

    public InMemoryHarnessEventStore(IList<string>? trace = null) => _trace = trace;

    public List<HarnessCommit> Commits { get; } = new();

    public bool ReturnVersionConflict { get; init; }

    public void Seed(AgentRun run)
    {
        lock (_gate)
        {
            _runs.Add(run.RunId, run);
            _events.Add(run.RunId, new List<HarnessEventEnvelope>());
        }
    }

    public Task<CreateRunResult> TryCreateRunAsync(
        NewAgentRun request,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_runs.ContainsKey(request.Run.RunId))
            {
                return Task.FromResult(CreateRunResult.AlreadyExists(_runs[request.Run.RunId]));
            }

            _runs.Add(request.Run.RunId, request.Run);
            _events.Add(request.Run.RunId, new List<HarnessEventEnvelope>());
            return Task.FromResult(CreateRunResult.Created(request.Run));
        }
    }

    public Task<AgentRun?> GetRunAsync(RunId runId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_runs.GetValueOrDefault(runId));
        }
    }

    public Task<HarnessCommitResult> CommitAsync(
        HarnessCommit commit,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(commit.RunId, out var current))
            {
                return Task.FromResult(HarnessCommitResult.RunNotFound());
            }

            if (current.Version != commit.ExpectedVersion)
            {
                return Task.FromResult(HarnessCommitResult.VersionConflict(current));
            }

            if (ReturnVersionConflict)
            {
                return Task.FromResult(HarnessCommitResult.VersionConflict(current));
            }

            if (commit.NextRun.LastSequence != current.LastSequence + commit.Events.Count)
            {
                throw new InvalidOperationException("next_run_sequence_mismatch");
            }

            var envelopes = _events[commit.RunId];
            for (var index = 0; index < commit.Events.Count; index++)
            {
                var intent = commit.Events[index];
                var sequence = current.LastSequence + index + 1;
                envelopes.Add(new HarnessEventEnvelope(
                    1,
                    new HarnessEvent(
                        intent.EventId,
                        commit.RunId,
                        sequence,
                        EventKindName(intent.Kind),
                        commit.NextRun.UpdatedAt,
                        payloadId: intent.PayloadId,
                        modelStepId: intent.ModelStepId,
                        toolCallId: intent.ToolCallId,
                        executionId: intent.ExecutionId,
                        approvalId: intent.ApprovalId,
                        safeCount: intent.SafeCount,
                        safeCode: intent.SafeCode,
                        toolOutcome: intent.ToolOutcome,
                        sideEffectsPossible: intent.SideEffectsPossible)));
                _trace?.Add($"commit:{intent.Kind}");
            }

            _runs[commit.RunId] = commit.NextRun;
            Commits.Add(commit);
            return Task.FromResult(HarnessCommitResult.Committed(commit.NextRun));
        }
    }

    public async IAsyncEnumerable<HarnessEventEnvelope> ReadEventsAsync(
        RunId runId,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        HarnessEventEnvelope[] snapshot;
        lock (_gate)
        {
            snapshot = _events.GetValueOrDefault(runId)?
                .Where(item => item.Event.Sequence > afterSequence)
                .ToArray()
                ?? Array.Empty<HarnessEventEnvelope>();
        }

        foreach (var item in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }
    }

    private static string EventKindName(HarnessEventKind kind) => kind switch
    {
        HarnessEventKind.RunStateChanged => "run.state_changed",
        HarnessEventKind.RunFailed => "run.failed",
        HarnessEventKind.ModelStepStarted => "model.step_started",
        HarnessEventKind.ModelStepCompleted => "model.step_completed",
        HarnessEventKind.ToolProposed => "tool.proposed",
        HarnessEventKind.ToolExecutionRequested => "tool.execution_requested",
        HarnessEventKind.ToolExecutionCompleted => "tool.execution_completed",
        HarnessEventKind.ApprovalRequested => "approval.requested",
        HarnessEventKind.ContinueRequested => "continue.requested",
        HarnessEventKind.ReconciliationRequired => "reconciliation.required",
        HarnessEventKind.ReconciliationResolved => "reconciliation.resolved",
        HarnessEventKind.TransientFramesDropped => "transient.frames_dropped",
        HarnessEventKind.ModelPromptComposed => "model.prompt_composed",
        HarnessEventKind.ModelUsageRecorded => "model.usage_recorded",
        HarnessEventKind.ContextCompacted => "context.compacted",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
