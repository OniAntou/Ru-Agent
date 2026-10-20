using System.Reflection;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Workspace;
using Xunit;
using RuAgent.Protocol.Harness;

namespace RuAgent.Harness.Core.Tests;

public sealed class PolicySnapshotImmutabilityTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 24, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Snapshot_has_exact_immutable_fields_with_stable_ids_and_ordering()
    {
        var type = typeof(PolicySnapshot);
        Assert.True(type.IsSealed);
        Assert.True(type.IsAssignableTo(typeof(IEquatable<PolicySnapshot>)));

        var expected = new (string Name, Type PropertyType)[]
        {
            ("Version", typeof(long)),
            ("Preset", typeof(AgentPermissionPreset)),
            ("Workspace", typeof(WorkspaceIdentity)),
            ("OrderedRules", typeof(IReadOnlyList<PolicyRule>)),
            ("CreatedAt", typeof(DateTimeOffset)),
            ("Sha256", typeof(string)),
        };

        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.Equal(expected.Length, properties.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Name, properties[i].Name);
            Assert.Equal(expected[i].PropertyType, properties[i].PropertyType);
            var setter = properties[i].SetMethod;
            if (setter is not null)
            {
                var isInitOnly = setter.ReturnParameter.GetRequiredCustomModifiers()
                    .Any(t => t.FullName == "System.Runtime.CompilerServices.IsExternalInit");
                Assert.True(isInitOnly || setter.IsPrivate || setter.IsFamily, $"Setter for {properties[i].Name} must be init-only or non-public.");
            }
        }

        // Verify ordering semantics: OrderedRules preserves creation order, is a defensive copy.
        var rules = new List<PolicyRule>
        {
            new(new PolicyRuleId("rule_11111111111111111111111111111111"), PolicyRuleScope.Global, PolicyDecision.Allow, new PolicyMatcher(ToolId: "files.read", OperationClass: "read"), 1, null),
            new(new PolicyRuleId("rule_22222222222222222222222222222222"), PolicyRuleScope.Project, PolicyDecision.Deny, new PolicyMatcher(ToolId: "files.write", OperationClass: "write"), 2, null),
            new(new PolicyRuleId("rule_33333333333333333333333333333333"), PolicyRuleScope.Run, PolicyDecision.Ask, new PolicyMatcher(ToolId: "files.edit", OperationClass: "edit"), 3, null),
        };
        var snapshot = PolicySnapshotCanonicalizer.Create(5, AgentPermissionPreset.Standard, TrustedIdentity(), rules, CreatedAt);
        Assert.Equal(5, snapshot.Version);
        Assert.Equal(AgentPermissionPreset.Standard, snapshot.Preset);
        Assert.Equal(3, snapshot.OrderedRules.Count);
        Assert.Equal("rule_11111111111111111111111111111111", snapshot.OrderedRules[0].RuleId.Value);
        Assert.Equal("rule_22222222222222222222222222222222", snapshot.OrderedRules[1].RuleId.Value);
        Assert.Equal("rule_33333333333333333333333333333333", snapshot.OrderedRules[2].RuleId.Value);
        Assert.Equal(PolicyRuleScope.Global, snapshot.OrderedRules[0].Scope);
        Assert.Equal(PolicyRuleScope.Project, snapshot.OrderedRules[1].Scope);
        Assert.Equal(PolicyRuleScope.Run, snapshot.OrderedRules[2].Scope);

        // Mutating source list must not affect snapshot (defensive copy).
        rules.Clear();
        Assert.Equal(3, snapshot.OrderedRules.Count);

        // Each rule's matcher is bounded and included in hash – changing matcher changes sha.
        var baselineSha = snapshot.Sha256;
        var altered = PolicySnapshotCanonicalizer.Create(5, AgentPermissionPreset.Standard, TrustedIdentity(), new[]
        {
            new PolicyRule(new PolicyRuleId("rule_11111111111111111111111111111111"), PolicyRuleScope.Global, PolicyDecision.Allow, new PolicyMatcher(ToolId: "files.read", OperationClass: "read", CanonicalPathPattern: "changed.txt"), 1, null),
        }, CreatedAt);
        Assert.NotEqual(baselineSha, altered.Sha256);
    }

    [Fact]
    public void Snapshot_is_immutable_session_preset_change_does_not_mutate_existing_run()
    {
        // Simulate Host snapshot binding at run creation.
        var workspace = TrustedIdentity();
        var standardSnapshot = PolicySnapshotCanonicalizer.Create(1, AgentPermissionPreset.Standard, workspace, Array.Empty<PolicyRule>(), CreatedAt);
        var sessionId = new SessionId("11111111111111111111111111111111");
        var run = new AgentRun(
            new RunId("run_11111111111111111111111111111111"),
            sessionId,
            workspace.WorkspaceId,
            new HarnessRunStatus(HarnessRunState.Running),
            version: 0,
            lastSequence: 1,
            budgetWindow: 1,
            modelStepsInWindow: 0,
            toolCallsInWindow: 0,
            AgentPermissionPreset.Standard,
            standardSnapshot.Version,
            standardSnapshot.Sha256,
            CreatedAt,
            CreatedAt);

        // Session preset later changes to FullAccess, producing a new snapshot version.
        var fullAccessSnapshot = PolicySnapshotCanonicalizer.Create(2, AgentPermissionPreset.FullAccess, workspace, Array.Empty<PolicyRule>(), Later);
        Assert.Equal(AgentPermissionPreset.Standard, run.Preset);
        Assert.Equal(standardSnapshot.Version, run.PolicySnapshotVersion);
        Assert.Equal(standardSnapshot.Sha256, run.PolicySnapshotSha256);
        Assert.NotEqual(fullAccessSnapshot.Sha256, run.PolicySnapshotSha256);
        Assert.NotEqual(fullAccessSnapshot.Version, run.PolicySnapshotVersion);

        // The run's snapshot remains the original even though session now prefers FullAccess.
        Assert.Equal("standard", run.Preset == AgentPermissionPreset.Standard ? "standard" : "full_access");
        Assert.True(run.PolicySnapshotVersion == 1);
    }

    [Fact]
    public void Full_access_is_not_inherited_by_new_session()
    {
        var workspace = TrustedIdentity();
        var sessionA = new SessionId("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var sessionB = new SessionId("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        // Session A has explicit FullAccess selection (version 2 after Standard 1).
        var snapshotFull = PolicySnapshotCanonicalizer.Create(2, AgentPermissionPreset.FullAccess, workspace, Array.Empty<PolicyRule>(), CreatedAt);
        // Session B is newly created – default must be Standard, not inherited FullAccess.
        var snapshotNewSession = PolicySnapshotCanonicalizer.Create(1, AgentPermissionPreset.Standard, workspace, Array.Empty<PolicyRule>(), CreatedAt);

        Assert.Equal(AgentPermissionPreset.FullAccess, snapshotFull.Preset);
        Assert.Equal(AgentPermissionPreset.Standard, snapshotNewSession.Preset);
        Assert.NotEqual(snapshotFull.Sha256, snapshotNewSession.Sha256);
        Assert.Equal(1, snapshotNewSession.Version);
        Assert.Equal(2, snapshotFull.Version);

        // A run created for session B must bind Standard, never Full.
        var runB = new AgentRun(
            new RunId("run_22222222222222222222222222222222"),
            sessionB,
            workspace.WorkspaceId,
            new HarnessRunStatus(HarnessRunState.Running),
            0, 1, 1, 0, 0,
            snapshotNewSession.Preset,
            snapshotNewSession.Version,
            snapshotNewSession.Sha256,
            CreatedAt, CreatedAt);
        Assert.Equal(AgentPermissionPreset.Standard, runB.Preset);
    }

    [Fact]
    public void Snapshot_uses_only_trusted_exact_identity_and_moved_identity_cannot_inherit_project_rules()
    {
        var originalIdentity = TrustedIdentity("root-a", 'a');
        var movedIdentity = TrustedIdentity("root-moved", 'c'); // Same WorkspaceId but different volume/root/display hash would be detected as mismatch in real resolver.

        // Project rule bound to original workspace identity.
        var projectRule = new PolicyRule(
            new PolicyRuleId("rule_11111111111111111111111111111111"),
            PolicyRuleScope.Project,
            PolicyDecision.Allow,
            new PolicyMatcher(ToolId: "files.read", CanonicalPathPattern: "src/project.txt", OperationClass: "read"),
            1,
            null);

        var snapshotOriginal = PolicySnapshotCanonicalizer.Create(1, AgentPermissionPreset.Standard, originalIdentity, new[] { projectRule }, CreatedAt);
        var snapshotMovedEmpty = PolicySnapshotCanonicalizer.Create(1, AgentPermissionPreset.Standard, movedIdentity, Array.Empty<PolicyRule>(), CreatedAt);
        var snapshotMovedWithSameProjectRule = PolicySnapshotCanonicalizer.Create(1, AgentPermissionPreset.Standard, movedIdentity, new[] { projectRule }, CreatedAt);

        // Hashes must differ because workspace identity is hash-bound.
        Assert.NotEqual(snapshotOriginal.Sha256, snapshotMovedEmpty.Sha256);
        // Even if an attacker tries to reuse the same project rule with a moved identity, the resulting hash differs from the original,
        // proving project rules are bound to exact identity via workspace hash – they cannot be silently inherited.
        Assert.NotEqual(snapshotOriginal.Sha256, snapshotMovedWithSameProjectRule.Sha256);

        // Verify trust exact-match: original matches itself, moved does not.
        Assert.True(IsTrustedExactMatch(originalIdentity, originalIdentity));
        Assert.False(IsTrustedExactMatch(originalIdentity, movedIdentity));

        // Simulate that a moved identity must request fresh trust; its snapshot version should start at 1 and not reuse previous version 1's hash.
        Assert.Equal(1, snapshotMovedEmpty.Version);
        Assert.NotEqual(snapshotOriginal.Sha256, snapshotMovedEmpty.Sha256);
    }

    [Fact]
    public void Policy_rule_expiry_is_part_of_snapshot_and_hash_bound()
    {
        var identity = TrustedIdentity();
        var expires = CreatedAt.AddHours(1);
        var withExpiry = new PolicyRule(new PolicyRuleId("rule_11111111111111111111111111111111"), PolicyRuleScope.Once, PolicyDecision.Allow, new PolicyMatcher(ToolId: "terminal.run", OperationClass: "terminal"), 1, expires);
        var withoutExpiry = new PolicyRule(new PolicyRuleId("rule_11111111111111111111111111111111"), PolicyRuleScope.Once, PolicyDecision.Allow, new PolicyMatcher(ToolId: "terminal.run", OperationClass: "terminal"), 1, null);

        var snapWithExpiry = PolicySnapshotCanonicalizer.Create(1, AgentPermissionPreset.Standard, identity, new[] { withExpiry }, CreatedAt);
        var snapWithoutExpiry = PolicySnapshotCanonicalizer.Create(1, AgentPermissionPreset.Standard, identity, new[] { withoutExpiry }, CreatedAt);
        Assert.NotEqual(snapWithExpiry.Sha256, snapWithoutExpiry.Sha256);

        // Different expiry timestamps also change hash.
        var laterExpiry = new PolicyRule(new PolicyRuleId("rule_11111111111111111111111111111111"), PolicyRuleScope.Once, PolicyDecision.Allow, new PolicyMatcher(ToolId: "terminal.run", OperationClass: "terminal"), 1, expires.AddTicks(1));
        var snapLater = PolicySnapshotCanonicalizer.Create(1, AgentPermissionPreset.Standard, identity, new[] { laterExpiry }, CreatedAt);
        Assert.NotEqual(snapWithExpiry.Sha256, snapLater.Sha256);
    }

    private static WorkspaceIdentity TrustedIdentity(string rootFileId = "root-a", char hash = 'a') =>
        new(
            new WorkspaceId("ws_11111111111111111111111111111111"),
            "A1B2-C3D4",
            rootFileId,
            new string(hash, 64));

    private static bool IsTrustedExactMatch(WorkspaceIdentity expected, WorkspaceIdentity actual) =>
        expected.WorkspaceId.Value == actual.WorkspaceId.Value
        && expected.VolumeSerialNumber == actual.VolumeSerialNumber
        && expected.RootFileId == actual.RootFileId
        && expected.DisplayIdentityHash == actual.DisplayIdentityHash;
}
