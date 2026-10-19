using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuBackend.Harness;
using RuBackend.Harness.Subagents;
using RuBackend.Harness.Workspace;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class SubagentLaunchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Followup_reuses_workspace_model_and_closed_history_and_repairs_preparation(bool incompleteHistory)
    {
        await using var storage = await BackendHarnessTestFixture.CreateAsync();
        var root = await storage.SeedRunWithEncryptedWorkspaceAsync(new(HarnessRunState.Running),
            "run_" + new string('a', 32), new string('a', 32), storage.RootPath);
        var identity = await new WindowsWorkspaceIdentityResolver().ResolveAsync(storage.RootPath, default);
        var policy = PolicySnapshotCanonicalizer.Create(1, root.Run.Preset, identity, [], BackendHarnessTestFixture.Timestamp);
        var roots = new AgentRuntimeRootRegistry();
        roots.Register(root.Run.RunId, storage.RootPath, policy);
        var agents = new SqliteSubagentStore(storage.Options, storage.PayloadStore);
        var scopes = new SqliteSubagentScopeStore(storage.Options, new WorkspaceJournalProtector(storage.KeyStore));
        var pins = new SqliteAgentRunProviderSelectionStore(storage.Options);
        var models = new Selection();
        var workspaces = new Workspaces();
        var clock = new Clock(DateTimeOffset.UtcNow);
        using var spawn = new SubagentRunFactory(storage.EventStore, agents, pins, storage.PayloadStore,
            scopes, roots, models, workspaces, clock);
        var agent = await spawn.CreateAsync(root.Run.RunId, "spawn_followup", new("review", "Inspect parser", "selected context"), default);
        var first = (await agents.GetLatestAttemptAsync(root.Run.RunId, agent.AgentId, default))!;
        var firstScope = await scopes.GetAsync(first.Run.RunId, default);
        if (incompleteHistory)
        {
            using var arguments = System.Text.Json.JsonDocument.Parse("{\"path\":\"x.txt\"}");
            var bytes = AssistantStepJsonCodec.Serialize(new AssistantStep("read", [new("provider_read", "read", "1.0.0", arguments.RootElement)],
                AssistantFinishReason.ToolCalls, 16));
            var before = first.Run;
            var after = new AgentRun(before.RunId, before.SessionId, before.WorkspaceId, before.Status, before.Version + 1,
                before.LastSequence + 1, before.BudgetWindow, before.ModelStepsInWindow, before.ToolCallsInWindow,
                before.Preset, before.PolicySnapshotVersion, before.PolicySnapshotSha256, before.CreatedAt, before.UpdatedAt);
            var payload = new PayloadId("payload_" + new string('f', 32));
            await storage.EventStore.CommitAsync(new(before.RunId, before.Version, after,
                [new(new("evt_" + new string('d', 32)), HarnessEventKind.ModelStepCompleted, modelStepId: new("step_" + new string('d', 32)), payloadId: payload)],
                [new(payload, HarnessPayloadKind.AssistantStep, HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                    HarnessPayloadEncoding.Json, bytes.Length, bytes)], []), default);
            first = first with { Run = after };
        }
        await storage.EventStore.CommitAsync(SubagentAdmission.CreateTerminalCommit(first.Run,
            new("evt_" + new string('f', 32)), false, "fixture_failure", clock.UtcNow), default);
        await agents.AppendMessageAsync(root.Run.RunId, agent.AgentId, "message_one", "Inspect empty input", default);
        using var continuations = new SubagentContinuationFactory(storage.EventStore, agents, pins, storage.PayloadStore, scopes, clock, storage.Options);
        if (incompleteHistory)
        {
            using var failedTicker = new SubagentContinuationCoordinator(agents, continuations);
            await failedTicker.TickAsync(default);
            Assert.Equal("subagent_continuation_history_incomplete",
                Assert.Single(await agents.ReadPendingMessagesAsync(root.Run.RunId, agent.AgentId, default)).ContinuationErrorCode);
            Assert.Equal(first.Run.RunId, (await agents.GetLatestAttemptAsync(root.Run.RunId, agent.AgentId, default))!.Run.RunId);
            await failedTicker.TickAsync(default);
            Assert.Empty(await agents.ListContinuationCandidatesAsync(null, default));
            return;
        }
        var next = (await continuations.ContinueAsync(root.Run.RunId, agent.AgentId, default))!;
        Assert.Equal(2, next.AttemptNumber);
        Assert.Equal(agent.InternalSessionId, next.Run.SessionId);
        Assert.Equal(firstScope, await scopes.GetAsync(next.Run.RunId, default));
        Assert.Equal(models.Pin, (await pins.GetAsync(next.Run.RunId, default))!.Selection);
        Assert.Equal(1, models.Calls);
        Assert.Equal(1, workspaces.Calls);
        await using (var connection = await storage.Options.OpenConnectionAsync(default))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM agent_subagent_scopes WHERE run_id=$run; UPDATE agent_runs SET base_context_captured_at_utc=NULL,base_context_entry_count=0 WHERE run_id=$run";
            command.Parameters.AddWithValue("$run", next.Run.RunId.Value);
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(next.Run.RunId, (await continuations.ContinueAsync(root.Run.RunId, agent.AgentId, default))!.Run.RunId);
        Assert.Equal(firstScope, await scopes.GetAsync(next.Run.RunId, default));
        var context = await new RuAgent.Harness.Persistence.Context.SqliteModelContextSource(storage.Options, storage.PayloadStore)
            .LoadAsync(next.Run.RunId, default);
        Assert.Contains(context.SessionBase, entry => entry.Content?.Contains("Inspect parser") == true);
        Assert.True(await agents.TryAdmitAsync(agent.AgentId, clock.UtcNow, default));
        var active = (await storage.EventStore.GetRunAsync(next.Run.RunId, default))!;
        await agents.AppendMessageAsync(root.Run.RunId, agent.AgentId, "message_two", "Arrived during the final step", default);
        await storage.EventStore.CommitAsync(SubagentAdmission.CreateTerminalCommit(active,
            new("evt_" + new string('e', 32)), false, "fixture_failure", clock.UtcNow), default);
        using var ticker = new SubagentContinuationCoordinator(agents, continuations);
        await ticker.TickAsync(default);
        var third = (await agents.GetLatestAttemptAsync(root.Run.RunId, agent.AgentId, default))!;
        Assert.Equal(3, third.AttemptNumber);
        Assert.Equal(HarnessRunState.Created, third.Run.State);
        await ticker.TickAsync(default);
        Assert.Equal(third.Run.RunId, (await agents.GetLatestAttemptAsync(root.Run.RunId, agent.AgentId, default))!.Run.RunId);
        Assert.Equal(1, models.Calls);
        Assert.Equal(1, workspaces.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Spawn_persists_fresh_child_once_without_registering_unadmitted_run(bool cancelFamily, bool corruptScope)
    {
        await using var storage = await BackendHarnessTestFixture.CreateAsync();
        var root = await storage.SeedRunWithEncryptedWorkspaceAsync(new(HarnessRunState.Running),
            "run_" + new string('a', 32), new string('a', 32), storage.RootPath);
        var identity = await new WindowsWorkspaceIdentityResolver().ResolveAsync(storage.RootPath, default);
        var policy = PolicySnapshotCanonicalizer.Create(1, root.Run.Preset, identity, [], BackendHarnessTestFixture.Timestamp);
        var roots = new AgentRuntimeRootRegistry();
        roots.Register(root.Run.RunId, storage.RootPath, policy);
        var agents = new SqliteSubagentStore(storage.Options, storage.PayloadStore);
        var scopes = new SqliteSubagentScopeStore(storage.Options, new WorkspaceJournalProtector(storage.KeyStore));
        var pins = new SqliteAgentRunProviderSelectionStore(storage.Options);
        var models = new Selection();
        var workspaces = new Workspaces();
        var clock = new Clock(DateTimeOffset.UtcNow);
        using var factory = new SubagentRunFactory(storage.EventStore, agents, pins, storage.PayloadStore,
            scopes, roots, models, workspaces, clock);
        var request = new SubagentSpawnRequest("review", "Inspect parser edge cases", "selected context only");
        var first = await factory.CreateAsync(root.Run.RunId, "spawn_one", request, default);
        var replay = await factory.CreateAsync(root.Run.RunId, "spawn_one", request, default);
        Assert.Equal(first.AgentId, replay.AgentId);
        Assert.Equal(1, models.Calls);
        Assert.Equal(1, workspaces.Calls);
        Assert.NotEqual(root.Run.SessionId, first.InternalSessionId);
        var attempt = await agents.GetLatestAttemptAsync(root.Run.RunId, first.AgentId, default);
        Assert.Equal(HarnessRunState.Created, attempt!.Run.State);
        Assert.False(roots.TryGetRoot(attempt.Run.RunId, out _));
        Assert.Equal(models.Pin, (await pins.GetAsync(attempt.Run.RunId, default))!.Selection);
        Assert.NotNull(await scopes.GetAsync(attempt.Run.RunId, default));
        await Assert.ThrowsAsync<HarnessStoreException>(() => factory.CreateAsync(root.Run.RunId, "spawn_one",
            request with { Task = "Changed task" }, default));
        for (var index = 2; index <= 4; index++)
            await factory.CreateAsync(root.Run.RunId, "spawn_" + index, request, default);
        if (corruptScope)
        {
            await using var connection = await storage.Options.OpenConnectionAsync(default);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE agent_subagent_scopes SET ciphertext=zeroblob(length(ciphertext)) WHERE run_id=$run";
            command.Parameters.AddWithValue("$run", attempt.Run.RunId.Value);
            await command.ExecuteNonQueryAsync();
        }
        using var scheduler = new SubagentScheduler(storage.EventStore, agents, scopes, roots, clock);
        var cleanup = new Cleanup(roots);
        await scheduler.TickAsync(cleanup, default);
        Assert.Equal(4, roots.Snapshot().Count); // parent plus three admitted children
        var states = new List<HarnessRunState>();
        foreach (var agent in await agents.ListAsync(root.Run.RunId, default))
            states.Add((await agents.GetLatestAttemptAsync(root.Run.RunId, agent.AgentId, default))!.Run.State);
        Assert.Equal(3, states.Count(state => state == HarnessRunState.Running));
        Assert.Equal(corruptScope ? 0 : 1, states.Count(state => state == HarnessRunState.Created));
        if (cancelFamily) await agents.CancelFamilyAsync(root.Run.RunId, default);
        else clock.UtcNow = clock.UtcNow.AddMinutes(16);
        await scheduler.TickAsync(cleanup, default);
        Assert.Equal(4, cleanup.Calls);
        foreach (var agent in await agents.ListAsync(root.Run.RunId, default))
        {
            var terminated = (await agents.GetLatestAttemptAsync(root.Run.RunId, agent.AgentId, default))!.Run;
            Assert.Equal(cancelFamily ? HarnessRunState.Cancelled : HarnessRunState.Failed, terminated.State);
            Assert.Contains(terminated.TerminalErrorCode, cancelFamily ? ["subagent_parent_canceled"]
                : new[] { "subagent_admission_timeout", "subagent_attempt_timeout", "subagent_scope_invalid" });
        }
        Assert.Null(await new HarnessOutboxStore(storage.Options).ClaimNextAsync(DateTimeOffset.UtcNow, default));
    }

    private sealed class Selection : ISubagentModelSelectionProvider
    {
        public int Calls;
        public AgentRunProviderSelection Pin { get; } = new("Custom OpenAI", "custom", "chat_completions",
            "https://example.invalid/v1/", "fixture-model", null, "{}", null, "fixture-model", "native");
        public Task<AgentRunProviderSelection> ResolveAsync(RunId rootRunId, SubagentRole role, CancellationToken ct, string? model = null, string? providerId = null)
        { Calls++; return Task.FromResult(Pin); }
    }

    private sealed class Workspaces : ISubagentWorkspaceFactory
    {
        public int Calls;
        public Task<PreparedSubagentWorkspace> CreateAsync(AgentRun parent, string root, PolicySnapshot policy,
            string agentId, SubagentRole role, CancellationToken ct)
        { Calls++; return Task.FromResult(new PreparedSubagentWorkspace(root, policy, null)); }
    }

    private sealed class Cleanup(AgentRuntimeRootRegistry roots) : IAgentRuntimeRunCleanup
    {
        public int Calls;
        public Task CancelAndRemoveRunAsync(RunId runId, CancellationToken ct)
        { Calls++; roots.Remove(runId); return Task.CompletedTask; }
    }

    private sealed class Clock(DateTimeOffset now) : ITimeProvider
    { public DateTimeOffset UtcNow { get; set; } = now; }
}
