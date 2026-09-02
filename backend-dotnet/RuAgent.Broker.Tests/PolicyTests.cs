using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Protocol.Harness;
using RuAgent.Broker.Tools;
using RuAgent.Broker.Tools.Terminal;
using RuAgent.Protocol;
using BrokerRunLease = RuAgent.Broker.Runtime.BrokerRunLease;
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
    public void Agent_and_full_profiles_enable_skills_but_chat_does_not()
    {
        Assert.Contains(ToolScope.SkillExecute, CapabilityProfile.Agent.Scopes());
        Assert.Contains(ToolScope.SkillExecute, CapabilityProfile.Full.Scopes());
        Assert.DoesNotContain(ToolScope.SkillExecute, CapabilityProfile.Chat.Scopes());
    }

    [Fact]
    public void Skill_policy_requires_explicit_id_and_scope_then_tool_scope()
    {
        var engine = new ToolPolicyEngine();
        var required = new HashSet<string>([ToolScope.SkillExecute, ToolScope.WorkspaceRead]);

        var missingId = engine.EvaluateSkill(
            new PolicyGrant("s", required, DateTimeOffset.UtcNow.AddMinutes(1), new HashSet<string>()),
            "demo.skill",
            required);
        Assert.Equal("skill_not_granted", missingId.Code);

        var missingSkillScope = engine.EvaluateSkill(
            new PolicyGrant("s", new HashSet<string>([ToolScope.WorkspaceRead]), DateTimeOffset.UtcNow.AddMinutes(1), new HashSet<string>(["demo.skill"])),
            "demo.skill",
            required);
        Assert.Equal("scope_denied", missingSkillScope.Code);

        var allowed = engine.EvaluateSkill(
            new PolicyGrant("s", required, DateTimeOffset.UtcNow.AddMinutes(1), new HashSet<string>(["demo.skill"])),
            "demo.skill",
            required);
        Assert.True(allowed.Allowed);
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

public sealed class PolicyEvaluationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Protocol_invariants_deny_before_any_rule_evaluation()
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.AllowAllRule()));
        var context = factory.Create("files.read", "1.0.0", Args(("path", "notes/a.txt")));
        var evaluator = new PolicySnapshotEvaluator();

        AssertDeny(evaluator.Evaluate(lease.Snapshot, context, Inv(schemaValid: false), Now), "schema_invalid");
        AssertDeny(evaluator.Evaluate(lease.Snapshot, context, Inv(requestBound: false), Now), "request_unbound");
        AssertDeny(evaluator.Evaluate(lease.Snapshot, context, Inv(notExpired: false), Now), "request_expired");
        AssertDeny(evaluator.Evaluate(lease.Snapshot, context, Inv(intentHash: false), Now), "intent_hash_invalid");
    }

    [Fact]
    public void Last_matching_rule_wins_in_snapshot_order()
    {
        var allowThenDeny = PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.Rule("r-allow", "allow", PolicyEvalSnapshots.Matcher(toolId: "files.read")),
            PolicyEvalSnapshots.Rule("r-deny", "deny", PolicyEvalSnapshots.Matcher(toolId: "files.read")));
        var denyThenAllow = PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.Rule("r-deny", "deny", PolicyEvalSnapshots.Matcher(toolId: "files.read")),
            PolicyEvalSnapshots.Rule("r-allow", "allow", PolicyEvalSnapshots.Matcher(toolId: "files.read")));

        var (firstLease, firstFactory) = Bind(allowThenDeny);
        var first = new PolicySnapshotEvaluator().Evaluate(
            firstLease.Snapshot,
            firstFactory.Create("files.read", "1.0.0", Args(("path", "a.txt"))),
            PolicyRequestInvariants.Valid,
            Now);
        Assert.Equal("deny", first.Decision);
        Assert.Equal("r-deny", first.MatchedRuleId);

        var (secondLease, secondFactory) = Bind(denyThenAllow);
        var second = new PolicySnapshotEvaluator().Evaluate(
            secondLease.Snapshot,
            secondFactory.Create("files.read", "1.0.0", Args(("path", "a.txt"))),
            PolicyRequestInvariants.Valid,
            Now);
        Assert.Equal("allow", second.Decision);
        Assert.Equal("r-allow", second.MatchedRuleId);
    }

    [Fact]
    public void Expired_rules_are_skipped_and_fallback_applies()
    {
        var expired = PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.Rule(
                "r-expired",
                "deny",
                PolicyEvalSnapshots.Matcher(toolId: "files.read"),
                expiresAt: "2026-08-24T00:00:00.0000000+00:00"));

        var (lease, factory) = Bind(expired);
        var evaluation = new PolicySnapshotEvaluator().Evaluate(
            lease.Snapshot,
            factory.Create("files.read", "1.0.0", Args(("path", "a.txt"))),
            PolicyRequestInvariants.Valid,
            Now);

        Assert.Equal("allow", evaluation.Decision);
        Assert.Equal("standard_workspace_allow", evaluation.ReasonCode);
        Assert.Null(evaluation.MatchedRuleId);
    }

    [Fact]
    public void Standard_fallback_allows_ordinary_workspace_operations()
    {
        foreach (var toolId in new[] { "files.list", "files.glob", "files.search", "files.read", "files.write", "files.edit", "files.apply_patch" })
        {
            var (lease, factory) = Bind(PolicyEvalSnapshots.Empty());
            var arguments = toolId == "files.list"
                ? new JsonObject { ["path"] = "." }
                : Args(("path", $"src/{toolId}.txt"));
            var evaluation = EvaluateDefault(lease, factory, toolId, arguments);
            Assert.Equal("allow", evaluation.Decision);
            Assert.Equal("standard_workspace_allow", evaluation.ReasonCode);
        }
    }

    [Theory]
    [InlineData("delete_requires_approval", "files.delete", "src/old.txt")]
    [InlineData("restore_once", "files.restore", "src/old.txt")]
    [InlineData("external_directory_required", "files.read", null)]
    [InlineData("protected_path", "files.read", ".env.local")]
    public void Standard_fallback_asks_for_risky_file_operations(string reasonCode, string toolId, string? path)
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.Empty());
        var arguments = path is null
            ? Args(("path", @"C:\Windows\outside.txt"))
            : Args(("path", path));

        var evaluation = EvaluateDefault(lease, factory, toolId, arguments);

        Assert.Equal("ask", evaluation.Decision);
        Assert.Equal(reasonCode, evaluation.ReasonCode);
    }

    [Fact]
    public void Standard_fallback_asks_for_declared_network_intent()
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.Empty());
        var evaluation = EvaluateDefault(
            lease,
            factory,
            "files.write",
            Args(("path", "out.txt"), ("network", true)));

        Assert.Equal("ask", evaluation.Decision);
        Assert.Equal("network_intent", evaluation.ReasonCode);
    }

    [Fact]
    public void Standard_terminal_classification_covers_recognized_and_unknown_forms()
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.Empty());
        var evaluator = new PolicySnapshotEvaluator();

        var rg = evaluator.Evaluate(lease.Snapshot, Terminal(factory, "rg", "pattern", "."), PolicyRequestInvariants.Valid, Now);
        Assert.Equal("allow", rg.Decision);
        Assert.Equal("recognized_safe_form", rg.ReasonCode);
        Assert.True(rg.AnalyzerRiskPending);

        var gitStatus = evaluator.Evaluate(lease.Snapshot, Terminal(factory, "git", "status"), PolicyRequestInvariants.Valid, Now);
        Assert.Equal("allow", gitStatus.Decision);
        Assert.True(gitStatus.AnalyzerRiskPending);

        var gitCommit = evaluator.Evaluate(lease.Snapshot, Terminal(factory, "git", "commit"), PolicyRequestInvariants.Valid, Now);
        Assert.Equal("ask", gitCommit.Decision);
        Assert.Equal("git_mutation", gitCommit.ReasonCode);

        var dotnetTest = evaluator.Evaluate(lease.Snapshot, Terminal(factory, "dotnet", "test"), PolicyRequestInvariants.Valid, Now);
        Assert.Equal("allow", dotnetTest.Decision);
        Assert.True(dotnetTest.AnalyzerRiskPending);

        var pytest = evaluator.Evaluate(lease.Snapshot, Terminal(factory, "python", "-m", "pytest"), PolicyRequestInvariants.Valid, Now);
        Assert.Equal("allow", pytest.Decision);
        Assert.True(pytest.AnalyzerRiskPending);

        var unknown = evaluator.Evaluate(lease.Snapshot, Terminal(factory, "frobnicate.exe"), PolicyRequestInvariants.Valid, Now);
        Assert.Equal("ask", unknown.Decision);
        Assert.Equal("unknown_executable", unknown.ReasonCode);
    }

    [Fact]
    public void Standard_terminal_fallback_uses_analyzer_classes_for_the_extended_matrix()
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.Empty());
        var evaluator = new PolicySnapshotEvaluator();

        var gitPush = evaluator.Evaluate(lease.Snapshot, Terminal(factory, "git", "push"), PolicyRequestInvariants.Valid, Now);
        Assert.Equal("ask", gitPush.Decision);
        Assert.Equal("external_mutation", gitPush.ReasonCode);

        var dotnetRestore = evaluator.Evaluate(lease.Snapshot, Terminal(factory, "dotnet", "restore"), PolicyRequestInvariants.Valid, Now);
        Assert.Equal("ask", dotnetRestore.Decision);
        Assert.Equal("external_mutation", dotnetRestore.ReasonCode);

        var ruffCheck = evaluator.Evaluate(lease.Snapshot, Terminal(factory, "ruff", "check", "."), PolicyRequestInvariants.Valid, Now);
        Assert.Equal("allow", ruffCheck.Decision);
        Assert.True(ruffCheck.AnalyzerRiskPending);

        var pipInstall = evaluator.Evaluate(lease.Snapshot, Terminal(factory, "python", "-m", "pip", "install", "requests"), PolicyRequestInvariants.Valid, Now);
        Assert.Equal("ask", pipInstall.Decision);
        Assert.Equal("external_mutation", pipInstall.ReasonCode);

        var rgReplace = evaluator.Evaluate(lease.Snapshot, Terminal(factory, "rg", "--replace", "x", "pattern"), PolicyRequestInvariants.Valid, Now);
        Assert.Equal("ask", rgReplace.Decision);
        Assert.Equal("ripgrep_replacement_requires_approval", rgReplace.ReasonCode);

        var pythonScript = evaluator.Evaluate(lease.Snapshot, Terminal(factory, "python", "script.py"), PolicyRequestInvariants.Valid, Now);
        Assert.Equal("ask", pythonScript.Decision);
    }

    [Fact]
    public void Full_access_allows_well_formed_terminal_calls_but_extended_timeouts_ask()
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.FullAccessEmpty());
        var evaluator = new PolicySnapshotEvaluator();
        var valid = PolicyRequestInvariants.Valid;

        var standardWindow = evaluator.Evaluate(
            lease.Snapshot, Terminal(factory, "dotnet", "test", 120_000), valid, Now);
        Assert.Equal("allow", standardWindow.Decision);

        var extended = evaluator.Evaluate(
            lease.Snapshot, Terminal(factory, "dotnet", "test", 1_200_000), valid, Now);
        Assert.Equal("ask", extended.Decision);
        Assert.Equal("extended_timeout_approval", extended.ReasonCode);

        var overCap = evaluator.Evaluate(
            lease.Snapshot, Terminal(factory, "dotnet", "test", 1_900_000), valid, Now);
        AssertDeny(overCap, "timeout_exceeds_hard_max");

        var shell = evaluator.Evaluate(lease.Snapshot, Shell(factory, "Get-Date"), valid, Now);
        Assert.Equal("allow", shell.Decision);

        var extendedShell = evaluator.Evaluate(
            lease.Snapshot, Shell(factory, "Get-Date", 1_200_000), valid, Now);
        Assert.Equal("ask", extendedShell.Decision);
        Assert.Equal("extended_timeout_approval", extendedShell.ReasonCode);
    }

    [Fact]
    public void Standard_asks_for_every_terminal_shell_without_an_exact_reusable_rule()
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.Empty());
        var evaluation = new PolicySnapshotEvaluator().Evaluate(
            lease.Snapshot,
            Shell(factory, "Get-Date"),
            PolicyRequestInvariants.Valid,
            Now);

        Assert.Equal("ask", evaluation.Decision);
        Assert.Equal("terminal_requires_rule", evaluation.ReasonCode);

        var withExactRule = PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.Rule(
                "r-shell-exact",
                "allow",
                PolicyEvalSnapshots.Matcher(toolId: "terminal.shell", cwdPattern: LeaseRoot)));
        var (ruleLease, ruleFactory) = Bind(withExactRule);
        var allowed = new PolicySnapshotEvaluator().Evaluate(
            ruleLease.Snapshot,
            Shell(ruleFactory, "Get-Date"),
            PolicyRequestInvariants.Valid,
            Now);
        Assert.Equal("allow", allowed.Decision);
        Assert.Equal("r-shell-exact", allowed.MatchedRuleId);
    }

    [Fact]
    public void Standard_asks_for_git_commit_but_full_access_allows_it_outside_the_managed_path()
    {
        var evaluator = new PolicySnapshotEvaluator();
        var valid = PolicyRequestInvariants.Valid;

        var (standardLease, standardFactory) = Bind(PolicyEvalSnapshots.Empty());
        var ask = evaluator.Evaluate(
            standardLease.Snapshot, Terminal(standardFactory, "git", "commit", "-m", "x"), valid, Now);
        Assert.Equal("ask", ask.Decision);
        Assert.Equal("git_mutation", ask.ReasonCode);

        var (fullLease, fullFactory) = Bind(PolicyEvalSnapshots.FullAccessEmpty());
        var allow = evaluator.Evaluate(
            fullLease.Snapshot, Terminal(fullFactory, "git", "commit", "-m", "x"), valid, Now);
        Assert.Equal("allow", allow.Decision);
    }

    [Fact]
    public void Full_access_fallback_allows_well_formed_calls_but_not_invariant_violations()
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.FullAccessEmpty());
        var evaluator = new PolicySnapshotEvaluator();
        var valid = PolicyRequestInvariants.Valid;

        Assert.Equal("allow", evaluator.Evaluate(lease.Snapshot, FileCall(factory, "files.delete", "src/old.txt"), valid, Now).Decision);
        Assert.Equal("allow", evaluator.Evaluate(lease.Snapshot, FileCall(factory, "files.read", @"C:\Windows\outside.txt"), valid, Now).Decision);
        Assert.Equal("allow", evaluator.Evaluate(lease.Snapshot, FileCall(factory, "files.write", ".env.local"), valid, Now).Decision);
        var networked = FileCall(factory, "files.write", "out.txt");
        networked = networked with { NetworkIntent = "declared", NetworkDeclared = true };
        Assert.Equal("allow", evaluator.Evaluate(lease.Snapshot, networked, valid, Now).Decision);
        Assert.Equal("allow", evaluator.Evaluate(lease.Snapshot, Shell(factory, "Remove-Item /"), valid, Now).Decision);

        AssertDeny(evaluator.Evaluate(lease.Snapshot, FileCall(factory, "files.read", "a.txt"), Inv(schemaValid: false), Now), "schema_invalid");
    }

    [Fact]
    public void Model_supplied_safety_labels_have_no_effect_on_decisions()
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.Empty());
        var labelledDelete = Args(("path", "src/old.txt"));
        labelledDelete["safe"] = true;
        labelledDelete["risk"] = "none";
        labelledDelete["safety_label"] = "approved_by_model";
        labelledDelete["decision_hint"] = "allow";

        var plain = EvaluateDefault(lease, factory, "files.delete", Args(("path", "src/old.txt")));
        var labelled = EvaluateDefault(lease, factory, "files.delete", labelledDelete);

        Assert.Equal(plain.Decision, labelled.Decision);
        Assert.Equal(plain.ReasonCode, labelled.ReasonCode);
        Assert.Equal("ask", labelled.Decision);
    }

    [Fact]
    public void Malformed_matcher_values_fail_closed_at_binding()
    {
        Assert.Throws<AgentProtocolException>(() => BoundPolicySnapshot.Parse(PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.Rule("r-bad-class", "allow", PolicyEvalSnapshots.Matcher(operationClass: "banana")))));
        Assert.Throws<AgentProtocolException>(() => BoundPolicySnapshot.Parse(PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.Rule("r-bad-intent", "allow", PolicyEvalSnapshots.Matcher(networkIntent: "sometimes")))));
        Assert.Throws<AgentProtocolException>(() => BoundPolicySnapshot.Parse(PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.Rule("r-empty-path", "allow", PolicyEvalSnapshots.Matcher(pathPattern: "  ")))));
    }

    private static PolicyEvaluation EvaluateDefault(
        BrokerRunLease lease,
        PolicyContextFactory factory,
        string toolId,
        JsonObject arguments) =>
        new PolicySnapshotEvaluator().Evaluate(
            lease.Snapshot,
            factory.Create(toolId, "1.0.0", arguments),
            PolicyRequestInvariants.Valid,
            Now);

    private static PolicyContext FileCall(PolicyContextFactory factory, string toolId, string path) =>
        factory.Create(toolId, "1.0.0", Args(("path", path)));

    private static PolicyContext Terminal(
        PolicyContextFactory factory,
        string executable,
        params object[] argvAndTimeout)
    {
        int? timeoutMs = null;
        var argvList = new List<string>();
        foreach (var item in argvAndTimeout)
        {
            if (item is int timeout)
            {
                timeoutMs = timeout;
            }
            else
            {
                argvList.Add((string)item);
            }
        }

        var arguments = new JsonObject
        {
            ["executable"] = executable,
            ["arguments"] = new JsonArray(argvList.Select(a => JsonValue.Create(a)).ToArray()),
            ["working_directory"] = ".",
        };
        if (timeoutMs is not null)
        {
            arguments["timeout_ms"] = timeoutMs.Value;
        }

        return factory.Create("terminal.run", "1.0.0", arguments);
    }

    private static PolicyContext Shell(PolicyContextFactory factory, string script, int? timeoutMs = null)
    {
        var arguments = new JsonObject
        {
            ["script"] = script,
            ["working_directory"] = ".",
        };
        if (timeoutMs is not null)
        {
            arguments["timeout_ms"] = timeoutMs.Value;
        }

        return factory.Create("terminal.shell", "1.0.0", arguments);
    }

    private static void AssertDeny(PolicyEvaluation evaluation, string reasonCode)
    {
        Assert.Equal("deny", evaluation.Decision);
        Assert.Equal(reasonCode, evaluation.ReasonCode);
        Assert.Null(evaluation.MatchedRuleId);
    }

    private static JsonObject Args(params (string Key, object Value)[] fields)
    {
        var result = new JsonObject();
        foreach (var (key, value) in fields)
        {
            result[key] = value switch
            {
                bool b => b,
                int i => i,
                long l => l,
                string s => s,
                _ => throw new ArgumentOutOfRangeException(nameof(value)),
            };
        }

        return result;
    }

    private static PolicyRequestInvariants Inv(
        bool schemaValid = true,
        bool requestBound = true,
        bool notExpired = true,
        bool intentHash = true) => new(
        SchemaValid: schemaValid,
        RequestBound: requestBound,
        NotExpired: notExpired,
        IntentHashValid: intentHash);

    private static (BrokerRunLease Lease, PolicyContextFactory Factory) Bind(BrokerPolicySnapshotWire snapshot)
    {
        var lease = BrokerRunLease.CreateValidated(
            PolicyEvalSnapshots.RunId,
            PolicyEvalSnapshots.SessionId,
            PolicyEvalSnapshots.WorkspaceId,
            LeaseRoot,
            PolicyEvalSnapshots.VolumeSerial,
            PolicyEvalSnapshots.RootFileId,
            PolicyEvalSnapshots.DisplayHash,
            snapshot.Preset,
            snapshot.Version,
            RuAgent.Protocol.Harness.BrokerPolicyContracts.ComputeCanonicalSnapshotHash(snapshot),
            DateTimeOffset.UtcNow.AddMinutes(5),
            snapshot);
        return (lease, new PolicyContextFactory(lease));
    }

    private static string LeaseRoot
    {
        get
        {
            if (_leaseRoot is null)
            {
                _leaseRoot = Path.Combine(Path.GetTempPath(), "ru-policy-eval-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(_leaseRoot, "src"));
                Directory.CreateDirectory(_leaseRoot);
            }

            return _leaseRoot;
        }
    }

    private static string? _leaseRoot;
}

internal static class PolicyEvalSnapshots
{
    public const string RunId = "run_44444444444444444444444444444444";
    public const string SessionId = "session-eval";
    public const string WorkspaceId = "ws_22222222222222222222222222222222";
    public const string VolumeSerial = "VOL-EVAL";
    public const string RootFileId = "fid-eval";
    public const string DisplayHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    public const string CreatedAt = "2026-08-24T00:00:00.0000000+00:00";

    public static BrokerPolicySnapshotWire Empty() => Snapshot(1, "standard", []);

    public static BrokerPolicySnapshotWire FullAccessEmpty(long version = 1) => Snapshot(version, "full_access", []);

    public static BrokerPolicySnapshotWire SnapshotWith(params BrokerPolicyRuleWire[] rules) =>
        Snapshot(1, "standard", rules);

    public static BrokerPolicyRuleWire AllowAllRule() =>
        Rule("r-allow-all", "allow", Matcher());

    public static BrokerPolicyRuleWire Rule(
        string id,
        string decision,
        BrokerPolicyMatcherWire matcher,
        string scope = "project",
        string? expiresAt = null) => new(id, scope, decision, matcher, CreationOrder: 1, ExpiresAt: expiresAt);

    public static BrokerPolicyMatcherWire Matcher(
        string? toolId = null,
        string? toolVersion = null,
        string? pathPattern = null,
        string? resolvedExecutable = null,
        IReadOnlyList<string>? argvPattern = null,
        string? cwdPattern = null,
        string? operationClass = null,
        string? networkIntent = null) => new(
        toolId,
        toolVersion,
        pathPattern,
        resolvedExecutable,
        argvPattern,
        cwdPattern,
        operationClass,
        networkIntent);

    private static BrokerPolicySnapshotWire Snapshot(long version, string preset, BrokerPolicyRuleWire[] rules) =>
        new(
            version,
            preset,
            new BrokerPolicyWorkspaceIdentityWire(WorkspaceId, VolumeSerial, RootFileId, DisplayHash),
            rules,
            CreatedAt);
}
