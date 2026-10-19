using System.Security.Cryptography;
using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Context;
using RuAgent.Harness.Persistence.Subagents;

namespace RuBackend.Harness.Subagents;

public sealed class SubagentContinuationFactory(IHarnessEventStore events, SqliteSubagentStore agents,
    SqliteAgentRunProviderSelectionStore selections, HarnessPayloadStore payloads, SqliteSubagentScopeStore scopes,
    ITimeProvider time, HarnessSqliteOptions options) : IDisposable
{
    private readonly SemaphoreSlim _prepare = new(1, 1);

    public async Task<SubagentAttempt?> ContinueAsync(RunId rootId, string agentId, CancellationToken ct)
    {
        await _prepare.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var root = await events.GetRunAsync(rootId, ct).ConfigureAwait(false);
            var latest = await agents.GetLatestAttemptAsync(rootId, agentId, ct).ConfigureAwait(false)
                ?? throw new HarnessStoreException("subagent_not_found");
            if (root is null || root.State.IsTerminal() || latest.Agent.Canceled)
                throw new HarnessStoreException("subagent_root_inactive");
            if (!latest.Run.State.IsTerminal())
            {
                if (latest.Run.State == HarnessRunState.Created && latest.AttemptNumber > 1)
                    await PrepareAsync(rootId, latest, ct).ConfigureAwait(false);
                return latest;
            }
            if (!await agents.HasUnscheduledMessagesAsync(rootId, agentId, ct).ConfigureAwait(false)) return null;
            // Validate closed native history before creating another attempt. A
            // partial call/result pair belongs to reconciliation, never blind replay.
            await new SqliteModelContextSource(options, payloads).LoadForSubagentContinuationAsync(latest.Run.RunId, ct).ConfigureAwait(false);
            if (await scopes.GetAsync(latest.Run.RunId, ct).ConfigureAwait(false) is null)
                throw new HarnessStoreException("subagent_scope_missing");
            var identity = Hash(agentId + ":" + latest.Run.RunId.Value + ":continue");
            var now = time.UtcNow < latest.Run.UpdatedAt ? latest.Run.UpdatedAt : time.UtcNow;
            var previous = latest.Run;
            var run = new AgentRun(new("run_" + identity[..32]), previous.SessionId, previous.WorkspaceId,
                new(HarnessRunState.Created), 0, 1, 1, 0, 0, previous.Preset, previous.PolicySnapshotVersion,
                previous.PolicySnapshotSha256, now, now, permissionProfileVersion: previous.PermissionProfileVersion);
            var create = new NewAgentRun(run,
                [new(new("evt_" + identity[..32]), HarnessEventKind.RunStateChanged, safeCode: "subagent_continuation_queued")], [], [],
                new(Hash("invocation:" + identity), Hash("request:" + identity), 1));
            var attempt = await agents.CreateContinuationAsync(rootId, agentId, previous.RunId, create, ct).ConfigureAwait(false);
            if (attempt?.Run.State == HarnessRunState.Created) await PrepareAsync(rootId, attempt, ct).ConfigureAwait(false);
            return attempt;
        }
        finally { _prepare.Release(); }
    }

    private async Task PrepareAsync(RunId root, SubagentAttempt attempt, CancellationToken ct)
    {
        if ((await selections.GetAsync(attempt.Run.RunId, ct).ConfigureAwait(false))?.Selection is null)
            throw new HarnessStoreException("subagent_model_pin_missing");
        var previous = await agents.GetAttemptRunIdAsync(root, attempt.Agent.AgentId, attempt.AttemptNumber - 1, ct).ConfigureAwait(false)
            ?? throw new HarnessStoreException("subagent_context_predecessor_missing");
        var scope = await scopes.GetAsync(previous, ct).ConfigureAwait(false)
            ?? throw new HarnessStoreException("subagent_scope_missing");
        await payloads.CaptureBaseContextAsync(attempt.Run.RunId, [], time.UtcNow, ct).ConfigureAwait(false);
        await scopes.SaveAsync(root, attempt.Run.RunId, scope, ct).ConfigureAwait(false);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public void Dispose() => _prepare.Dispose();
}
