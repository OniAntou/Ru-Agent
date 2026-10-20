using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence.Workspace;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class WorkspaceJournalRetentionTests
{
    private static async Task<string> AppendAsync(
        WorkspaceCoordinatorStore store,
        WorkspaceJournalProtector protector,
        WorkspaceId workspaceId,
        string changeId,
        int payloadBytes,
        DateTimeOffset createdAt,
        string status = "committed")
    {
        var encrypted = await protector.EncryptAsync(
            workspaceId, changeId, "src/" + changeId + ".cs", new byte[payloadBytes], CancellationToken.None);
        await store.AppendChangeAsync(
            workspaceId, runId: null, changeId, "src/" + changeId + ".cs",
            new string('a', 64), new string('b', 64), encrypted, status, createdAt, CancellationToken.None);
        return changeId;
    }

    private static async Task<string[]> RemainingAsync(WorkspaceCoordinatorStore store, WorkspaceId workspaceId) =>
        (await store.ListChangesAsync(workspaceId, 0, 200, CancellationToken.None))
            .Select(change => change.ChangeId)
            .ToArray();

    [Fact]
    public async Task Settled_entries_older_than_the_cutoff_are_removed_and_recent_ones_stay()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var identity = await fixture.RegisterWorkspaceAsync(71);
        var store = new WorkspaceCoordinatorStore(fixture.Options);
        await store.GetOrCreateAsync(identity.WorkspaceId, CancellationToken.None);
        var protector = new WorkspaceJournalProtector(new FakeHarnessKeyStore());
        var now = DateTimeOffset.UtcNow;
        await AppendAsync(store, protector, identity.WorkspaceId, "chg_old", 100, now.AddDays(-30));
        await AppendAsync(store, protector, identity.WorkspaceId, "chg_recent", 100, now.AddDays(-1));

        var result = await store.PruneChangeJournalAsync(now.AddDays(-14), long.MaxValue, CancellationToken.None);

        Assert.Equal(1, result.RemovedByAge);
        Assert.Equal(0, result.RemovedByBudget);
        Assert.Equal(["chg_recent"], await RemainingAsync(store, identity.WorkspaceId));
    }

    [Fact]
    public async Task A_prepared_entry_is_an_in_flight_change_and_is_never_pruned()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var identity = await fixture.RegisterWorkspaceAsync(72);
        var store = new WorkspaceCoordinatorStore(fixture.Options);
        await store.GetOrCreateAsync(identity.WorkspaceId, CancellationToken.None);
        var protector = new WorkspaceJournalProtector(new FakeHarnessKeyStore());
        var now = DateTimeOffset.UtcNow;
        await AppendAsync(store, protector, identity.WorkspaceId, "chg_prepared", 100, now.AddDays(-60), "prepared");

        var result = await store.PruneChangeJournalAsync(now.AddDays(-14), 0, CancellationToken.None);

        Assert.Equal(0, result.Total);
        Assert.Equal(["chg_prepared"], await RemainingAsync(store, identity.WorkspaceId));
    }

    [Fact]
    public async Task The_oldest_entries_go_first_when_a_workspace_exceeds_its_payload_budget()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var identity = await fixture.RegisterWorkspaceAsync(73);
        var store = new WorkspaceCoordinatorStore(fixture.Options);
        await store.GetOrCreateAsync(identity.WorkspaceId, CancellationToken.None);
        var protector = new WorkspaceJournalProtector(new FakeHarnessKeyStore());
        var now = DateTimeOffset.UtcNow;
        foreach (var name in new[] { "chg_1", "chg_2", "chg_3", "chg_4" })
        {
            await AppendAsync(store, protector, identity.WorkspaceId, name, 1000, now);
        }

        var result = await store.PruneChangeJournalAsync(now.AddDays(-14), 2500, CancellationToken.None);

        Assert.Equal(0, result.RemovedByAge);
        Assert.Equal(2, result.RemovedByBudget);
        Assert.Equal(["chg_3", "chg_4"], await RemainingAsync(store, identity.WorkspaceId));
    }

    [Fact]
    public async Task The_budget_applies_to_each_workspace_separately()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var first = await fixture.RegisterWorkspaceAsync(74);
        var second = await fixture.RegisterWorkspaceAsync(75);
        var store = new WorkspaceCoordinatorStore(fixture.Options);
        await store.GetOrCreateAsync(first.WorkspaceId, CancellationToken.None);
        await store.GetOrCreateAsync(second.WorkspaceId, CancellationToken.None);
        var protector = new WorkspaceJournalProtector(new FakeHarnessKeyStore());
        var now = DateTimeOffset.UtcNow;
        foreach (var name in new[] { "chg_a1", "chg_a2", "chg_a3" })
        {
            await AppendAsync(store, protector, first.WorkspaceId, name, 1000, now);
        }

        await AppendAsync(store, protector, second.WorkspaceId, "chg_b1", 1000, now);

        await store.PruneChangeJournalAsync(now.AddDays(-14), 2500, CancellationToken.None);

        Assert.Equal(["chg_a2", "chg_a3"], await RemainingAsync(store, first.WorkspaceId));
        Assert.Equal(["chg_b1"], await RemainingAsync(store, second.WorkspaceId));
    }

    [Fact]
    public async Task A_negative_budget_is_rejected()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var store = new WorkspaceCoordinatorStore(fixture.Options);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.PruneChangeJournalAsync(DateTimeOffset.UtcNow, -1, CancellationToken.None));
    }
}
