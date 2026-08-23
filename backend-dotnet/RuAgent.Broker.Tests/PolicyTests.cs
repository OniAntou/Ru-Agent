using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools;
using RuAgent.Broker.Tools.Terminal;
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

    [Fact]
    public void ReversibleWriteRequiresApprovalByFlag()
    {
        var manifest = MakeManifest("files.write", ToolScope.WorkspaceWrite,
            RiskClass.Reversible, requiresApproval: true);
        var decision = new ToolPolicyEngine().Evaluate(MakeGrant(ToolScope.WorkspaceWrite), manifest);
        Assert.True(decision.Allowed);
        Assert.True(decision.RequiresApproval);
    }

    [Fact]
    public void ReadToolsNeverRequireApproval()
    {
        var manifest = MakeManifest("files.read", ToolScope.WorkspaceRead,
            RiskClass.Read, requiresApproval: false);
        var decision = new ToolPolicyEngine().Evaluate(MakeGrant(ToolScope.WorkspaceRead), manifest);
        Assert.False(decision.RequiresApproval);
    }

    [Fact]
    public void Full_terminal_network_scope_does_not_bypass_shell_approval()
    {
        var decision = new ToolPolicyEngine().Evaluate(
            MakeGrant(ToolScope.TerminalExecute, ToolScope.TerminalNetwork),
            TerminalManifests.Shell);

        Assert.True(decision.Allowed);
        Assert.True(decision.RequiresApproval);
    }

    [Fact]
    public void Reversible_terminal_run_requires_approval()
    {
        var decision = new ToolPolicyEngine().Evaluate(
            MakeGrant(ToolScope.TerminalExecute, ToolScope.TerminalNetwork),
            TerminalManifests.Run);

        Assert.True(decision.Allowed);
        Assert.True(decision.RequiresApproval);
    }

    private static PolicyGrant MakeGrant(params string[] scopes) =>
        new("s1", scopes.ToHashSet(), DateTimeOffset.UtcNow.AddMinutes(5));

    private static ToolManifest MakeManifest(string id, string scope, RiskClass risk,
        bool requiresApproval) => new(
        id, "1.0.0", "builtin", risk,
        new HashSet<string>([scope]), new JsonObject { ["type"] = "object" },
        new JsonObject { ["type"] = "object" }, 30_000, 256 * 1024, "builtin",
        RequiresApproval: requiresApproval);

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
