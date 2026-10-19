using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;

namespace RuBackend.Harness.Subagents;

/// <summary>Queues followups that arrive during a child's final step; never drives a model.</summary>
public sealed class SubagentContinuationCoordinator(SqliteSubagentStore agents, SubagentContinuationFactory factory) : IDisposable
{
    private readonly SemaphoreSlim _tick = new(1, 1);
    private string? _afterAgent;

    public async Task TickAsync(CancellationToken ct)
    {
        await _tick.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var candidates = await agents.ListContinuationCandidatesAsync(_afterAgent, ct).ConfigureAwait(false);
            if (candidates.Count == 0 && _afterAgent is not null)
                candidates = await agents.ListContinuationCandidatesAsync(null, ct).ConfigureAwait(false);
            foreach (var candidate in candidates)
            {
                try { await factory.ContinueAsync(candidate.RootRunId, candidate.AgentId, ct).ConfigureAwait(false); }
                catch (HarnessStoreException error)
                {
                    // Exhaustion waits for the parent's explicit Continue. Other
                    // named failures belong to the message, so a bad child cannot
                    // block peers or retry a permanent error on every loop tick.
                    if (error.Code is not ("subagent_family_budget_exhausted" or "subagent_root_inactive" or "subagent_result_pending"))
                        await agents.RecordContinuationFailureAsync(candidate, error.Code, ct).ConfigureAwait(false);
                }
            }
            _afterAgent = candidates.Count == SqliteSubagentStore.FamilyPageSize ? candidates[^1].AgentId : null;
        }
        finally { _tick.Release(); }
    }

    public void Dispose() => _tick.Dispose();
}
