using System.Security.Cryptography;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;

namespace RuBackend.Harness.Subagents;

public sealed class SubagentPublicService(SqliteSubagentSnapshotReader snapshots,
    SubagentFamilyUsageReader usage, SqliteSubagentUserApplyStore actions)
{
    public async Task<SubagentListResponse> ListAsync(RunId root, CancellationToken ct)
    {
        var snapshot = await snapshots.ReadAsync(root, ct).ConfigureAwait(false);
        var queued = await actions.ListAsync(root, ct).ConfigureAwait(false);
        var reported = await usage.ReadDetailedAsync(root, ct).ConfigureAwait(false);
        var totals = reported.Family;
        var rows = snapshot.Agents.Select(a => Map(a, queued, reported.Runs)).ToArray();
        var preparations = snapshot.Preparations.Select(a => new SubagentPreparationSummary(a.AgentId, a.Stage.ToString())).ToArray();
        var summary = new SubagentUsageSummary(totals.ModelSteps, totals.ReportedSteps, totals.InputTokens,
            totals.OutputTokens, totals.TotalTokens, totals.IsComplete);
        var status = AgentV1Shared.MapRunState(snapshot.Root.Status);
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { status, rows, preparations, snapshot.UnavailableAttempts, summary })));
        return new(root.Value, status, fingerprint, rows, preparations, snapshot.UnavailableAttempts, summary);
    }

    public async Task<SubagentDetailResponse> DetailAsync(RunId root, string agent, long? version, int offset, int limit, CancellationToken ct)
    {
        var list = await ListAsync(root, ct).ConfigureAwait(false);
        var row = list.Agents.SingleOrDefault(a => a.AgentId == agent) ?? throw new HarnessStoreException("subagent_not_found");
        SubagentResultPageResponse? page = null;
        if (version is { } value)
        {
            var result = await snapshots.ReadResultAsync(root, agent, value, offset, limit, ct).ConfigureAwait(false);
            page = new(value, result.Text, result.Offset, result.TotalBytes, result.NextOffset);
        }
        return new(root.Value, row, page);
    }

    private static SubagentUserActionSummary MapAction(SubagentUserApply a) => new(a.Status, a.Revision, a.ApprovalId, a.Code);
    private static SubagentSummary Map(SubagentSnapshotEntry entry, IReadOnlyList<SubagentUserApply> actions, IReadOnlyDictionary<string, SubagentFamilyUsage> usage) => new(
        entry.Agent.AgentId, entry.Agent.Role.ToString().ToLowerInvariant(), entry.Task, entry.Agent.Canceled,
        entry.Attempts.Select(a => new SubagentAttemptSummary(a.Attempt.Run.RunId.Value, a.Attempt.AttemptNumber,
            AgentV1Shared.MapRunState(a.Attempt.Run.Status), a.Attempt.Run.Version,
            a.Model?.ProviderId, a.Model?.Model, a.Model?.Protocol, a.Attempt.DeadlineAt,
            a.Approval is { } approval ? new(approval.ApprovalId.Value, approval.ToolId, approval.Arguments, approval.ArgumentsTruncated) : null,
            usage.TryGetValue(a.Attempt.Run.RunId.Value, out var reported)
                ? new(reported.ModelSteps, reported.ReportedSteps, reported.InputTokens, reported.OutputTokens, reported.TotalTokens, reported.IsComplete)
                : new(0, 0, null, null, null, false))).ToArray(),
        entry.Results.Select(r => new SubagentResultSummary(r.Result.Version, r.Result.RunId.Value,
            AgentV1Shared.MapRunState(new(r.Result.State)), r.Result.Code, r.Apply?.Status.ToString().ToLowerInvariant(),
            r.Apply?.AppliedFiles, r.Acknowledged,
            actions.SingleOrDefault(a => a.AgentId == entry.Agent.AgentId && a.Version == r.Result.Version) is { } action ? MapAction(action) : null)).ToArray());
}
