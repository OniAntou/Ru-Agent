using System.Text.Json;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Subagents.Workspaces;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed partial class SubagentWorkspaceTests
{
    [Theory]
    [InlineData("reserved_absent")]
    [InlineData("reserved_unproven")]
    [InlineData("snapshot")]
    [InlineData("registering")]
    [InlineData("registered")]
    [InlineData("prepared")]
    [InlineData("busy")]
    [InlineData("wrong_storage")]
    public async Task Restart_cleanup_uses_only_durable_allocation_evidence_before_child_creation(string scenario)
    {
        var observer = new AllocationObserver(State);
        var evidence = observer.Reservation;
        SubagentWorkspaceManifest? manifest = null;
        var owner = Path.Combine(State, evidence.SnapshotId);
        if (scenario == "reserved_unproven")
        {
            Directory.CreateDirectory(owner);
            File.WriteAllText(Path.Combine(owner, "user.txt"), "preserve unknown owner");
        }
        if (!scenario.StartsWith("reserved", StringComparison.Ordinal))
        {
            var snapshot = await new SubagentSnapshotCapture().CaptureAsync(Lease(), State, default, observer);
            evidence = observer.Records[0];
            if (scenario == "registering") evidence = evidence with { Stage = SubagentWorkspaceAllocationStage.RegisteringWorktree };
            if (scenario is "registered" or "prepared" or "busy")
            {
                manifest = await new SubagentWorktreeProvisioner().ProvisionAsync(snapshot, evidence.AgentId, default, observer);
                evidence = observer.Records.Single(r => r.Stage == (scenario == "registered"
                    ? SubagentWorkspaceAllocationStage.WorktreeRegistered : SubagentWorkspaceAllocationStage.Prepared));
            }
        }
        var before = (await Git("status", "--porcelain=v1")).Stdout;
        var session = BrokerSession.CreateForHost(_owned, DateTimeOffset.UtcNow.AddMinutes(30));
        using var tools = new ToolExecutionService(session, Root);
        using var restarted = new BrokerExecutionRuntime(session, tools);
        if (scenario == "busy")
            Assert.Null((await restarted.OpenRunAsync(new AgentEnvelope(2, "open", AgentMessageTypes.RunOpen, "other", null, 10000,
                EnvelopeTestSnapshots.RunOpenPayload("run_other", "other", manifest!.WorkspaceRoot, EnvelopeTestSnapshots.StandardEmpty())), default)).Error);
        if (scenario is "reserved_unproven" or "registering" or "busy" or "wrong_storage")
        {
            var error = await Assert.ThrowsAsync<AgentProtocolException>(() => restarted.ReclaimSubagentAllocationAsync(evidence,
                scenario == "wrong_storage" ? Path.Combine(_owned, "foreign") : State, default));
            Assert.Equal(scenario switch { "registering" => "subagent_cleanup_registration_unknown", "busy" => "subagent_cleanup_busy",
                "wrong_storage" => "subagent_allocation_invalid", _ => "subagent_cleanup_allocation_unproven" }, error.Code);
            Assert.True(Directory.Exists(owner));
            if (scenario == "reserved_unproven") Assert.Equal("preserve unknown owner", File.ReadAllText(Path.Combine(owner, "user.txt")));
        }
        else
        {
            await restarted.ReclaimSubagentAllocationAsync(evidence, State, default);
            await restarted.ReclaimSubagentAllocationAsync(evidence, State, default);
            Assert.False(Directory.Exists(owner));
            if (manifest is not null) Assert.False(Directory.Exists(manifest.GitDirectory));
        }
        Assert.Equal(before, (await Git("status", "--porcelain=v1")).Stdout);
        Assert.Equal("clean\r\n", File.ReadAllText(Path.Combine(Root, "edit.txt")));
    }
}
