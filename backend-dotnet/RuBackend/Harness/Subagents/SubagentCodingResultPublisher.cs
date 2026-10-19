using System.Text.Json;
using RuAgent.Broker.Subagents.Workspaces;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;
using RuBackend.Harness.Broker;

namespace RuBackend.Harness.Subagents;

/// <summary>Seals coding output once, before any continuation can reuse its worktree.</summary>
public sealed partial class SubagentCodingResultPublisher(IHarnessEventStore events, SqliteSubagentStore agents,
    SqliteSubagentScopeStore scopes, SqliteSubagentChangeSetStore changes, SqliteSubagentResultStore results,
    SqliteSubagentApplyJournal journal, IAgentRuntimeRootRegistry roots, IToolGatewayFactory gateways,
    SubagentWorkspaceStorage storage) : IDisposable
{
    private readonly SemaphoreSlim _publish = new(1, 1);

    public async Task<bool> PublishAsync(SubagentPublicationCandidate candidate, CancellationToken ct)
    {
        await _publish.WaitAsync(ct).ConfigureAwait(false);
        try { return await PublishCoreAsync(candidate, ct).ConfigureAwait(false); }
        finally { _publish.Release(); }
    }

    private async Task<bool> PublishCoreAsync(SubagentPublicationCandidate candidate, CancellationToken ct)
    {
        var run = await events.GetRunAsync(candidate.RunId, ct).ConfigureAwait(false)
            ?? throw new HarnessStoreException("subagent_not_found");
        var owner = await agents.FindOwnerAsync(run.SessionId, ct).ConfigureAwait(false);
        if (candidate.Role != SubagentRole.Coding || run.State != HarnessRunState.Completed || owner is null
            || owner.Role != SubagentRole.Coding || owner.RootRunId != candidate.RootRunId || owner.AgentId != candidate.AgentId)
            throw new HarnessStoreException("subagent_result_ownership_denied");
        // Replays validate persisted identity before touching any mutable artifact.
        if ((await results.ListAsync(owner.RootRunId, owner.AgentId, ct).ConfigureAwait(false)).Any(r => r.RunId == run.RunId)) return true;
        if (roots.TryGetRoot(run.RunId, out _)) return false;
        var latest = await agents.GetLatestAttemptAsync(owner.RootRunId, owner.AgentId, ct).ConfigureAwait(false)
            ?? throw new HarnessStoreException("subagent_not_found");
        try
        {
            var frozen = await changes.GetAsync(owner.RootRunId, owner.AgentId, run.RunId, ct).ConfigureAwait(false);
            if (frozen is null)
            {
                if (latest.Run.RunId != run.RunId)
                    return await FailAsync(candidate, "subagent_result_attempt_advanced", ct).ConfigureAwait(false);
                // An absent runtime registration is not proof that orphaned terminal
                // pumps drained. Close the exact internal session before reading bytes.
                await DrainAsync(run, ct).ConfigureAwait(false);
                await CaptureAsync(run, owner, latest.AttemptNumber, ct).ConfigureAwait(false);
            }
            await results.PublishTerminalAsync(owner.RootRunId, owner.AgentId, run.RunId, ct).ConfigureAwait(false);
            return true;
        }
        catch (HarnessStoreException error) when (error.Code is "subagent_apply_reconciliation_required" or "subagent_cleanup_incomplete")
        { return false; }
        catch (HarnessStoreException error) { return await FailAsync(candidate, error.Code, ct).ConfigureAwait(false); }
        catch (SubagentWorkspaceException error) { return await FailAsync(candidate, error.Code, ct).ConfigureAwait(false); }
        catch (AgentProtocolException error) when (error.Code == "subagent_collection_busy") { return false; }
        catch (AgentProtocolException error) { return await FailAsync(candidate, error.Code, ct).ConfigureAwait(false); }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { return await FailAsync(candidate, "subagent_scope_invalid", ct).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return await FailAsync(candidate, "subagent_result_capture_failed", ct).ConfigureAwait(false); }
    }

    private async Task DrainAsync(AgentRun run, CancellationToken ct)
    {
        try { await gateways.CloseSessionAsync(run.SessionId.Value, ct).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or TimeoutException or AgentProtocolException
            || error is OperationCanceledException && !ct.IsCancellationRequested)
        { throw new HarnessStoreException("subagent_cleanup_incomplete"); }
    }

    private async Task<bool> FailAsync(SubagentPublicationCandidate candidate, string code, CancellationToken ct)
    {
        await results.PublishFailureAsync(candidate.RootRunId, candidate.AgentId, candidate.RunId, code, ct).ConfigureAwait(false);
        return true;
    }

    public void Dispose() => _publish.Dispose();
}
