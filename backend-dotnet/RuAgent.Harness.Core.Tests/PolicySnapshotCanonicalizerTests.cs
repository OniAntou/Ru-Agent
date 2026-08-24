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
            "{\"version\":1,\"preset\":\"standard\",\"workspace\":{\"workspace_id\":\"ws_11111111111111111111111111111111\",\"volume_serial_number\":\"A1B2-C3D4\",\"root_file_id\":\"root-a\",\"display_identity_hash\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"},\"ordered_rules\":[],\"created_at\":\"2026-08-24T08:00:00.0000000+00:00\"}",
            canonical);
        Assert.Equal(
            "cce0e0a6677226ded25745c3aff6facdea655d6ce29147391800120bf21c8823",
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
            Create(createdAt: CreatedAt.AddTicks(1)),
            withRule,
        };

        Assert.Equal(baseline.Sha256, repeated.Sha256);
        Assert.Equal(
            PolicySnapshotCanonicalizer.GetCanonicalUtf8(baseline),
            PolicySnapshotCanonicalizer.GetCanonicalUtf8(repeated));
        Assert.All(variants, variant => Assert.NotEqual(baseline.Sha256, variant.Sha256));
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
            "cce0e0a6677226ded25745c3aff6facdea655d6ce29147391800120bf21c8823",
            snapshot.Sha256);
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
