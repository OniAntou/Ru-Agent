using RuAgent.Broker.Policy;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class CanonicalExecutableBoundsTests
{
    [Fact]
    public void Approval_and_policy_matcher_accept_a_300_character_canonical_path()
    {
        var executable = new string('x', 300);
        var binding = CreateBinding(executable);
        var matcher = CreateMatcher(executable);

        Assert.Equal(executable, binding.ResolvedExecutable);
        BrokerPolicyContracts.ValidateMatcher(matcher);
        var rule = PolicyRuleMatcher.Parse(new BrokerPolicyRuleWire(
            "rule_" + new string('1', 32), "run", "allow", matcher, 1, null));
        Assert.Equal(executable, rule.Matcher.ResolvedExecutable);
    }

    [Fact]
    public void Approval_and_policy_matcher_reject_a_path_over_the_canonical_path_cap()
    {
        var executable = new string('x', TerminalLimits.MaxCanonicalExecutableChars + 1);

        Assert.Throws<ArgumentException>(() => CreateBinding(executable));
        var matcher = CreateMatcher(executable);
        Assert.Throws<AgentProtocolException>(() => BrokerPolicyContracts.ValidateMatcher(matcher));
        Assert.Throws<AgentProtocolException>(() => PolicyRuleMatcher.Parse(new BrokerPolicyRuleWire(
            "rule_" + new string('1', 32), "run", "allow", matcher, 1, null)));
    }

    private static V2ApprovalBinding CreateBinding(string executable) => new(
        RunId: "run_11111111111111111111111111111111",
        ToolCallId: "call_11111111111111111111111111111111",
        ToolId: "run_process",
        ToolVersion: "1.0.0",
        CanonicalPath: null,
        ResolvedExecutable: executable,
        Argv: Array.Empty<string>(),
        Cwd: null,
        OperationClass: "terminal",
        NetworkIntent: "none",
        SnapshotVersion: 1,
        IntentHash: new string('a', 64));

    private static BrokerPolicyMatcherWire CreateMatcher(string executable) => new(
        ToolId: "run_process",
        ToolVersion: "1.0.0",
        CanonicalPathPattern: null,
        ResolvedExecutable: executable,
        ArgvPattern: Array.Empty<string>(),
        CwdPattern: null,
        OperationClass: null,
        NetworkIntent: null);
}
