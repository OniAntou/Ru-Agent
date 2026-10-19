using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Reconciliation;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Context;
using RuBackend.Harness;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuBackend.Harness.Subagents;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class SubagentNativeControlTests
{
    private static async Task VerifyUserApplyAsync(BackendHarnessTestFixture storage, InProcessExecutionFixture execution,
        RuAgent.Harness.Core.Subagents.SubagentRecord agent, string repo, PolicySnapshot policy, string mode)
    {
        if (mode == "user_approval")
        {
            var originalRoot = (await storage.EventStore.GetRunAsync(agent.RootRunId, default))!;
            var ask = PolicySnapshotCanonicalizer.Create(policy.Version + 1, policy.Preset, policy.Workspace,
                [new(new("rule_" + new string('1', 32)), PolicyRuleScope.Run, PolicyDecision.Ask,
                    new(ToolId: "write", CanonicalPathPattern: Path.Combine(repo, "a.txt")), 1, null)], DateTimeOffset.UtcNow);
            await execution.Factory.CreateAsync(repo, ask, agent.RootRunId, originalRoot.SessionId.Value, default);
        }
        var actions = new SqliteSubagentUserApplyStore(storage.Options);
        var action = await actions.EnqueueAsync(agent.RootRunId, agent.AgentId, 1, default);

        var drive = new SubagentUserApplyCoordinator(actions, storage.EventStore);
        var root = (await storage.EventStore.GetRunAsync(agent.RootRunId, default))!;
        if (mode == "user_unknown")
        {
            Assert.True(await actions.UpdateAsync(action, "executing", "legacy-fixture", null, null, default));
            Assert.True(await drive.DriveAsync(root, default));
            Assert.Equal("unknown", Assert.Single(await actions.ListAsync(root.RunId, default)).Status);
            var blocked = (await storage.EventStore.GetRunAsync(root.RunId, default))!;
            Assert.Equal(HarnessRunState.ReconciliationRequired, blocked.State);
            Assert.False(await drive.DriveAsync(blocked, default));
            Assert.Equal("parent", await File.ReadAllTextAsync(Path.Combine(repo, "a.txt")));
            return;
        }
        Assert.True(await drive.DriveAsync(root, default));
        var retired = Assert.Single(await actions.ListAsync(root.RunId, default));
        Assert.Equal("cancelled", retired.Status);
        Assert.Equal("subagent_user_control_retired", retired.Code);
        Assert.Equal("parent", await File.ReadAllTextAsync(Path.Combine(repo, "a.txt")));
        Assert.False(await drive.DriveAsync(root, default));
        Assert.Equal("parent", await File.ReadAllTextAsync(Path.Combine(repo, "a.txt")));
    }
}
