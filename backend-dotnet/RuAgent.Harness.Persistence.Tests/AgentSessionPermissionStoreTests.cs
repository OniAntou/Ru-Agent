using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence.Policy;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class AgentSessionPermissionStoreTests
{
    [Fact]
    public async Task First_standard_run_creates_version_one_then_requires_an_exact_match()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var store = new AgentSessionPermissionStore(fixture.Options);
        var session = HarnessStoreTestFixture.Session(1);

        var created = await store.EnsureForRunAsync(
            session,
            AgentPermissionPreset.Standard,
            selectionVersion: 1,
            HarnessStoreTestFixture.Timestamp,
            CancellationToken.None);
        var matched = await store.EnsureForRunAsync(
            session,
            AgentPermissionPreset.Standard,
            selectionVersion: 1,
            HarnessStoreTestFixture.Timestamp.AddMinutes(1),
            CancellationToken.None);
        var mismatch = await store.EnsureForRunAsync(
            session,
            AgentPermissionPreset.FullAccess,
            selectionVersion: 1,
            HarnessStoreTestFixture.Timestamp.AddMinutes(2),
            CancellationToken.None);

        Assert.Equal(SessionPermissionOutcome.CreatedDefault, created.Outcome);
        Assert.Equal(1, created.Selection!.Version);
        Assert.Equal(SessionPermissionOutcome.Matched, matched.Outcome);
        Assert.Equal(SessionPermissionOutcome.PresetMismatch, mismatch.Outcome);
        Assert.Equal("preset_mismatch", mismatch.ErrorCode);
    }

    [Fact]
    public async Task Full_access_cannot_be_created_implicitly()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var store = new AgentSessionPermissionStore(fixture.Options);

        var result = await store.EnsureForRunAsync(
            HarnessStoreTestFixture.Session(1),
            AgentPermissionPreset.FullAccess,
            selectionVersion: 1,
            HarnessStoreTestFixture.Timestamp,
            CancellationToken.None);

        Assert.Equal(SessionPermissionOutcome.SelectionRequired, result.Outcome);
        Assert.Equal("preset_selection_required", result.ErrorCode);
        Assert.Null(result.Selection);
    }

    [Fact]
    public async Task Sessions_default_independently_and_deletion_removes_only_that_selection()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var store = new AgentSessionPermissionStore(fixture.Options);
        var firstSession = HarnessStoreTestFixture.Session(1);
        var secondSession = HarnessStoreTestFixture.Session(2);
        await store.EnsureForRunAsync(
            firstSession,
            AgentPermissionPreset.Standard,
            1,
            HarnessStoreTestFixture.Timestamp,
            CancellationToken.None);
        await store.EnsureForRunAsync(
            secondSession,
            AgentPermissionPreset.Standard,
            1,
            HarnessStoreTestFixture.Timestamp,
            CancellationToken.None);

        await store.DeleteSessionAsync(firstSession, CancellationToken.None);

        Assert.Null(await store.GetAsync(firstSession, CancellationToken.None));
        var second = await store.GetAsync(secondSession, CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal(AgentPermissionPreset.Standard, second.Preset);
        Assert.Equal(1, second.Version);
    }

    [Fact]
    public async Task Interactive_selection_can_explicitly_enable_full_access()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var store = new AgentSessionPermissionStore(fixture.Options);
        var session = HarnessStoreTestFixture.Session(1);

        var selected = await store.SetInteractivelyAsync(
            session,
            AgentPermissionPreset.FullAccess,
            expectedVersion: 0,
            HarnessStoreTestFixture.Timestamp,
            CancellationToken.None);
        var run = await store.EnsureForRunAsync(
            session,
            AgentPermissionPreset.FullAccess,
            selectionVersion: 1,
            HarnessStoreTestFixture.Timestamp.AddMinutes(1),
            CancellationToken.None);

        Assert.Equal(SessionPermissionOutcome.UpdatedInteractively, selected.Outcome);
        Assert.Equal(AgentPermissionPreset.FullAccess, selected.Selection!.Preset);
        Assert.Equal(SessionPermissionOutcome.Matched, run.Outcome);
    }
}
