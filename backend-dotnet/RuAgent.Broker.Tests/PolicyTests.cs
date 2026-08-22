using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class PolicyTests
{
    [Theory]
    [InlineData(RiskClass.Read, false)]
    [InlineData(RiskClass.Reversible, false)]
    [InlineData(RiskClass.External, true)]
    [InlineData(RiskClass.Destructive, true)]
    public void Policy_matches_approval_requirement(RiskClass risk, bool approval)
    {
        var decision = new ToolPolicyEngine().Evaluate(
            new PolicyGrant(
                "session-1",
                new HashSet<string>([ToolScope.WorkspaceWrite], StringComparer.Ordinal),
                DateTimeOffset.UtcNow.AddMinutes(1)),
            Manifest(risk, [ToolScope.WorkspaceWrite]));

        Assert.True(decision.Allowed);
        Assert.Equal(approval, decision.RequiresApproval);
    }

    [Fact]
    public void Policy_rejects_missing_scope_and_expired_grant()
    {
        var manifest = Manifest(RiskClass.Read, [ToolScope.WorkspaceRead]);
        var engine = new ToolPolicyEngine();

        Assert.Equal(
            "scope_missing",
            engine.Evaluate(
                new PolicyGrant("session-1", new HashSet<string>(), DateTimeOffset.UtcNow.AddMinutes(1)),
                manifest).Code);
        Assert.Equal(
            "session_expired",
            engine.Evaluate(
                new PolicyGrant("session-1", new HashSet<string>([ToolScope.WorkspaceRead]), DateTimeOffset.UtcNow.AddSeconds(-1)),
                manifest).Code);
    }

    private static ToolManifest Manifest(RiskClass risk, string[] scopes) => new(
        "files.write",
        "1.0.0",
        "builtin",
        risk,
        new HashSet<string>(scopes, StringComparer.Ordinal),
        new JsonObject { ["type"] = "object" },
        new JsonObject { ["type"] = "object" },
        10_000,
        64 * 1024,
        "builtin",
        WorkspaceRequired: true);
}
