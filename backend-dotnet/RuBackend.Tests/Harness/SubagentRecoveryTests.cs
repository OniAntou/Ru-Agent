using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Harness;
using RuAgent.Protocol.Subagents;
using RuBackend.Harness;
using RuBackend.Harness.Broker;
using RuBackend.Harness.Subagents;
using RuBackend.Harness.Workspace;
using RuBackend.Harness.Reconciliation;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class SubagentRecoveryTests
{
    [Theory]
    [InlineData("queued", false)]
    [InlineData("running", false)]
    [InlineData("ambiguous", false)]
    [InlineData("missing_pin", false)]
    [InlineData("missing_scope", false)]
    [InlineData("missing_owner", false)]
    [InlineData("parent_pending", false)]
    [InlineData("queued", true)]
    [InlineData("running", true)]
    [InlineData("missing_pin", true)]
    public async Task Recovery_preserves_frozen_authority_and_never_replays_ambiguous_mutation(string scenario, bool viaStartup)
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        using var execution = new InProcessExecutionFixture(fixture.RootPath);
        var root = await fixture.SeedRunWithEncryptedWorkspaceAsync(new(HarnessRunState.WaitingSubagents),
            "run_" + new string('a', 32), new string('a', 32), fixture.RootPath);
        var identity = await new WindowsWorkspaceIdentityResolver().ResolveAsync(fixture.RootPath, default);
        var policy = PolicySnapshotCanonicalizer.Create(1, root.Run.Preset, identity, [], BackendHarnessTestFixture.Timestamp);
        var policyJson = BrokerPolicyContracts.WriteSnapshot(PolicySnapshotMapper.ToWire(policy)).ToJsonString();
        var now = DateTimeOffset.UtcNow;
        var run = new AgentRun(new("run_" + new string('b', 32)), new(new string('b', 32)), identity.WorkspaceId,
            new(HarnessRunState.Created), 0, 1, 1, 0, 0, policy.Preset, policy.Version, policy.Sha256, now, now);
        var agents = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        await agents.CreateAsync(new("agent_review", root.Run.RunId, "spawn_review", SubagentRole.Review,
            new(run, [new(new("evt_" + new string('b', 32)), HarnessEventKind.RunStateChanged)], [], [],
                new(new string('a', 64), new string('b', 64), 1))), default);
        var pins = new SqliteAgentRunProviderSelectionStore(fixture.Options);
        if (scenario != "missing_pin")
            await pins.PinAsync(run.RunId, new("Custom OpenAI", "custom", "chat_completions",
                "https://example.invalid/v1/", "original-model", null, "{}", null), default);
        await fixture.PayloadStore.CaptureBaseContextAsync(run.RunId, [], now, default);
        var scopes = new SqliteSubagentScopeStore(fixture.Options, new WorkspaceJournalProtector(fixture.KeyStore));
        if (scenario != "missing_scope")
            await scopes.SaveAsync(root.Run.RunId, run.RunId, new(fixture.RootPath, fixture.RootPath,
                new(policy.Version, policy.Sha256), policyJson, null, policyJson), default);
        if (scenario is "running" or "ambiguous")
        {
            Assert.True(await agents.TryAdmitAsync("agent_review", now, default));
            run = (await fixture.EventStore.GetRunAsync(run.RunId, default))!;
        }
        if (scenario == "ambiguous")
        {
            var next = new AgentRun(run.RunId, run.SessionId, run.WorkspaceId, run.Status, run.Version + 1,
                run.LastSequence + 1, 1, 1, 1, run.Preset, run.PolicySnapshotVersion, run.PolicySnapshotSha256, run.CreatedAt, now);
            await fixture.EventStore.CommitAsync(new(run.RunId, run.Version, next,
                [new(new("evt_" + new string('c', 32)), HarnessEventKind.ToolExecutionRequested,
                    modelStepId: new("step_" + new string('c', 32)), toolCallId: new("call_" + new string('c', 32)),
                    executionId: new("exec_" + new string('c', 32)), providerCallId: "provider_mutation", safeCode: "mutation")], [], []), default);
        }
        var roots = new AgentRuntimeRootRegistry();
        if (scenario != "parent_pending") roots.Register(root.Run.RunId, fixture.RootPath, policy);
        var recovery = new SubagentRecoveryCoordinator(fixture.EventStore, agents, scopes, pins, roots,
            execution.Factory, new SystemTimeProvider(), fixture.Options, fixture.PayloadStore);
        if (scenario == "missing_owner")
        {
            await using var connection = await fixture.Options.OpenConnectionAsync(default);
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM agent_subagents WHERE agent_id='agent_review'; DELETE FROM agent_run_provider_selections WHERE run_id=$run; INSERT INTO agent_run_provider_selections(run_id) VALUES($run)";
            command.Parameters.AddWithValue("$run", run.RunId.Value);
            await command.ExecuteNonQueryAsync();
            Assert.True(await recovery.IsInternalRunAsync(run.RunId, default));
            Assert.True(await agents.IsInternalSessionAsync(run.SessionId, default));
            Assert.Null(await pins.GetAsync(run.RunId, default));
        }
        if (scenario == "parent_pending")
        {
            Assert.False(await recovery.RecoverAsync(run.RunId, default));
            roots.Register(root.Run.RunId, fixture.RootPath, policy);
        }
        if (viaStartup)
        {
            using var startup = new HarnessRecoveryStartupService(fixture.EventStore, fixture.Options,
                NullLogger<HarnessRecoveryStartupService>.Instance, runtimeRoots: roots, subagentRecovery: recovery);
            await startup.StartAsync(default);
            try
            {
                var until = DateTimeOffset.UtcNow.AddSeconds(5);
                while (DateTimeOffset.UtcNow < until)
                {
                    if (scenario == "queued")
                    {
                        try
                        {
                            var gateway = await execution.Factory.CreateAsync(fixture.RootPath, policy, run.RunId, run.SessionId.Value, default);
                            if (await new SubagentRuntimeBinder(fixture.EventStore, agents, scopes, execution.Factory)
                                    .BindAsync(run.RunId, fixture.RootPath, policy, default)) break;
                        }
                        catch (RuAgent.Protocol.AgentProtocolException) { }
                    }
                    else if (roots.TryGetRoot(run.RunId, out _)
                        || (await fixture.EventStore.GetRunAsync(run.RunId, default))!.State.IsTerminal()) break;
                    await Task.Delay(20);
                }
            }
            finally { await startup.StopAsync(default); }
        }
        else Assert.True(await recovery.RecoverAsync(run.RunId, default));
        var after = (await fixture.EventStore.GetRunAsync(run.RunId, default))!;
        if (scenario is "missing_pin" or "missing_scope" or "missing_owner")
        {
            Assert.Equal(HarnessRunState.Failed, after.State);
            Assert.Equal(scenario == "missing_pin" ? "subagent_model_pin_missing"
                : scenario == "missing_owner" ? "subagent_owner_missing" : "subagent_scope_missing", after.TerminalErrorCode);
            Assert.False(roots.TryGetRoot(run.RunId, out _));
            return;
        }
        Assert.Equal(scenario == "ambiguous" ? HarnessRunState.ReconciliationRequired
            : scenario == "running" ? HarnessRunState.Running : HarnessRunState.Created, after.State);
        if (after.State == HarnessRunState.Created)
        {
            Assert.False(roots.TryGetRoot(run.RunId, out _));
            Assert.True(await agents.TryAdmitAsync("agent_review", now, default));
        }
        var childGateway = await execution.Factory.CreateAsync(fixture.RootPath, policy, run.RunId, run.SessionId.Value, default);
        Assert.True(await new SubagentRuntimeBinder(fixture.EventStore, agents, scopes, execution.Factory)
            .BindAsync(run.RunId, fixture.RootPath, policy, default));
        Assert.Equal("original-model", (await pins.GetAsync(run.RunId, default))!.Selection!.Model);
        Assert.Null(await new HarnessOutboxStore(fixture.Options).ClaimNextAsync(now, default));
    }
}
