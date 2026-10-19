using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using RuAgent.Protocol.Harness;
using RuAgent.Harness.Persistence.Policy;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class SubagentStoreTests
{
    [Fact]
    public async Task Family_seed_counts_entire_prior_root_batch_and_batch_reservations_are_atomic()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var root = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(root, default);
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE agent_runs SET model_steps_in_window=10,tool_calls_in_window=254";
            await command.ExecuteNonQueryAsync();
        }
        await new SqliteSubagentStore(fixture.Options, fixture.PayloadStore).CreateAsync(
            new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Review, fixture.NewRun(2, 2, 1)), default);
        var budgets = new SqliteSubagentBudgetStore(fixture.Options);
        Assert.False(await budgets.ReserveAmountAsync(root.Run.RunId, 1, "tools_failed", SubagentBudgetKind.ToolCall, 3, 0, 0, default));
        Assert.True(await budgets.ReserveAmountAsync(root.Run.RunId, 1, "tools_one", SubagentBudgetKind.ToolCall, 2, 0, 0, default));
        Assert.True(await budgets.ReserveAmountAsync(root.Run.RunId, 1, "tools_one", SubagentBudgetKind.ToolCall, 2, 0, 0, default));
        await Assert.ThrowsAsync<HarnessStoreException>(() => budgets.ReserveAmountAsync(
            root.Run.RunId, 1, "tools_one", SubagentBudgetKind.ToolCall, 1, 0, 0, default));
        Assert.False(await budgets.ReserveAmountAsync(root.Run.RunId, 1, "tools_two", SubagentBudgetKind.ToolCall, 1, 0, 0, default));
    }

    [Fact]
    public async Task Active_families_are_paged_without_starving_later_roots()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var store = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        var events = fixture.CreateEventStore();
        for (var index = 0; index < 129; index++)
        {
            var number = 10 + index * 2;
            var root = fixture.NewRun(number, number, 1);
            await events.TryCreateRunAsync(root, default);
            await store.CreateAsync(new("agent_" + index, root.Run.RunId, "spawn_" + index, SubagentRole.Review,
                fixture.NewRun(number + 1, number + 1, 1)), default);
        }
        var first = await store.ListActiveFamiliesAsync(null, default);
        Assert.Equal(128, first.Count);
        var second = await store.ListActiveFamiliesAsync(first[^1].Value, default);
        Assert.Single(second);
        Assert.Empty(first.Intersect(second));
        Assert.Empty(await store.ListActiveFamiliesAsync(second[^1].Value, default));
    }

    [Theory]
    [InlineData(AgentPermissionPreset.Plan)]
    [InlineData(AgentPermissionPreset.Standard)]
    [InlineData(AgentPermissionPreset.FullAccess)]
    public async Task Fresh_internal_session_inherits_exact_parent_preset(AgentPermissionPreset preset)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var permissions = new AgentSessionPermissionStore(fixture.Options);
        await permissions.SetInteractivelyAsync(HarnessStoreTestFixture.Session(1), preset, 0, HarnessStoreTestFixture.Timestamp, default);
        var root = fixture.NewRun(1, 1, 1, preset: preset);
        await fixture.CreateEventStore().TryCreateRunAsync(root, default);
        var agents = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        await agents.CreateAsync(new("agent_review", root.Run.RunId, "spawn_1", SubagentRole.Review,
            fixture.NewRun(2, 2, 1, preset: preset)), default);
        var selected = await permissions.EnsureForRunAsync(HarnessStoreTestFixture.Session(2), preset, 1, HarnessStoreTestFixture.Timestamp, default);
        Assert.Equal(SessionPermissionOutcome.Matched, selected.Outcome);
        Assert.Equal(preset, selected.Selection!.Preset);
    }

    [Fact]
    public async Task Child_cannot_adopt_an_existing_conversation_session()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var root = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(root, default);
        await new AgentSessionPermissionStore(fixture.Options).SetInteractivelyAsync(HarnessStoreTestFixture.Session(2),
            AgentPermissionPreset.Standard, 0, HarnessStoreTestFixture.Timestamp, default);
        await Assert.ThrowsAsync<HarnessStoreException>(() => new SqliteSubagentStore(fixture.Options, fixture.PayloadStore).CreateAsync(
            new("agent_review", root.Run.RunId, "spawn_1", SubagentRole.Review, fixture.NewRun(2, 2, 1)), default));
    }

    [Fact]
    public async Task Queued_message_is_encrypted_idempotent_and_cannot_outlive_cancellation()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var root = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(root, default);
        var store = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        await store.CreateAsync(new("agent_one", root.Run.RunId, "spawn_1", SubagentRole.Review, fixture.NewRun(2, 2, 1)), default);
        await store.AppendMessageAsync(root.Run.RunId, "agent_one", "message_1", "Investigate parser boundary", default);
        await store.AppendMessageAsync(root.Run.RunId, "agent_one", "message_1", "Investigate parser boundary", default);
        Assert.Single(await store.ReadPendingMessagesAsync(root.Run.RunId, "agent_one", default));
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT protected_content FROM agent_payloads";
            var protectedBytes = Assert.IsType<byte[]>(await command.ExecuteScalarAsync());
            Assert.DoesNotContain("Investigate parser boundary", System.Text.Encoding.UTF8.GetString(protectedBytes));
        }
        await store.CancelFamilyAsync(root.Run.RunId, default);
        await Assert.ThrowsAsync<HarnessStoreException>(() => store.AppendMessageAsync(root.Run.RunId, "agent_one", "message_2", "retry", default));
    }
    [Fact]
    public async Task Admission_has_one_deadline_and_observes_family_cancel()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var workspace = HarnessStoreTestFixture.Workspace(1);
        var policy = new BrokerPolicySnapshotWire(1, "standard", new(workspace.WorkspaceId.Value,
            workspace.VolumeSerialNumber, workspace.RootFileId, workspace.DisplayIdentityHash), [],
            HarnessStoreTestFixture.Timestamp.ToString("O"));
        var root = SubagentScopeStoreTests.WithPolicy(fixture.NewRun(1, 1, 1), policy);
        await fixture.CreateEventStore().TryCreateRunAsync(root, default);
        var store = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        await store.CreateAsync(new("agent_one", root.Run.RunId, "spawn_1", SubagentRole.Review,
            SubagentScopeStoreTests.WithPolicy(fixture.NewRun(2, 2, 1), policy)), default);
        var now = HarnessStoreTestFixture.Timestamp.AddSeconds(1);
        Assert.False(await store.TryAdmitAsync("agent_one", now, default));
        await new SqliteAgentRunProviderSelectionStore(fixture.Options).PinAsync(HarnessStoreTestFixture.Run(2),
            new("Custom OpenAI", "custom_openai", "chat_completions", "https://example.invalid/v1/", "fixture-model", null, "{}", null), default);
        Assert.False(await store.TryAdmitAsync("agent_one", now, default));
        await fixture.PayloadStore.CaptureBaseContextAsync(HarnessStoreTestFixture.Run(2), [], now, default);
        Assert.False(await store.TryAdmitAsync("agent_one", now, default));
        await new SqliteSubagentScopeStore(fixture.Options, new WorkspaceJournalProtector(new FakeHarnessKeyStore())).SaveAsync(
            root.Run.RunId, HarnessStoreTestFixture.Run(2), new(fixture.RootPath, fixture.RootPath,
                new(root.Run.PolicySnapshotVersion, root.Run.PolicySnapshotSha256),
                BrokerPolicyContracts.WriteSnapshot(policy).ToJsonString(), null,
                BrokerPolicyContracts.WriteSnapshot(policy).ToJsonString()), default);
        Assert.True(await store.TryAdmitAsync("agent_one", now, default));
        var attempt = await store.GetLatestAttemptAsync(root.Run.RunId, "agent_one", default);
        Assert.Equal(HarnessRunState.Running, attempt!.Run.State);
        Assert.Equal(now.AddMinutes(15), attempt!.DeadlineAt);
        Assert.False(await store.TryAdmitAsync("agent_one", now.AddMinutes(1), default));
        Assert.Equal(attempt.DeadlineAt, (await store.GetLatestAttemptAsync(root.Run.RunId, "agent_one", default))!.DeadlineAt);
        await store.CancelFamilyAsync(root.Run.RunId, default);
        Assert.False(await store.TryAdmitAsync("agent_one", now.AddMinutes(1), default));
    }
    [Fact]
    public async Task Child_creation_is_atomic_idempotent_and_owned_by_root()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var events = fixture.CreateEventStore();
        var root = fixture.NewRun(1, 1, 1);
        await events.TryCreateRunAsync(root, default);
        var store = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        var command = new SubagentCreateCommand("agent_one", root.Run.RunId, "spawn_1", SubagentRole.Review, fixture.NewRun(2, 2, 1));
        var first = await store.CreateAsync(command, default);
        var replay = await store.CreateAsync(command, default);
        Assert.Equal(first, replay);
        Assert.Single(await store.ListAsync(root.Run.RunId, default));
        Assert.NotNull(await events.GetRunAsync(command.ChildRun.Run.RunId, default));
        Assert.True(await store.IsInternalSessionAsync(command.ChildRun.Run.SessionId, default));
        // A crash before explicit child pinning must never select current global defaults.
        Assert.Null(await new SqliteAgentRunProviderSelectionStore(fixture.Options).GetAsync(command.ChildRun.Run.RunId, default));
        await Assert.ThrowsAsync<HarnessStoreException>(() => store.CreateAsync(command with { Role = SubagentRole.Coding }, default));
    }

    [Fact]
    public async Task Canceled_family_rejects_new_children_and_messages()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var root = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(root, default);
        var store = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        var request = new SubagentCreateCommand("agent_one", root.Run.RunId, "spawn_1", SubagentRole.Review, fixture.NewRun(2, 2, 1));
        await store.CreateAsync(request, default);
        await store.CancelFamilyAsync(root.Run.RunId, default);
        await Assert.ThrowsAsync<HarnessStoreException>(() => store.CreateAsync(request with
        { AgentId = "agent_two", InvocationId = "spawn_2", ChildRun = fixture.NewRun(3, 3, 1) }, default));
        Assert.True((await store.ListAsync(root.Run.RunId, default)).Single().Canceled);
    }

    [Fact]
    public async Task Budget_reservation_is_shared_idempotent_and_counts_prior_root_usage()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var root = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(root, default);
        var budgets = new SqliteSubagentBudgetStore(fixture.Options);
        Assert.True(await budgets.ReserveAsync(root.Run.RunId, 1, "step1", SubagentBudgetKind.ModelStep, 127, 0, default));
        Assert.True(await budgets.ReserveAsync(root.Run.RunId, 1, "step1", SubagentBudgetKind.ModelStep, 127, 0, default));
        Assert.False(await budgets.ReserveAsync(root.Run.RunId, 1, "step2", SubagentBudgetKind.ModelStep, 127, 0, default));
        Assert.True(await budgets.ReserveAsync(root.Run.RunId, 1, "tool1", SubagentBudgetKind.ToolCall, 127, 0, default));
    }
}
