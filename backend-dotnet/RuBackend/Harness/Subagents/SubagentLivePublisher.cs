using System.Collections.Concurrent;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;

namespace RuBackend.Harness.Subagents;

/// <summary>Loss-tolerant root hints; list snapshots are authoritative on attach/reconnect.</summary>
public sealed class SubagentLivePublisher(SqliteSubagentSnapshotReader snapshots, SqliteSubagentStore agents,
    SqliteSubagentUserApplyStore actions, HarnessLiveEventHub hub)
{
    private readonly ConcurrentDictionary<string, string> _last = new(StringComparer.Ordinal);

    public async Task PublishAsync(RunId root, CancellationToken ct)
    {
        if (!hub.HasSubscribers(root)) { Forget(root); return; }
        if (await agents.IsInternalRunAsync(root, ct).ConfigureAwait(false)) return;
        if ((await agents.ListAsync(root, ct).ConfigureAwait(false)).Count == 0) return;
        var snapshot = await snapshots.ReadAsync(root, ct).ConfigureAwait(false);
        var queue = await actions.ListAsync(root, ct).ConfigureAwait(false);
        var children = snapshot.Agents.Select(a => new
        {
            agent_id = a.Agent.AgentId,
            canceled = a.Agent.Canceled,
            attempts = a.Attempts.Select(t => new { attempt_run_id = t.Attempt.Run.RunId.Value,
                attempt_number = t.Attempt.AttemptNumber, version = t.Attempt.Run.Version,
                status = AgentV1Shared.MapRunState(t.Attempt.Run.Status) }).ToArray(),
            results = a.Results.Select(r => new { result_version = r.Result.Version,
                status = AgentV1Shared.MapRunState(new(r.Result.State)), apply_status = r.Apply?.Status.ToString(),
                acknowledged = r.Acknowledged }).ToArray(),
        }).ToArray();
        var json = JsonSerializer.Serialize(new { root_run_id = root.Value, root_version = snapshot.Root.Version,
            agents = children, action_revisions = queue.Select(a => new { agent_id = a.AgentId, result_version = a.Version, revision = a.Revision }) });
        if (_last.TryGetValue(root.Value, out var prior) && prior == json) return;
        _last[root.Value] = json;
        hub.TryPublish(root, "subagent.changed", json);
    }

    public void Forget(RunId root) => _last.TryRemove(root.Value, out _);
}
