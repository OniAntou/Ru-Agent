extern alias Broker;

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
    [Theory]
    [InlineData(false, SubagentRole.Review, null, false)]
    [InlineData(true, SubagentRole.Review, null, false)]
    [InlineData(true, SubagentRole.Coding, null, false)]
    [InlineData(true, SubagentRole.Coding, "unknown_continue", false)]
    [InlineData(true, SubagentRole.Coding, "applied", false)]
    [InlineData(true, SubagentRole.Coding, "not_applied", false)]
    [InlineData(true, SubagentRole.Coding, "unknown_continue", true)]
    [InlineData(true, SubagentRole.Coding, "user_success", false)]
    [InlineData(true, SubagentRole.Coding, "user_unknown", false)]
    [InlineData(true, SubagentRole.Coding, "user_approval", false)]
    [InlineData(true, SubagentRole.Coding, "family_e2e", false)]
    [InlineData(true, SubagentRole.Coding, "non_git", false)]
    [InlineData(true, SubagentRole.Coding, "unborn_git", false)]
    public async Task Native_gateway_controls_use_broker_callback_and_durable_identity(bool enabled, SubagentRole role, string? recoveryDecision, bool failFinish)
    {
        await using var storage = await BackendHarnessTestFixture.CreateAsync();
        using var files = new FixtureFileAttributes(storage.RootPath);
        var repo = Path.Combine(storage.RootPath, "repo");
        Directory.CreateDirectory(repo);
        await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "parent");
        if (role == SubagentRole.Coding && recoveryDecision is not ("non_git" or "unborn_git")) await InitializeRepoAsync(repo);
        if (recoveryDecision == "unborn_git")
        {
            var init = await new Broker::RuAgent.Broker.Tools.Git.StreamingGitCli().RunAsync(["init"], repo, default);
            Assert.True(init.Succeeded, init.FailureReason);
        }
        SubagentControlDispatcher? dispatch = null;
        using var execution = new InProcessExecutionFixture(repo, subagentControls: enabled,
            subagents: new Lazy<SubagentControlDispatcher>(() => dispatch!));
        RuBackend.Business.Sessions.SessionStore? familySession = null;
        var sessionId = new string('a', 32);
        if (recoveryDecision == "family_e2e")
        {
            familySession = new(new RuBackend.Business.DataPaths(Path.Combine(storage.RootPath, "settings.json"),
                Path.Combine(storage.RootPath, "sessions"), Path.Combine(storage.RootPath, "activity.log")), TimeProvider.System);
            sessionId = (await familySession.CreateAsync(default))!;
        }
        var seeded = await storage.SeedRunWithEncryptedWorkspaceAsync(new(HarnessRunState.Running),
            "run_" + new string('a', 32), sessionId, repo);
        var root = seeded.Run;
        var resolver = new WindowsWorkspaceIdentityResolver();
        var policy = PolicySnapshotCanonicalizer.Create(1, root.Preset, await resolver.ResolveAsync(repo, default), [], BackendHarnessTestFixture.Timestamp);
        var roots = new AgentRuntimeRootRegistry();
        roots.Register(root.RunId, repo, policy);
        var gateway = await execution.Factory.CreateAsync(repo, policy, root.RunId, root.SessionId.Value, default);
        var agents = new SqliteSubagentStore(storage.Options, storage.PayloadStore);
        var protector = new WorkspaceJournalProtector(storage.KeyStore);
        var scopes = new SqliteSubagentScopeStore(storage.Options, protector);
        var pins = new SqliteAgentRunProviderSelectionStore(storage.Options);
        var paths = new SubagentWorkspaceStorage(Path.Combine(storage.RootPath, "workspaces"));
        var allocations = new SqliteSubagentWorkspaceAllocationStore(storage.Options, protector);
        var workspace = new SubagentWorkspaceFactory(execution.Factory,
            new WorkspaceTrustService(resolver, new SqliteWorkspaceStore(storage.Options), new WorkspaceTrustRequestStore(storage.Options), storage.KeyStore),
            paths.Root, allocations);
        using var children = new SubagentRunFactory(storage.EventStore, agents, pins, storage.PayloadStore, scopes,
            roots, new Selection(), workspace, new SystemTimeProvider());
        var cancel = new SubagentCancellationCoordinator(storage.EventStore, agents,
            new Cleanup(storage, execution, roots), new SystemTimeProvider());
        var results = new SqliteSubagentResultStore(storage.Options, storage.PayloadStore);
        var changes = new SqliteSubagentChangeSetStore(storage.Options, protector);
        var journal = new SqliteSubagentApplyJournal(storage.Options, protector);
        using var apply = new SubagentApplyService(storage.EventStore, agents, results, changes, journal, execution.Factory);
        using var publisher = new SubagentCodingResultPublisher(storage.EventStore, agents, scopes, changes, results,
            journal, roots, execution.Factory, paths);
        dispatch = new(storage.EventStore, agents, children, cancel, apply);

        ToolProposal Proposal(string tool, object arguments, char call) => new(root.RunId,
            new("step_" + new string('d', 32)), new("call_" + new string(call, 32)), "provider_" + call,
            1, tool, "1.0.0", JsonSerializer.SerializeToElement(arguments));
        async Task<ToolExecutionReceipt> Execute(ToolProposal proposal, ToolAccessKind kind)
        {
            var decision = await gateway.EvaluateAsync(proposal, default);
            Assert.True(decision.Decision == PolicyDecision.Allow, proposal.ToolId + ": " + decision.SafeCode);
            Assert.Equal(kind, decision.AccessKind);
            var prepared = await gateway.PrepareAsync(new(proposal, new("exec_" + Guid.NewGuid().ToString("N")),
                decision.CanonicalIntentToken!, decision.GatewayGeneration), default);
            var result = await gateway.ExecuteAsync(prepared, _ => ValueTask.CompletedTask, default);
            Assert.Equal(prepared.ExecutionId, result.ExecutionId);
            Assert.Equal(prepared.ToolCallId, result.ToolCallId);
            return result;
        }
        if (recoveryDecision == "family_e2e")
        {
            await VerifyFamilyScenarioAsync(storage, execution, agents, scopes, publisher, root, repo, policy, roots, familySession!);
            return;
        }
        var spawn = Proposal("agent_spawn", new { role = role.ToString().ToLowerInvariant(), task = "Inspect parser" }, 'b');
        if (!enabled)
        {
            var denied = await gateway.EvaluateAsync(spawn, default);
            Assert.Equal(PolicyDecision.Deny, denied.Decision);
            Assert.Equal("phase_not_available", denied.SafeCode);
            Assert.Empty(await agents.ListAsync(root.RunId, default));
            return;
        }
        var spawned = await Execute(spawn, ToolAccessKind.Mutation);
        if (recoveryDecision is "non_git" or "unborn_git")
        {
            Assert.Equal(ToolExecutionOutcome.Failed, spawned.Outcome);
            Assert.False(spawned.SideEffectsPossible);
            Assert.Equal(recoveryDecision == "non_git" ? "subagent_git_root_unavailable" : "subagent_git_head_unavailable", spawned.Code);
            Assert.Empty(await agents.ListAsync(root.RunId, default));
            Assert.Empty(await allocations.ListAsync(root.RunId, default));
            Assert.False(Directory.Exists(paths.Root));
            Assert.Equal(ToolExecutionOutcome.Failed, (await Execute(spawn, ToolAccessKind.Mutation)).Outcome);
            Assert.Empty(await allocations.ListAsync(root.RunId, default));
            // The parent can recover by delegating research without a Git workspace.
            var research = await Execute(Proposal("agent_spawn", new { role = "review", task = "Research today's gold price using web_search" }, 'e'), ToolAccessKind.Mutation);
            Assert.Equal(ToolExecutionOutcome.Succeeded, research.Outcome);
            Assert.Single(await agents.ListAsync(root.RunId, default));
            return;
        }
        Assert.Equal(ToolExecutionOutcome.Succeeded, spawned.Outcome);
        using var output = JsonDocument.Parse(spawned.ModelContent);
        var agentId = output.RootElement.GetProperty("agent_id").GetString()!;
        var record = Assert.Single(await agents.ListAsync(root.RunId, default));
        Assert.Equal(agentId, record.AgentId);
        var repeated = await Execute(spawn, ToolAccessKind.Mutation);
        Assert.Equal(ToolExecutionOutcome.Succeeded, repeated.Outcome);
        Assert.Single(await agents.ListAsync(root.RunId, default));
        var send = Proposal("agent_send", new { agent_id = agentId, message = "Inspect empty input" }, 'c');
        Assert.Equal(ToolExecutionOutcome.Succeeded, (await Execute(send, ToolAccessKind.Mutation)).Outcome);
        Assert.Equal(ToolExecutionOutcome.Succeeded, (await Execute(send, ToolAccessKind.Mutation)).Outcome);
        Assert.True(await agents.TryAdmitAsync(agentId, DateTimeOffset.UtcNow, default));
        var attempt = (await agents.GetLatestAttemptAsync(root.RunId, agentId, default))!;
        Assert.Equal(1, await agents.DeliverMessagesAsync(attempt.Run.RunId, DateTimeOffset.UtcNow, default));
        Assert.Equal(0, await agents.DeliverMessagesAsync(attempt.Run.RunId, DateTimeOffset.UtcNow, default));
        await VerifyDeferredWaitAsync(storage, execution, root, repo, policy, agentId);
        if (role == SubagentRole.Coding)
            await CompleteAndApplyCodingAsync(storage, execution, scopes, record, attempt, publisher, Execute, recoveryDecision, policy, failFinish);
        if (recoveryDecision is not null) return;
        var beforeCancel = (await storage.EventStore.GetRunAsync(root.RunId, default))!;
        Assert.Equal(ToolExecutionOutcome.Succeeded,
            (await Execute(Proposal("agent_cancel", new { agent_id = agentId }, 'f'), ToolAccessKind.Mutation)).Outcome);
        Assert.Equal(role == SubagentRole.Coding ? HarnessRunState.Completed : HarnessRunState.Cancelled,
            (await storage.EventStore.GetRunAsync(attempt.Run.RunId, default))!.State);
        Assert.Equal(beforeCancel, await storage.EventStore.GetRunAsync(root.RunId, default));
        Assert.Equal(role == SubagentRole.Coding ? "later parent edit" : "parent", await File.ReadAllTextAsync(Path.Combine(repo, "a.txt")));
    }

    private sealed class Selection : ISubagentModelSelectionProvider
    {
        public Task<AgentRunProviderSelection> ResolveAsync(RunId root, SubagentRole role, CancellationToken ct, string? model = null, string? providerId = null) =>
            Task.FromResult(new AgentRunProviderSelection("Custom OpenAI", "custom", "chat_completions",
                "https://example.invalid/v1/", "fixture-model", null, "{}", null));
    }

    private sealed class Cleanup(BackendHarnessTestFixture storage, InProcessExecutionFixture execution,
        AgentRuntimeRootRegistry roots) : IAgentRuntimeRunCleanup
    {
        public async Task CancelAndRemoveRunAsync(RunId id, CancellationToken ct)
        {
            execution.Runtime.CancelRun(id.Value);
            var run = await storage.EventStore.GetRunAsync(id, ct);
            if (run is not null) await execution.Factory.CloseSessionAsync(run.SessionId.Value, ct);
            roots.Remove(id);
        }
    }
}
