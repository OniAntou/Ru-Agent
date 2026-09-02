using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Workspace;
using System.Text;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class WorkspaceCoordinatorStoreTests
{
    [Fact]
    public async Task Get_or_create_is_idempotent_and_allocates_monotonic_sequences()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var identity = await fixture.RegisterWorkspaceAsync(41);
        var store = new WorkspaceCoordinatorStore(fixture.Options);

        var first = await store.GetOrCreateAsync(identity.WorkspaceId, CancellationToken.None);
        var second = await store.GetOrCreateAsync(identity.WorkspaceId, CancellationToken.None);
        var firstSequence = await store.AllocateCommitSequenceAsync(identity.WorkspaceId, CancellationToken.None);
        var secondSequence = await store.AllocateCommitSequenceAsync(identity.WorkspaceId, CancellationToken.None);

        Assert.Equal("exclusive", first.Mode);
        Assert.Equal(first, second);
        Assert.Equal(1, firstSequence);
        Assert.Equal(2, secondSequence);
    }

    [Fact]
    public async Task Update_policy_is_optimistic_and_persists_shared_mode()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var identity = await fixture.RegisterWorkspaceAsync(42);
        var store = new WorkspaceCoordinatorStore(fixture.Options);
        var current = await store.GetOrCreateAsync(identity.WorkspaceId, CancellationToken.None);

        var updated = await store.UpdatePolicyAsync(
            identity.WorkspaceId,
            current.Version,
            "shared",
            4,
            45,
            mergeTextConflicts: true,
            externalWriterDetection: true,
            CancellationToken.None);

        Assert.Equal("shared", updated.Mode);
        Assert.Equal(4, updated.MaxConcurrentRuns);
        Assert.Equal(45, updated.LeaseTtlSeconds);
        Assert.Equal(current.Version + 1, updated.Version);
    }

    [Fact]
    public async Task Claim_paths_is_idempotent_for_the_same_run()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(43);
        var run = fixture.NewRun(43, 43, 43);
        var create = await fixture.CreateEventStore().TryCreateRunAsync(run, CancellationToken.None);
        Assert.Equal(CreateRunOutcome.Created, create.Outcome);
        var store = new WorkspaceCoordinatorStore(fixture.Options);
        await store.GetOrCreateAsync(run.Run.WorkspaceId, CancellationToken.None);

        var first = await store.TryClaimPathsAsync(
            run.Run.WorkspaceId,
            run.Run.RunId,
            [new WorkspacePathClaim("src/app.cs", WorkspacePathKind.Text)],
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(1),
            CancellationToken.None);
        var replay = await store.TryClaimPathsAsync(
            run.Run.WorkspaceId,
            run.Run.RunId,
            [new WorkspacePathClaim("src/app.cs", WorkspacePathKind.Text)],
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(1),
            CancellationToken.None);

        Assert.True(first.Accepted);
        Assert.True(replay.Accepted);
        Assert.Single(replay.Claims);
    }

    [Fact]
    public async Task Shared_mode_allows_two_runs_in_one_workspace_when_sessions_differ()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(44);
        var eventStore = fixture.CreateEventStore();
        var coordinator = new WorkspaceCoordinatorStore(fixture.Options);
        var workspaceId = fixture.NewRun(44, 44, 44).Run.WorkspaceId;
        var current = await coordinator.GetOrCreateAsync(workspaceId, CancellationToken.None);
        await coordinator.UpdatePolicyAsync(workspaceId, current.Version, "shared", 2, 30, true, true, CancellationToken.None);

        var first = fixture.NewRun(44, 44, 44);
        var second = fixture.NewRun(45, 45, 44);
        Assert.Equal(CreateRunOutcome.Created, (await eventStore.TryCreateRunAsync(first, CancellationToken.None)).Outcome);
        Assert.Equal(CreateRunOutcome.Created, (await eventStore.TryCreateRunAsync(second, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Parallel_sequence_allocations_are_unique_and_contiguous()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var identity = await fixture.RegisterWorkspaceAsync(46);
        var store = new WorkspaceCoordinatorStore(fixture.Options);
        await store.GetOrCreateAsync(identity.WorkspaceId, CancellationToken.None);

        var tasks = Enumerable.Range(0, 4)
            .Select(_ => store.AllocateCommitSequenceAsync(identity.WorkspaceId, CancellationToken.None))
            .ToArray();
        var sequences = await Task.WhenAll(tasks);

        Assert.Equal([1L, 2L, 3L, 4L], sequences.Order().ToArray());
    }

    [Fact]
    public async Task External_change_is_durable_and_listed_in_sequence_order()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var identity = await fixture.RegisterWorkspaceAsync(47);
        var store = new WorkspaceCoordinatorStore(fixture.Options);
        await store.GetOrCreateAsync(identity.WorkspaceId, CancellationToken.None);
        var protector = new WorkspaceJournalProtector(new FakeHarnessKeyStore());
        var encrypted = await protector.EncryptAsync(
            identity.WorkspaceId,
            "chg_47",
            "src/app.cs",
            Encoding.UTF8.GetBytes("external"),
            CancellationToken.None);

        var change = await store.AppendChangeAsync(
            identity.WorkspaceId,
            runId: null,
            "chg_47",
            "src/app.cs",
            new string('a', 64),
            new string('b', 64),
            encrypted,
            "committed",
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        var listed = await store.ListChangesAsync(identity.WorkspaceId, 0, 10, CancellationToken.None);
        var replay = await store.AppendChangeAsync(
            identity.WorkspaceId,
            runId: null,
            "chg_47",
            "src/app.cs",
            new string('a', 64),
            new string('b', 64),
            encrypted,
            "committed",
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Equal("chg_47", change.ChangeId);
        Assert.Equal(change.CommitSequence, replay.CommitSequence);
        var item = Assert.Single(listed);
        Assert.Equal(1, item.CommitSequence);
        Assert.Equal("committed", item.Status);
        Assert.Null(item.RunId);
    }

    [Fact]
    public async Task Membership_lease_expires_and_release_is_idempotent()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(48);
        var store = new WorkspaceCoordinatorStore(fixture.Options);
        var workspaceId = new WorkspaceId("ws_00000000000000000000000000000030");
        var policy = await store.GetOrCreateAsync(workspaceId, CancellationToken.None);
        await store.UpdatePolicyAsync(workspaceId, policy.Version, "shared", 1, 30, true, true, CancellationToken.None);
        var first = fixture.NewRun(48, 48, 48);
        var second = fixture.NewRun(49, 49, 48);
        var eventStore = fixture.CreateEventStore();
        Assert.Equal(CreateRunOutcome.Created, (await eventStore.TryCreateRunAsync(first, CancellationToken.None)).Outcome);
        Assert.Equal(CreateRunOutcome.Created, (await eventStore.TryCreateRunAsync(second, CancellationToken.None)).Outcome);
        var now = DateTimeOffset.UtcNow;

        var membership = await store.AcquireRunMembershipAsync(
            first.Run.WorkspaceId,
            first.Run.RunId,
            "ru_peer",
            now,
            TimeSpan.FromSeconds(10),
            CancellationToken.None);
        Assert.Equal("active", membership.State);
        await Assert.ThrowsAsync<HarnessStoreException>(() => store.AcquireRunMembershipAsync(
            second.Run.WorkspaceId,
            second.Run.RunId,
            "ru_peer",
            now,
            TimeSpan.FromSeconds(10),
            CancellationToken.None));

        await store.ReleaseRunMembershipAsync(first.Run.WorkspaceId, first.Run.RunId, now, CancellationToken.None);
        await store.ReleaseRunMembershipAsync(first.Run.WorkspaceId, first.Run.RunId, now, CancellationToken.None);
        var released = await store.AcquireRunMembershipAsync(
            second.Run.WorkspaceId,
            second.Run.RunId,
            "ru_peer",
            now,
            TimeSpan.FromSeconds(10),
            CancellationToken.None);
        Assert.Equal(second.Run.RunId.Value, released.RunId);
    }

    [Fact]
    public async Task Reconcile_expired_memberships_and_claims_is_idempotent()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(49);
        var run = fixture.NewRun(50, 50, 49);
        Assert.Equal(CreateRunOutcome.Created, (await fixture.CreateEventStore().TryCreateRunAsync(run, CancellationToken.None)).Outcome);
        var store = new WorkspaceCoordinatorStore(fixture.Options);
        await store.GetOrCreateAsync(run.Run.WorkspaceId, CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        await store.AcquireRunMembershipAsync(run.Run.WorkspaceId, run.Run.RunId, "ru_peer", now, TimeSpan.FromSeconds(5), CancellationToken.None);
        var claim = await store.TryClaimPathsAsync(
            run.Run.WorkspaceId,
            run.Run.RunId,
            [new WorkspacePathClaim("src/recovery.cs", WorkspacePathKind.Text)],
            now,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        Assert.True(claim.Accepted);

        var first = await store.ReconcileExpiredAsync(now.AddMinutes(1), CancellationToken.None);
        var second = await store.ReconcileExpiredAsync(now.AddMinutes(1), CancellationToken.None);

        Assert.Equal(1, first.ExpiredMemberships);
        Assert.Equal(1, first.ExpiredClaims);
        Assert.Equal(0, second.ExpiredMemberships);
        Assert.Equal(0, second.ExpiredClaims);
    }

}
