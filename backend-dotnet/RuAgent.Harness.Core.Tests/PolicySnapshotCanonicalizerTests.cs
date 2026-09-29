using System.Text;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Workspace;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class PolicySnapshotCanonicalizerTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 24, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Phase_one_empty_snapshot_has_exact_canonical_utf8_and_sha256()
    {
        var snapshot = PolicySnapshotCanonicalizer.Create(
            1,
            AgentPermissionPreset.Standard,
            Identity("root-a", 'a'),
            Array.Empty<PolicyRule>(),
            CreatedAt);

        var canonical = Encoding.UTF8.GetString(
            PolicySnapshotCanonicalizer.GetCanonicalUtf8(snapshot));

        Assert.Equal(
            "{\"version\":1,\"preset\":\"standard\",\"workspace\":{\"workspace_id\":\"ws_11111111111111111111111111111111\",\"volume_serial_number\":\"A1B2-C3D4\",\"root_file_id\":\"root-a\",\"display_identity_hash\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"},\"ordered_rules\":[]}",
            canonical);
        Assert.Equal(
            "a67923f5a23bf318895b8effa9d52349d80ffb25e29b49b04489c8f0ab81989a",
            snapshot.Sha256);
    }

    [Fact]
    public void Same_inputs_are_deterministic_and_each_phase_one_field_is_hash_bound()
    {
        var baseline = Create();
        var repeated = Create();
        var withRule = PolicySnapshotCanonicalizer.Create(
            1,
            AgentPermissionPreset.Standard,
            Identity("root-a", 'a'),
            new[]
            {
                new PolicyRule(
                    new PolicyRuleId("rule_11111111111111111111111111111111"),
                    PolicyRuleScope.Run,
                    PolicyDecision.Allow,
                    new PolicyMatcher(ToolId: "files.read"),
                    1,
                    null),
            },
            CreatedAt);
        var variants = new[]
        {
            Create(version: 2),
            Create(preset: AgentPermissionPreset.FullAccess),
            Create(identity: Identity("root-moved", 'b')),
            withRule,
        };

        Assert.Equal(baseline.Sha256, repeated.Sha256);
        Assert.Equal(
            PolicySnapshotCanonicalizer.GetCanonicalUtf8(baseline),
            PolicySnapshotCanonicalizer.GetCanonicalUtf8(repeated));
        Assert.All(variants, variant => Assert.NotEqual(baseline.Sha256, variant.Sha256));

        // created_at is metadata, not a hash-bound field: a snapshot re-loaded after
        // a restart carries a fresh timestamp and must still match its durable hash.
        var laterTimestamp = Create(createdAt: CreatedAt.AddTicks(1));
        Assert.Equal(baseline.Sha256, laterTimestamp.Sha256);
        Assert.Equal(
            PolicySnapshotCanonicalizer.GetCanonicalUtf8(baseline),
            PolicySnapshotCanonicalizer.GetCanonicalUtf8(laterTimestamp));
    }

    [Fact]
    public void Snapshot_copies_the_ordered_rule_collection()
    {
        var rules = new List<PolicyRule>();
        var snapshot = PolicySnapshotCanonicalizer.Create(
            1,
            AgentPermissionPreset.Standard,
            Identity("root-a", 'a'),
            rules,
            CreatedAt);

        rules.Add(new PolicyRule(
            new PolicyRuleId("rule_11111111111111111111111111111111"),
            PolicyRuleScope.Run,
            PolicyDecision.Allow,
            new PolicyMatcher(ToolId: "files.read"),
            1,
            null));

        Assert.Empty(snapshot.OrderedRules);
        Assert.Equal(
            "a67923f5a23bf318895b8effa9d52349d80ffb25e29b49b04489c8f0ab81989a",
            snapshot.Sha256);
    }

    [Fact]
    public void Canonical_json_property_order_is_deterministic_and_dictionary_order_does_not_change_hash()
    {
        // Two matchers constructed with same values but via different property assignment order
        // must produce identical canonical bytes because the writer orders fields explicitly.
        var matcherA = new PolicyMatcher(
            ToolId: "files.read",
            ToolVersion: "1",
            CanonicalPathPattern: "src/a.txt",
            ResolvedExecutable: "git",
            ArgvPattern: new[] { "a", "b" },
            CwdPattern: "src",
            OperationClass: "read",
            NetworkIntent: "none");
        var matcherB = new PolicyMatcher(
            NetworkIntent: "none",
            OperationClass: "read",
            CwdPattern: "src",
            ArgvPattern: new[] { "a", "b" },
            ResolvedExecutable: "git",
            CanonicalPathPattern: "src/a.txt",
            ToolVersion: "1",
            ToolId: "files.read");

        var snapshotA = PolicySnapshotCanonicalizer.Create(
            1,
            AgentPermissionPreset.Standard,
            Identity("root-a", 'a'),
            new[] { new PolicyRule(new PolicyRuleId("rule_11111111111111111111111111111111"), PolicyRuleScope.Project, PolicyDecision.Allow, matcherA, 1, null) },
            CreatedAt);

        var snapshotB = PolicySnapshotCanonicalizer.Create(
            1,
            AgentPermissionPreset.Standard,
            Identity("root-a", 'a'),
            new[] { new PolicyRule(new PolicyRuleId("rule_11111111111111111111111111111111"), PolicyRuleScope.Project, PolicyDecision.Allow, matcherB, 1, null) },
            CreatedAt);

        Assert.Equal(snapshotA.Sha256, snapshotB.Sha256);
        Assert.Equal(
            PolicySnapshotCanonicalizer.GetCanonicalUtf8(snapshotA),
            PolicySnapshotCanonicalizer.GetCanonicalUtf8(snapshotB));

        // Verify explicit ordering: version appears before preset, preset before workspace, etc.
        var canonical = Encoding.UTF8.GetString(PolicySnapshotCanonicalizer.GetCanonicalUtf8(snapshotA));
        var versionIdx = canonical.IndexOf("\"version\"", StringComparison.Ordinal);
        var presetIdx = canonical.IndexOf("\"preset\"", StringComparison.Ordinal);
        var workspaceIdx = canonical.IndexOf("\"workspace\"", StringComparison.Ordinal);
        var orderedRulesIdx = canonical.IndexOf("\"ordered_rules\"", StringComparison.Ordinal);
        Assert.True(versionIdx < presetIdx);
        Assert.True(presetIdx < workspaceIdx);
        Assert.True(workspaceIdx < orderedRulesIdx);
        Assert.DoesNotContain("\"created_at\"", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_hash_bound_field_change_alters_hash()
    {
        var baseline = PolicySnapshotCanonicalizer.Create(
            1,
            AgentPermissionPreset.Standard,
            Identity("root-a", 'a'),
            new[]
            {
                new PolicyRule(
                    new PolicyRuleId("rule_11111111111111111111111111111111"),
                    PolicyRuleScope.Project,
                    PolicyDecision.Allow,
                    new PolicyMatcher(ToolId: "files.read", CanonicalPathPattern: "src/a.txt", ArgvPattern: new[] { "a" }, OperationClass: "read"),
                    1,
                    null),
                new PolicyRule(
                    new PolicyRuleId("rule_22222222222222222222222222222222"),
                    PolicyRuleScope.Run,
                    PolicyDecision.Deny,
                    new PolicyMatcher(ToolId: "files.write", CanonicalPathPattern: "src/b.txt", ArgvPattern: new[] { "b" }, OperationClass: "write"),
                    2,
                    null),
            },
            CreatedAt);

        var ruleOrderSwapped = PolicySnapshotCanonicalizer.Create(
            1,
            AgentPermissionPreset.Standard,
            Identity("root-a", 'a'),
            new[]
            {
                new PolicyRule(
                    new PolicyRuleId("rule_22222222222222222222222222222222"),
                    PolicyRuleScope.Run,
                    PolicyDecision.Deny,
                    new PolicyMatcher(ToolId: "files.write", CanonicalPathPattern: "src/b.txt", ArgvPattern: new[] { "b" }, OperationClass: "write"),
                    2,
                    null),
                new PolicyRule(
                    new PolicyRuleId("rule_11111111111111111111111111111111"),
                    PolicyRuleScope.Project,
                    PolicyDecision.Allow,
                    new PolicyMatcher(ToolId: "files.read", CanonicalPathPattern: "src/a.txt", ArgvPattern: new[] { "a" }, OperationClass: "read"),
                    1,
                    null),
            },
            CreatedAt);

        var decisionChanged = PolicySnapshotCanonicalizer.Create(
            1,
            AgentPermissionPreset.Standard,
            Identity("root-a", 'a'),
            new[]
            {
                new PolicyRule(
                    new PolicyRuleId("rule_11111111111111111111111111111111"),
                    PolicyRuleScope.Project,
                    PolicyDecision.Deny,
                    new PolicyMatcher(ToolId: "files.read", CanonicalPathPattern: "src/a.txt", ArgvPattern: new[] { "a" }, OperationClass: "read"),
                    1,
                    null),
                new PolicyRule(
                    new PolicyRuleId("rule_22222222222222222222222222222222"),
                    PolicyRuleScope.Run,
                    PolicyDecision.Deny,
                    new PolicyMatcher(ToolId: "files.write", CanonicalPathPattern: "src/b.txt", ArgvPattern: new[] { "b" }, OperationClass: "write"),
                    2,
                    null),
            },
            CreatedAt);

        var pathChanged = PolicySnapshotCanonicalizer.Create(
            1,
            AgentPermissionPreset.Standard,
            Identity("root-a", 'a'),
            new[]
            {
                new PolicyRule(
                    new PolicyRuleId("rule_11111111111111111111111111111111"),
                    PolicyRuleScope.Project,
                    PolicyDecision.Allow,
                    new PolicyMatcher(ToolId: "files.read", CanonicalPathPattern: "src/changed.txt", ArgvPattern: new[] { "a" }, OperationClass: "read"),
                    1,
                    null),
                new PolicyRule(
                    new PolicyRuleId("rule_22222222222222222222222222222222"),
                    PolicyRuleScope.Run,
                    PolicyDecision.Deny,
                    new PolicyMatcher(ToolId: "files.write", CanonicalPathPattern: "src/b.txt", ArgvPattern: new[] { "b" }, OperationClass: "write"),
                    2,
                    null),
            },
            CreatedAt);

        var argvChanged = PolicySnapshotCanonicalizer.Create(
            1,
            AgentPermissionPreset.Standard,
            Identity("root-a", 'a'),
            new[]
            {
                new PolicyRule(
                    new PolicyRuleId("rule_11111111111111111111111111111111"),
                    PolicyRuleScope.Project,
                    PolicyDecision.Allow,
                    new PolicyMatcher(ToolId: "files.read", CanonicalPathPattern: "src/a.txt", ArgvPattern: new[] { "changed" }, OperationClass: "read"),
                    1,
                    null),
                new PolicyRule(
                    new PolicyRuleId("rule_22222222222222222222222222222222"),
                    PolicyRuleScope.Run,
                    PolicyDecision.Deny,
                    new PolicyMatcher(ToolId: "files.write", CanonicalPathPattern: "src/b.txt", ArgvPattern: new[] { "b" }, OperationClass: "write"),
                    2,
                    null),
            },
            CreatedAt);

        var workspaceChanged = PolicySnapshotCanonicalizer.Create(
            1,
            AgentPermissionPreset.Standard,
            Identity("root-moved", 'c'),
            new[]
            {
                new PolicyRule(
                    new PolicyRuleId("rule_11111111111111111111111111111111"),
                    PolicyRuleScope.Project,
                    PolicyDecision.Allow,
                    new PolicyMatcher(ToolId: "files.read", CanonicalPathPattern: "src/a.txt", ArgvPattern: new[] { "a" }, OperationClass: "read"),
                    1,
                    null),
                new PolicyRule(
                    new PolicyRuleId("rule_22222222222222222222222222222222"),
                    PolicyRuleScope.Run,
                    PolicyDecision.Deny,
                    new PolicyMatcher(ToolId: "files.write", CanonicalPathPattern: "src/b.txt", ArgvPattern: new[] { "b" }, OperationClass: "write"),
                    2,
                    null),
            },
            CreatedAt);

        var presetChanged = PolicySnapshotCanonicalizer.Create(
            1,
            AgentPermissionPreset.FullAccess,
            Identity("root-a", 'a'),
            new[]
            {
                new PolicyRule(
                    new PolicyRuleId("rule_11111111111111111111111111111111"),
                    PolicyRuleScope.Project,
                    PolicyDecision.Allow,
                    new PolicyMatcher(ToolId: "files.read", CanonicalPathPattern: "src/a.txt", ArgvPattern: new[] { "a" }, OperationClass: "read"),
                    1,
                    null),
                new PolicyRule(
                    new PolicyRuleId("rule_22222222222222222222222222222222"),
                    PolicyRuleScope.Run,
                    PolicyDecision.Deny,
                    new PolicyMatcher(ToolId: "files.write", CanonicalPathPattern: "src/b.txt", ArgvPattern: new[] { "b" }, OperationClass: "write"),
                    2,
                    null),
            },
            CreatedAt);

        var versionChanged = PolicySnapshotCanonicalizer.Create(
            2,
            AgentPermissionPreset.Standard,
            Identity("root-a", 'a'),
            new[]
            {
                new PolicyRule(
                    new PolicyRuleId("rule_11111111111111111111111111111111"),
                    PolicyRuleScope.Project,
                    PolicyDecision.Allow,
                    new PolicyMatcher(ToolId: "files.read", CanonicalPathPattern: "src/a.txt", ArgvPattern: new[] { "a" }, OperationClass: "read"),
                    1,
                    null),
                new PolicyRule(
                    new PolicyRuleId("rule_22222222222222222222222222222222"),
                    PolicyRuleScope.Run,
                    PolicyDecision.Deny,
                    new PolicyMatcher(ToolId: "files.write", CanonicalPathPattern: "src/b.txt", ArgvPattern: new[] { "b" }, OperationClass: "write"),
                    2,
                    null),
            },
            CreatedAt);

        Assert.NotEqual(baseline.Sha256, ruleOrderSwapped.Sha256);
        Assert.NotEqual(baseline.Sha256, decisionChanged.Sha256);
        Assert.NotEqual(baseline.Sha256, pathChanged.Sha256);
        Assert.NotEqual(baseline.Sha256, argvChanged.Sha256);
        Assert.NotEqual(baseline.Sha256, workspaceChanged.Sha256);
        Assert.NotEqual(baseline.Sha256, presetChanged.Sha256);
        Assert.NotEqual(baseline.Sha256, versionChanged.Sha256);
    }

    [Fact]
    public void Invalid_or_unknown_matcher_kinds_fail_closed_rather_than_omitted()
    {
        // Unknown operation class must be rejected at matcher construction – fail closed, not omitted from hash.
        Assert.Throws<ArgumentOutOfRangeException>(() => new PolicyMatcher(OperationClass: "invalid_kind_zzz"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PolicyMatcher(NetworkIntent: "invalid_network_zzz"));
        Assert.Throws<ArgumentException>(() => new PolicyMatcher(ToolId: ""));
        Assert.Throws<ArgumentException>(() => new PolicyMatcher(CanonicalPathPattern: new string('a', 5000)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PolicyMatcher(ArgvPattern: Enumerable.Repeat("a", 65).ToArray()));

        // Unknown enum values for scope/decision must fail closed during snapshot creation.
        var validMatcher = new PolicyMatcher(ToolId: "files.read", OperationClass: "read");
        var unknownScopeRule = new PolicyRule(
            new PolicyRuleId("rule_11111111111111111111111111111111"),
            (PolicyRuleScope)int.MaxValue,
            PolicyDecision.Allow,
            validMatcher,
            1,
            null);
        var unknownDecisionRule = new PolicyRule(
            new PolicyRuleId("rule_11111111111111111111111111111111"),
            PolicyRuleScope.Run,
            (PolicyDecision)int.MaxValue,
            validMatcher,
            1,
            null);

        Assert.Throws<ArgumentOutOfRangeException>(() => PolicySnapshotCanonicalizer.Create(1, AgentPermissionPreset.Standard, Identity("root-a", 'a'), new[] { unknownScopeRule }, CreatedAt));
        Assert.Throws<ArgumentOutOfRangeException>(() => PolicySnapshotCanonicalizer.Create(1, AgentPermissionPreset.Standard, Identity("root-a", 'a'), new[] { unknownDecisionRule }, CreatedAt));

        // Unknown preset must also fail closed.
        Assert.Throws<ArgumentOutOfRangeException>(() => PolicySnapshotCanonicalizer.Create(1, (AgentPermissionPreset)int.MaxValue, Identity("root-a", 'a'), Array.Empty<PolicyRule>(), CreatedAt));

        // Snapshot construction itself must fail closed for unknown matcher scope – not omit the field.
        Assert.Throws<ArgumentOutOfRangeException>(() => new PolicySnapshot(
            1,
            AgentPermissionPreset.Standard,
            Identity("root-a", 'a'),
            new[]
            {
                new PolicyRule(
                    new PolicyRuleId("rule_11111111111111111111111111111111"),
                    (PolicyRuleScope)int.MaxValue,
                    PolicyDecision.Allow,
                    validMatcher,
                    1,
                    null),
            },
            CreatedAt,
            new string('a', 64)));
    }

    private static PolicySnapshot Create(
        long version = 1,
        AgentPermissionPreset preset = AgentPermissionPreset.Standard,
        WorkspaceIdentity? identity = null,
        DateTimeOffset? createdAt = null) =>
        PolicySnapshotCanonicalizer.Create(
            version,
            preset,
            identity ?? Identity("root-a", 'a'),
            Array.Empty<PolicyRule>(),
            createdAt ?? CreatedAt);

    private static WorkspaceIdentity Identity(string rootFileId, char hash) =>
        new(
            new WorkspaceId("ws_11111111111111111111111111111111"),
            "A1B2-C3D4",
            rootFileId,
            new string(hash, 64));
}
