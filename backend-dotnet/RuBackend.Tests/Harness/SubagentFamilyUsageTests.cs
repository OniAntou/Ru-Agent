using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence;
using RuBackend.Harness.Subagents;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class SubagentFamilyUsageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Usage_counts_only_owned_durable_receipts_and_marks_missing_fields(bool missing)
    {
        await using var storage = await BackendHarnessTestFixture.CreateAsync();
        var root = (await storage.SeedRunWithEncryptedWorkspaceAsync(new(HarnessRunState.Running),
            "run_" + new string('a', 32), new string('a', 32))).Run;
        var child = (await storage.SeedRunWithEncryptedWorkspaceAsync(new(HarnessRunState.Running),
            "run_" + new string('b', 32), new string('b', 32))).Run;
        var unrelated = (await storage.SeedRunWithEncryptedWorkspaceAsync(new(HarnessRunState.Running),
            "run_" + new string('c', 32), new string('c', 32))).Run;
        await using (var connection = await storage.Options.OpenConnectionAsync(default))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO agent_internal_runs(run_id,root_run_id) VALUES($child,$root)";
            command.Parameters.AddWithValue("$child", child.RunId.Value);
            command.Parameters.AddWithValue("$root", root.RunId.Value);
            await command.ExecuteNonQueryAsync();
        }
        await RecordAsync(storage, root, ["{\"input_tokens\":10,\"output_tokens\":2,\"total_tokens\":12}"]);
        await RecordAsync(storage, child, missing
            ? ["{\"input_tokens\":20}", null]
            : ["{\"input_tokens\":20,\"output_tokens\":3,\"total_tokens\":23}", "{\"input_tokens\":0,\"output_tokens\":0,\"total_tokens\":0}"]);
        await RecordAsync(storage, unrelated, ["{\"total_tokens\":9999}"]);

        var service = new SubagentFamilyUsageReader(storage.Options, storage.PayloadStore);
        var usage = await service.ReadAsync(root.RunId, default);
        Assert.Equal(3, usage.ModelSteps);
        Assert.Equal(missing ? 2 : 3, usage.ReportedSteps);
        Assert.Equal(30, usage.InputTokens);
        Assert.Equal(missing ? 2 : 5, usage.OutputTokens);
        Assert.Equal(missing ? 12 : 35, usage.TotalTokens);
        Assert.Equal(!missing, usage.IsComplete);
        Assert.Equal(usage, await service.ReadAsync(root.RunId, default));
        var detailed = await service.ReadDetailedAsync(root.RunId, default);
        Assert.Equal(usage, detailed.Family);
        Assert.Equal(12, detailed.Runs[root.RunId.Value].TotalTokens);
        Assert.False(detailed.Runs.ContainsKey(unrelated.RunId.Value));
        var rejected = await Assert.ThrowsAsync<HarnessStoreException>(() => service.ReadAsync(child.RunId, default));
        Assert.Equal("subagent_root_not_found", rejected.Code);
    }

    [Theory]
    [InlineData("{\"input_tokens\":-1}")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    public async Task Missing_or_corrupt_usage_is_unknown_and_cancellation_propagates(string corrupt)
    {
        await using var storage = await BackendHarnessTestFixture.CreateAsync();
        var root = (await storage.SeedRunAsync(new(HarnessRunState.Running))).Run;
        var service = new SubagentFamilyUsageReader(storage.Options, storage.PayloadStore);
        var empty = await service.ReadAsync(root.RunId, default);
        Assert.Null(empty.TotalTokens);
        Assert.False(empty.IsComplete);
        await RecordAsync(storage, root, [corrupt, null]);
        var unreadable = await service.ReadAsync(root.RunId, default);
        Assert.Equal(2, unreadable.ModelSteps);
        Assert.Equal(0, unreadable.ReportedSteps);
        Assert.Null(unreadable.InputTokens);
        Assert.False(unreadable.IsComplete);
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadAsync(root.RunId, stopped.Token));
    }

    private static async Task RecordAsync(BackendHarnessTestFixture storage, AgentRun run, string?[] reports)
    {
        var events = new List<HarnessEventIntent>();
        var payloads = new List<HarnessPayloadIntent>();
        foreach (var report in reports)
        {
            var step = new ModelStepId("step_" + Guid.NewGuid().ToString("N"));
            events.Add(new(new("evt_" + Guid.NewGuid().ToString("N")), HarnessEventKind.ModelStepStarted, modelStepId: step));
            events.Add(new(new("evt_" + Guid.NewGuid().ToString("N")), HarnessEventKind.ModelStepCompleted, modelStepId: step));
            if (report is null) continue;
            var bytes = Encoding.UTF8.GetBytes(report);
            var payload = new PayloadId("payload_" + Guid.NewGuid().ToString("N"));
            payloads.Add(new(payload, HarnessPayloadKind.ModelUsage, HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Json, bytes.Length, bytes));
            events.Add(new(new("evt_" + Guid.NewGuid().ToString("N")), HarnessEventKind.ModelUsageRecorded,
                modelStepId: step, payloadId: payload));
        }
        var next = new AgentRun(run.RunId, run.SessionId, run.WorkspaceId, run.Status, run.Version + 1,
            run.LastSequence + events.Count, run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow,
            run.Preset, run.PolicySnapshotVersion, run.PolicySnapshotSha256, run.CreatedAt, run.UpdatedAt);
        await storage.EventStore.CommitAsync(new(run.RunId, run.Version, next, events, payloads, []), default);
    }
}
