using System.Text.Json;
using RuAgent.Broker.Subagents.Workspaces;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed partial class SubagentWorkspaceTests
{
    [Fact]
    public async Task Allocation_checkpoints_precede_bulk_copy_and_git_registration()
    {
        var observer = new AllocationObserver(State);
        var snapshot = await new SubagentSnapshotCapture().CaptureAsync(Lease(), State, default, observer);
        var workspace = await new SubagentWorktreeProvisioner().ProvisionAsync(snapshot, observer.Reservation.AgentId, default, observer);
        Assert.Equal(new[] { SubagentWorkspaceAllocationStage.SnapshotAllocated, SubagentWorkspaceAllocationStage.RegisteringWorktree,
            SubagentWorkspaceAllocationStage.WorktreeRegistered, SubagentWorkspaceAllocationStage.Prepared }, observer.Records.Select(r => r.Stage));
        Assert.All(observer.Records, row => Assert.Empty(JsonSerializer.Deserialize<SubagentSnapshot>(row.SnapshotJson!)!.Files));
        Assert.Equal(observer.Records[0].SnapshotJson, observer.Records[^1].SnapshotJson);
        Assert.Equal("clean\r\n", File.ReadAllText(Path.Combine(workspace.WorkspaceRoot, "edit.txt")));
    }

    private sealed class AllocationObserver(string state) : ISubagentWorkspaceAllocationObserver
    {
        public SubagentWorkspaceAllocation Reservation { get; } = new("run_parent", "agent_allocation",
            "snapshot_" + Guid.NewGuid().ToString("N"), SubagentWorkspaceAllocationStage.Reserved);
        public List<SubagentWorkspaceAllocation> Records { get; } = [];

        public Task RecordAsync(SubagentWorkspaceAllocation evidence, CancellationToken ct)
        {
            var snapshot = JsonSerializer.Deserialize<SubagentSnapshot>(evidence.SnapshotJson!)!;
            var child = Path.Combine(state, Reservation.SnapshotId, Reservation.AgentId);
            if (evidence.Stage == SubagentWorkspaceAllocationStage.SnapshotAllocated)
                Assert.False(Directory.Exists(snapshot.BaseDirectory));
            if (evidence.Stage == SubagentWorkspaceAllocationStage.RegisteringWorktree)
                Assert.False(Directory.Exists(child));
            if (evidence.Stage == SubagentWorkspaceAllocationStage.WorktreeRegistered)
            {
                Assert.True(File.Exists(Path.Combine(child, ".git")));
                Assert.False(File.Exists(Path.Combine(child, "edit.txt")));
                Assert.True(Directory.Exists(JsonSerializer.Deserialize<SubagentWorkspaceManifest>(evidence.WorktreeManifestJson!)!.GitDirectory));
            }
            if (evidence.Stage == SubagentWorkspaceAllocationStage.Prepared)
                Assert.Equal("clean\r\n", File.ReadAllText(Path.Combine(child, "edit.txt")));
            Records.Add(evidence);
            return Task.CompletedTask;
        }
    }
}
