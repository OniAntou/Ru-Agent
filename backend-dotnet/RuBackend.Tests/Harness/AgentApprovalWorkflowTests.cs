using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Protocol.Harness;
using RuBackend.Harness.Policy;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentApprovalWorkflowTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 25, 8, 0, 0, TimeSpan.Zero);

    // 11a Parity hash test
    [Fact]
    public void Parity_hash_between_core_and_protocol_matches()
    {
        var workspace = new WorkspaceIdentity(
            new WorkspaceId("ws_11111111111111111111111111111111"),
            "VOL-12345678",
            "fid-abc123",
            new string('a', 64));
        var rules = new List<PolicyRule>
        {
            new(new PolicyRuleId("rule_11111111111111111111111111111111"),
                PolicyRuleScope.Project,
                PolicyDecision.Allow,
                new PolicyMatcher(ToolId: "files.read", ToolVersion: "1.0.0", CanonicalPathPattern: "a.txt", ResolvedExecutable: null, ArgvPattern: null, CwdPattern: null, OperationClass: "read", NetworkIntent: "none"),
                CreationOrder: 1,
                ExpiresAt: null),
        };
        var snapshot = PolicySnapshotCanonicalizer.Create(
            version: 1,
            preset: AgentPermissionPreset.Standard,
            workspace: workspace,
            orderedRules: rules,
            createdAt: FixedNow);

        var protocolHash = HarnessPolicySnapshotService.ComputeProtocolHash(snapshot);
        Assert.Equal(snapshot.Sha256, protocolHash);
        Assert.True(HarnessPolicySnapshotService.ParityCheck(snapshot));
    }
}
