using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed partial class PolicyEvaluationTests
{
    [Fact]
    public void Explicit_patch_operation_deny_applies_to_the_entire_patch()
    {
        var snapshot = PolicyEvalSnapshots.SnapshotWith(PolicyEvalSnapshots.Rule("deny-patches", "deny",
            PolicyEvalSnapshots.Matcher(operationClass: "patch")));
        var (lease, factory) = Bind(snapshot);
        var context = factory.Create("apply_patch", "1.0.0", new JsonObject { ["patch"] =
            "--- /dev/null\n+++ b/ok.txt\n@@ -0,0 +1,1 @@\n+ok\n" });
        var decision = new PolicySnapshotEvaluator().Evaluate(lease.Snapshot, context, PolicyRequestInvariants.Valid, DateTimeOffset.UtcNow);
        Assert.Equal("deny", decision.Decision);
        Assert.Equal("deny-patches", decision.MatchedRuleId);
    }

    [Fact]
    public void Patch_evaluates_every_target_including_protected_second_file()
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.Empty());
        var context = factory.Create("apply_patch", "1.0.0", new JsonObject { ["patch"] =
            "--- /dev/null\n+++ a/ok.txt\n@@ -0,0 +1,1 @@\n+ok\n" +
            "--- /dev/null\n+++ a/.ru/hooks.json\n@@ -0,0 +1,1 @@\n+{}\n" });
        var decision = new PolicySnapshotEvaluator().Evaluate(lease.Snapshot, context, PolicyRequestInvariants.Valid, DateTimeOffset.UtcNow);
        Assert.Equal("deny", decision.Decision);
    }

    [Theory]
    [InlineData("write_file", "write", "write")]
    [InlineData("edit_file", "edit", "edit")]
    [InlineData("powershell", "bash", "terminal")]
    public void New_identity_keeps_policy_family_and_binds_original_arguments(string id, string legacyId, string operation)
    {
        var (_, factory) = Bind(PolicyEvalSnapshots.Empty());
        var arguments = id == "powershell" ? new JsonObject { ["script"] = "Write-Output ok" }
            : new JsonObject { ["path"] = "a.txt", ["content"] = "hello" };
        var context = factory.Create(id, "1.0.0", arguments);
        Assert.Equal(id, context.ToolId);
        Assert.Equal(operation, context.OperationClass);
        var matcher = new MatcherSpec(legacyId, null, null, null, [], null, null, null);
        Assert.True(PolicyRuleMatcher.MatchesMatcher(matcher, context));
        var changed = (JsonObject)arguments.DeepClone();
        changed[id == "powershell" ? "script" : "content"] = "changed";
        Assert.NotEqual(context.IntentHash, factory.Create(id, "1.0.0", changed).IntentHash);
    }
}
