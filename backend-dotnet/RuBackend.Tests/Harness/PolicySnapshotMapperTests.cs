using System.Globalization;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Protocol.Harness;
using RuBackend.Harness.Broker;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class PolicySnapshotMapperTests
{
    private static readonly DateTimeOffset FixedCreatedAt =
        new(2026, 9, 2, 10, 0, 0, TimeSpan.FromHours(7));

    [Fact]
    public void ToWire_MapsPresetNames()
    {
        Assert.Equal("standard", MapperWire(AgentPermissionPreset.Standard).Preset);
        Assert.Equal("full_access", MapperWire(AgentPermissionPreset.FullAccess).Preset);
        Assert.Equal("trusted", MapperWire(AgentPermissionPreset.Trusted).Preset);
    }

    [Fact]
    public void ToWire_MapsWorkspaceAndTimestamp()
    {
        var wire = MapperWire(AgentPermissionPreset.Standard, createdAt: FixedCreatedAt);
        Assert.Equal("ws_" + new string('1', 32), wire.Workspace.WorkspaceId);
        Assert.Equal("VOL-MAPPER", wire.Workspace.VolumeSerialNumber);
        Assert.Equal("fid-mapper", wire.Workspace.RootFileId);
        Assert.Equal(new string('a', 64), wire.Workspace.DisplayIdentityHash);
        Assert.Equal(1, wire.Version);
        Assert.Equal(
            FixedCreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            wire.CreatedAt);
        Assert.Empty(wire.OrderedRules);
    }

    [Fact]
    public void ToWire_MapsRulesAndMatchers()
    {
        var expiresAt = new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.FromHours(-5));
        var fullMatcher = new PolicyMatcher(
            ToolId: "files.write",
            ToolVersion: "1.0.0",
            CanonicalPathPattern: "/workspace/**",
            ResolvedExecutable: null,
            ArgvPattern: new[] { "dotnet", "build" },
            CwdPattern: "/workspace/src",
            OperationClass: "write",
            NetworkIntent: "none");
        var fullRule = new PolicyRule(
            new PolicyRuleId("rule_" + new string('2', 32)),
            PolicyRuleScope.Project,
            PolicyDecision.Allow,
            fullMatcher,
            CreationOrder: 3,
            ExpiresAt: expiresAt);
        var minimalRule = new PolicyRule(
            new PolicyRuleId("rule_" + new string('3', 32)),
            PolicyRuleScope.Run,
            PolicyDecision.Deny,
            new PolicyMatcher(),
            CreationOrder: 0,
            ExpiresAt: null);

        var wire = MapperWire(
            AgentPermissionPreset.FullAccess,
            createdAt: FixedCreatedAt,
            [fullRule, minimalRule]);

        Assert.Equal(2, wire.OrderedRules.Count);
        var first = wire.OrderedRules[0];
        Assert.Equal("rule_" + new string('2', 32), first.RuleId);
        Assert.Equal("project", first.Scope);
        Assert.Equal("allow", first.Decision);
        Assert.Equal(3, first.CreationOrder);
        Assert.Equal(
            expiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            first.ExpiresAt);
        Assert.Equal("files.write", first.Matcher.ToolId);
        Assert.Equal("1.0.0", first.Matcher.ToolVersion);
        Assert.Equal("/workspace/**", first.Matcher.CanonicalPathPattern);
        Assert.Null(first.Matcher.ResolvedExecutable);
        Assert.Equal(new[] { "dotnet", "build" }, first.Matcher.ArgvPattern);
        Assert.Equal("/workspace/src", first.Matcher.CwdPattern);
        Assert.Equal("write", first.Matcher.OperationClass);
        Assert.Equal("none", first.Matcher.NetworkIntent);

        var second = wire.OrderedRules[1];
        Assert.Equal("run", second.Scope);
        Assert.Equal("deny", second.Decision);
        Assert.Null(second.ExpiresAt);
        Assert.All(
            new object?[]
            {
                second.Matcher.ToolId,
                second.Matcher.ToolVersion,
                second.Matcher.CanonicalPathPattern,
                second.Matcher.ResolvedExecutable,
                second.Matcher.ArgvPattern,
                second.Matcher.CwdPattern,
                second.Matcher.OperationClass,
                second.Matcher.NetworkIntent,
            },
            field => Assert.Null(field));
    }

    [Fact]
    public void ToWire_HashMatchesCanonicalizerSha256()
    {
        var rule = new PolicyRule(
            new PolicyRuleId("rule_" + new string('4', 32)),
            PolicyRuleScope.Once,
            PolicyDecision.Ask,
            new PolicyMatcher(
                ToolId: "terminal.run",
                ArgvPattern: new[] { "dotnet", "--version" },
                OperationClass: "terminal"),
            CreationOrder: 1,
            ExpiresAt: new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.FromHours(7)));

        var snapshot = PolicySnapshotCanonicalizer.Create(
            7,
            AgentPermissionPreset.Standard,
            Workspace(),
            [rule],
            FixedCreatedAt);

        var wire = PolicySnapshotMapper.ToWire(snapshot);
        Assert.Equal(snapshot.Sha256, BrokerPolicyContracts.ComputeCanonicalSnapshotHash(wire));
    }

    [Fact]
    public void ToWire_RejectsNullSnapshot()
    {
        Assert.Throws<ArgumentNullException>(() => PolicySnapshotMapper.ToWire(null!));
    }

    private static BrokerPolicySnapshotWire MapperWire(
        AgentPermissionPreset preset,
        DateTimeOffset? createdAt = null,
        IReadOnlyList<PolicyRule>? rules = null)
    {
        var snapshot = new PolicySnapshot(
            1,
            preset,
            Workspace(),
            rules ?? [],
            createdAt ?? DateTimeOffset.UtcNow,
            string.Empty);
        return PolicySnapshotMapper.ToWire(snapshot);
    }

    private static WorkspaceIdentity Workspace() => new(
        new WorkspaceId("ws_" + new string('1', 32)),
        "VOL-MAPPER",
        "fid-mapper",
        new string('a', 64));
}
