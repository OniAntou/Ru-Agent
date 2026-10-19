using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence;
using RuBackend.Harness.Subagents;
using RuBackend.Harness.Workspace;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class SubagentParentLeaseTests
{
    [Fact]
    public async Task Recovery_cannot_open_parent_lease_for_replaced_physical_directory()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        using var execution = new InProcessExecutionFixture(fixture.RootPath);
        var actual = await new WindowsWorkspaceIdentityResolver().ResolveAsync(fixture.RootPath, default);
        var stale = new WorkspaceIdentity(actual.WorkspaceId, actual.VolumeSerialNumber, "replaced-directory", actual.DisplayIdentityHash);
        var policy = PolicySnapshotCanonicalizer.Create(1, AgentPermissionPreset.Standard, stale, [], DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var run = new AgentRun(new("run_" + new string('a', 32)), new(new string('a', 32)), stale.WorkspaceId,
            new(HarnessRunState.WaitingSubagents), 0, 1, 1, 0, 0, policy.Preset, policy.Version, policy.Sha256, now, now);
        var error = await Assert.ThrowsAsync<HarnessStoreException>(() => SubagentParentLeaseRestorer.EnsureAsync(
            execution.Factory, run, fixture.RootPath, policy, now, default));
        Assert.Equal("subagent_parent_workspace_identity_changed", error.Code);
    }
}
