using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed partial class SubagentSnapshotReaderTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(40000)]
    public async Task Approval_details_are_exact_owned_and_bounded(int contentLength)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var root = fixture.NewRun(1, 1, 1);
        var child = fixture.NewRun(2, 2, 1);
        var events = fixture.CreateEventStore();
        await events.TryCreateRunAsync(root, default);
        await new SqliteSubagentStore(fixture.Options, fixture.PayloadStore).CreateAsync(
            new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Coding, child), default);
        var run = child.Run;
        var payload = new PayloadId("payload_" + new string('c', 32));
        var step = new ModelStepId("step_" + new string('c', 32));
        var call = new ToolCallId("call_" + new string('c', 32));
        var approval = new ApprovalId("apr_" + new string('c', 32));
        var bytes = ToolArgumentsCodec.Serialize("fixture_proposal", "write", "1.0.0", JsonSerializer.SerializeToElement(
            new { op = "write", path = "a.txt", content = new string('x', contentLength) }));
        var waiting = new AgentRun(run.RunId, run.SessionId, run.WorkspaceId, new(HarnessRunState.WaitingApproval),
            run.Version + 1, run.LastSequence + 2, run.BudgetWindow, 0, 0, run.Preset, run.PolicySnapshotVersion,
            run.PolicySnapshotSha256, run.CreatedAt, run.UpdatedAt);
        await events.CommitAsync(new(run.RunId, run.Version, waiting,
            [new(new("evt_" + new string('c', 32)), HarnessEventKind.ToolProposed, modelStepId: step, toolCallId: call,
                providerCallId: "fixture_proposal", payloadId: payload),
             new(new("evt_" + new string('d', 32)), HarnessEventKind.ApprovalRequested, modelStepId: step, toolCallId: call, approvalId: approval)],
            [new(payload, HarnessPayloadKind.ToolArguments, HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Json, bytes.Length, bytes)], []), default);
        var snapshot = await new SqliteSubagentSnapshotReader(fixture.Options, fixture.PayloadStore).ReadAsync(root.Run.RunId, default);
        var details = Assert.Single(Assert.Single(snapshot.Agents).Attempts).Approval!;
        Assert.Equal(approval, details.ApprovalId);
        if (contentLength > 32768) Assert.Equal("subagent_approval_details_too_large", details.Arguments.GetProperty("error_code").GetString());
        else Assert.Equal(new string('x', contentLength), details.Arguments.GetProperty("content").GetString());
    }
}
