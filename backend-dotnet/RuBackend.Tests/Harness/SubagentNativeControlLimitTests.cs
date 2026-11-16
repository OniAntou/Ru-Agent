using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Harness.Persistence.Workspace;
using RuAgent.Protocol.Subagents;
using RuBackend.Harness;
using RuBackend.Harness.Subagents;
using RuBackend.Harness.Workspace;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class SubagentNativeControlTests
{
    [Fact]
    public async Task Spawns_have_no_family_ceiling_and_a_replay_stays_idempotent()
    {
        await using var storage = await BackendHarnessTestFixture.CreateAsync();
        var repo = Path.Combine(storage.RootPath, "repo");
        Directory.CreateDirectory(repo);
        await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "parent");
        SubagentControlDispatcher? dispatch = null;
        using var execution = new InProcessExecutionFixture(repo, subagentControls: true,
            subagents: new Lazy<SubagentControlDispatcher>(() => dispatch!));
        var seeded = await storage.SeedRunWithEncryptedWorkspaceAsync(new(HarnessRunState.Running),
            "run_" + new string('a', 32), new string('a', 32), repo);
        var root = seeded.Run;
        var resolver = new WindowsWorkspaceIdentityResolver();
        var policy = PolicySnapshotCanonicalizer.Create(1, root.Preset,
            await resolver.ResolveAsync(repo, default), [], BackendHarnessTestFixture.Timestamp);
        var roots = new AgentRuntimeRootRegistry();
        roots.Register(root.RunId, repo, policy);
        var gateway = await execution.Factory.CreateAsync(repo, policy, root.RunId, root.SessionId.Value, default);
        var agents = new SqliteSubagentStore(storage.Options, storage.PayloadStore);
        var protector = new WorkspaceJournalProtector(storage.KeyStore);
        var scopes = new SqliteSubagentScopeStore(storage.Options, protector);
        var paths = new SubagentWorkspaceStorage(Path.Combine(storage.RootPath, "workspaces"));
        var allocations = new SqliteSubagentWorkspaceAllocationStore(storage.Options, protector);
        var workspace = new SubagentWorkspaceFactory(execution.Factory,
            new WorkspaceTrustService(resolver, new SqliteWorkspaceStore(storage.Options),
                new WorkspaceTrustRequestStore(storage.Options), storage.KeyStore),
            paths.Root, allocations);
        using var children = new SubagentRunFactory(storage.EventStore, agents,
            new SqliteAgentRunProviderSelectionStore(storage.Options), storage.PayloadStore, scopes,
            roots, new Selection(), workspace, new SystemTimeProvider());
        var cancel = new SubagentCancellationCoordinator(storage.EventStore, agents,
            new Cleanup(storage, execution, roots), new SystemTimeProvider());
        var results = new SqliteSubagentResultStore(storage.Options, storage.PayloadStore);
        var changes = new SqliteSubagentChangeSetStore(storage.Options, protector);
        var journal = new SqliteSubagentApplyJournal(storage.Options, protector);
        using var apply = new SubagentApplyService(storage.EventStore, agents, results, changes, journal, execution.Factory);
        dispatch = new(storage.EventStore, agents, children, cancel, apply);

        ToolProposal Proposal(int call) => new(root.RunId,
            new("step_" + new string('d', 32)), new("call_" + call.ToString("x32")), "provider_" + call.ToString("x"),
            1, "agent_spawn", "1.0.0", JsonSerializer.SerializeToElement(new { role = "review", task = "Inspect parser" }));

        async Task<ToolExecutionReceipt> Execute(ToolProposal proposal)
        {
            var decision = await gateway.EvaluateAsync(proposal, default);
            Assert.Equal(PolicyDecision.Allow, decision.Decision);
            var prepared = await gateway.PrepareAsync(new(proposal, new("exec_" + Guid.NewGuid().ToString("N")),
                decision.CanonicalIntentToken!, decision.GatewayGeneration), default);
            return await gateway.ExecuteAsync(prepared, _ => ValueTask.CompletedTask, default);
        }

        // Twelve is past the eight-identity ceiling this family used to enforce.
        const int spawns = 12;
        for (var index = 0; index < spawns; index++)
        {
            var spawned = await Execute(Proposal(index));
            Assert.Equal(ToolExecutionOutcome.Succeeded, spawned.Outcome);
        }

        Assert.Equal(spawns, (await agents.ListAsync(root.RunId, default)).Count);
        var replay = await gateway.EvaluateAsync(Proposal(spawns - 1), default);
        Assert.Equal(PolicyDecision.Allow, replay.Decision);
        Assert.Equal(ToolExecutionOutcome.Succeeded, (await Execute(Proposal(spawns - 1))).Outcome);
        Assert.Equal(spawns, (await agents.ListAsync(root.RunId, default)).Count);

        var applyProposal = new ToolProposal(root.RunId,
            new("step_" + new string('d', 32)), new("call_" + new string('e', 32)), "provider_e",
            1, "agent_apply", "1.0.0", JsonSerializer.SerializeToElement(new
            {
                agent_id = "agent_" + new string('f', 32),
                result_version = 1,
            }));
        var applyDenied = await gateway.EvaluateAsync(applyProposal, default);

        Assert.Equal(PolicyDecision.Deny, applyDenied.Decision);
        Assert.Equal("subagent_apply_ownership_denied", applyDenied.SafeCode);
    }
}
