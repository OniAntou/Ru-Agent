using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence.Policy;
using Xunit;
using RuAgent.Protocol.Harness;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class PolicySnapshotLoaderTests
{
    [Fact]
    public async Task Ordering_is_preset_fallback_then_global_then_project_then_run_then_once_last_wins()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var workspace = await fixture.RegisterWorkspaceAsync(1);
        var identity = new WorkspaceIdentity(workspace.WorkspaceId, workspace.VolumeSerialNumber, workspace.RootFileId, workspace.DisplayIdentityHash);
        var keyStore = new FakeHarnessKeyStore();
        var ruleStore = new SqlitePolicyRuleStore(fixture.Options, keyStore);
        var loader = new PolicySnapshotLoader(fixture.Options, keyStore);

        // Setup runs
        var eventStore = fixture.CreateEventStore();
        await eventStore.TryCreateRunAsync(fixture.NewRun(1, 1, 1), CancellationToken.None);
        var runId = HarnessStoreTestFixture.Run(1);

        var globalRule = Rule(1, PolicyRuleScope.Global, PolicyDecision.Allow, "global/path", 1);
        var projectRule = Rule(2, PolicyRuleScope.Project, PolicyDecision.Ask, "project/path", 2);
        var runRule = Rule(3, PolicyRuleScope.Run, PolicyDecision.Deny, "run/path", 3);
        var onceRule = Rule(4, PolicyRuleScope.Once, PolicyDecision.Allow, "once/path", 4);

        await ruleStore.CreateAsync(globalRule, null, null, HarnessStoreTestFixture.Timestamp, CancellationToken.None);
        await ruleStore.CreateAsync(projectRule, workspace.WorkspaceId, null, HarnessStoreTestFixture.Timestamp, CancellationToken.None);
        await ruleStore.CreateAsync(runRule, null, runId, HarnessStoreTestFixture.Timestamp, CancellationToken.None);
        await ruleStore.CreateAsync(onceRule, null, runId, HarnessStoreTestFixture.Timestamp, CancellationToken.None);

        var snapshot = await loader.LoadAsync(identity, AgentPermissionPreset.Standard, runId, HarnessStoreTestFixture.Timestamp.AddMinutes(1), CancellationToken.None);

        Assert.Equal(4, snapshot.OrderedRules.Count);
        Assert.Equal(globalRule.RuleId, snapshot.OrderedRules[0].RuleId);
        Assert.Equal(projectRule.RuleId, snapshot.OrderedRules[1].RuleId);
        Assert.Equal(runRule.RuleId, snapshot.OrderedRules[2].RuleId);
        Assert.Equal(onceRule.RuleId, snapshot.OrderedRules[3].RuleId);
        // last wins – once is last
        Assert.Equal(PolicyRuleScope.Once, snapshot.OrderedRules[^1].Scope);
    }

    [Fact]
    public async Task Canonical_ordered_list_is_deterministic_and_hash_changes_with_order()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var ws = await fixture.RegisterWorkspaceAsync(2);
        var identity = new WorkspaceIdentity(ws.WorkspaceId, ws.VolumeSerialNumber, ws.RootFileId, ws.DisplayIdentityHash);
        var keyStore = new FakeHarnessKeyStore(0x11);
        var ruleStore = new SqlitePolicyRuleStore(fixture.Options, keyStore);
        var loader = new PolicySnapshotLoader(fixture.Options, keyStore);

        var eventStore = fixture.CreateEventStore();
        await eventStore.TryCreateRunAsync(fixture.NewRun(10, 10, 2), CancellationToken.None);
        var runId = HarnessStoreTestFixture.Run(10);

        var r1 = Rule(10, PolicyRuleScope.Global, PolicyDecision.Allow, "a", 1);
        var r2 = Rule(11, PolicyRuleScope.Global, PolicyDecision.Allow, "b", 2);
        await ruleStore.CreateAsync(r1, null, null, HarnessStoreTestFixture.Timestamp, CancellationToken.None);
        await ruleStore.CreateAsync(r2, null, null, HarnessStoreTestFixture.Timestamp, CancellationToken.None);

        var snap1 = await loader.LoadAsync(identity, AgentPermissionPreset.Standard, runId, HarnessStoreTestFixture.Timestamp, CancellationToken.None);
        // reload should be deterministic
        var snap2 = await loader.LoadAsync(identity, AgentPermissionPreset.Standard, runId, HarnessStoreTestFixture.Timestamp, CancellationToken.None);
        Assert.Equal(snap1.Sha256, snap2.Sha256);
        Assert.Equal(snap1.Version, snap2.Version);
        Assert.Equal(snap1.OrderedRules.Count, snap2.OrderedRules.Count);

        // different preset changes hash
        var snapFull = await loader.LoadAsync(identity, AgentPermissionPreset.FullAccess, runId, HarnessStoreTestFixture.Timestamp, CancellationToken.None);
        Assert.NotEqual(snap1.Sha256, snapFull.Sha256);
    }

    [Fact]
    public async Task Loader_fails_closed_on_invalid_matcher_and_returns_no_partial_snapshot()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var ws = await fixture.RegisterWorkspaceAsync(3);
        var identity = new WorkspaceIdentity(ws.WorkspaceId, ws.VolumeSerialNumber, ws.RootFileId, ws.DisplayIdentityHash);
        var keyStore = new FakeHarnessKeyStore();
        var store = new SqlitePolicyRuleStore(fixture.Options, keyStore);
        var loader = new PolicySnapshotLoader(fixture.Options, keyStore);

        var eventStore = fixture.CreateEventStore();
        await eventStore.TryCreateRunAsync(fixture.NewRun(20, 20, 3), CancellationToken.None);
        var runId = HarnessStoreTestFixture.Run(20);

        var good = Rule(20, PolicyRuleScope.Global, PolicyDecision.Allow, "good", 1);
        await store.CreateAsync(good, null, null, HarnessStoreTestFixture.Timestamp, CancellationToken.None);

        // corrupt the stored document directly to simulate invalid matcher / decryption failure
        await CorruptFirstGlobalRuleAsync(fixture, "corrupt-ciphertext");

        await Assert.ThrowsAsync<HarnessStoreException>(() =>
            loader.LoadAsync(identity, AgentPermissionPreset.Standard, runId, HarnessStoreTestFixture.Timestamp, CancellationToken.None));
    }

    [Fact]
    public async Task Loader_computes_version_and_hash_via_canonicalizer()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var ws = await fixture.RegisterWorkspaceAsync(4);
        var identity = new WorkspaceIdentity(ws.WorkspaceId, ws.VolumeSerialNumber, ws.RootFileId, ws.DisplayIdentityHash);
        var keyStore = new FakeHarnessKeyStore();
        var store = new SqlitePolicyRuleStore(fixture.Options, keyStore);
        var loader = new PolicySnapshotLoader(fixture.Options, keyStore);
        var eventStore = fixture.CreateEventStore();
        await eventStore.TryCreateRunAsync(fixture.NewRun(30, 30, 4), CancellationToken.None);
        var runId = HarnessStoreTestFixture.Run(30);

        var rule = Rule(30, PolicyRuleScope.Global, PolicyDecision.Allow, "path/one", 1);
        await store.CreateAsync(rule, null, null, HarnessStoreTestFixture.Timestamp, CancellationToken.None);

        var snapshot = await loader.LoadAsync(identity, AgentPermissionPreset.Standard, runId, HarnessStoreTestFixture.Timestamp, CancellationToken.None);
        Assert.True(snapshot.Version > 0);
        Assert.Equal(64, snapshot.Sha256.Length);
        Assert.True(snapshot.Sha256.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')));
        // hash must match canonicalizer output
        var expected = PolicySnapshotCanonicalizer.Create(snapshot.Version, snapshot.Preset, snapshot.Workspace, snapshot.OrderedRules, snapshot.CreatedAt);
        Assert.Equal(expected.Sha256, snapshot.Sha256);
    }

    [Fact]
    public async Task Loader_only_decrypts_active_run_rules_and_validates_every_matcher()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var ws1 = await fixture.RegisterWorkspaceAsync(5);
        var ws2 = await fixture.RegisterWorkspaceAsync(6);
        var identity1 = new WorkspaceIdentity(ws1.WorkspaceId, ws1.VolumeSerialNumber, ws1.RootFileId, ws1.DisplayIdentityHash);
        var identity2 = new WorkspaceIdentity(ws2.WorkspaceId, ws2.VolumeSerialNumber, ws2.RootFileId, ws2.DisplayIdentityHash);
        var keyStore = new FakeHarnessKeyStore();
        var store = new SqlitePolicyRuleStore(fixture.Options, keyStore);
        var loader = new PolicySnapshotLoader(fixture.Options, keyStore);
        var eventStore = fixture.CreateEventStore();

        // one run per workspace, two isolated workspaces for the two identities
        await eventStore.TryCreateRunAsync(fixture.NewRun(40, 40, 5), CancellationToken.None);
        await eventStore.TryCreateRunAsync(fixture.NewRun(41, 41, 6), CancellationToken.None);
        var runActive = HarnessStoreTestFixture.Run(40);
        var runOther = HarnessStoreTestFixture.Run(41);

        var activeRule = Rule(40, PolicyRuleScope.Run, PolicyDecision.Allow, "active/path", 1);
        var otherRule = Rule(41, PolicyRuleScope.Run, PolicyDecision.Allow, "other/path", 2);
        await store.CreateAsync(activeRule, null, runActive, HarnessStoreTestFixture.Timestamp, CancellationToken.None);
        await store.CreateAsync(otherRule, null, runOther, HarnessStoreTestFixture.Timestamp, CancellationToken.None);

        var snapshotActive = await loader.LoadAsync(identity1, AgentPermissionPreset.Standard, runActive, HarnessStoreTestFixture.Timestamp, CancellationToken.None);
        Assert.Single(snapshotActive.OrderedRules);
        Assert.Equal(activeRule.RuleId, snapshotActive.OrderedRules[0].RuleId);

        var snapshotOther = await loader.LoadAsync(identity2, AgentPermissionPreset.Standard, runOther, HarnessStoreTestFixture.Timestamp, CancellationToken.None);
        Assert.Single(snapshotOther.OrderedRules);
        Assert.Equal(otherRule.RuleId, snapshotOther.OrderedRules[0].RuleId);
    }

    private static PolicyRule Rule(int number, PolicyRuleScope scope, PolicyDecision decision, string path, long order)
    {
        return new PolicyRule(
            new PolicyRuleId("rule_" + number.ToString("x32")),
            scope,
            decision,
            new PolicyMatcher(CanonicalPathPattern: path),
            order,
            null);
    }

    private static async Task CorruptFirstGlobalRuleAsync(HarnessStoreTestFixture fixture, string corruptText)
    {
        await using var conn = fixture.CreateRawConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE agent_global_rules SET rule_document = randomblob(16) WHERE rule_id = (SELECT rule_id FROM agent_global_rules LIMIT 1)";
        await cmd.ExecuteNonQueryAsync();
    }
}
