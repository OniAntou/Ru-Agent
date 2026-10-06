using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Policy.Commands;
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
    public void Agent_profile_keeps_terminal_network_ungranted()
    {
        Assert.DoesNotContain(ToolScope.TerminalNetwork, CapabilityProfile.Agent.Scopes());
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
    public void Full_terminal_network_scope_does_not_bypass_terminal_policy()
    {
        var decision = new ToolPolicyEngine().Evaluate(
            MakeGrant(ToolScope.TerminalExecute),
            TerminalManifests.Bash);

        Assert.True(decision.Allowed);
        Assert.True(decision.RequiresApproval);
    }

    [Fact]
    public void Reversible_terminal_run_requires_approval()
    {
        var decision = new ToolPolicyEngine().Evaluate(
            MakeGrant(ToolScope.TerminalExecute, ToolScope.TerminalNetwork),
            TerminalManifests.Bash);

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

    public static TheoryData<string, AgentPolicySubject, string> CanonicalProfileMatrix => new()
    {
        { "plan", AgentPolicySubject.WorkspaceRead, "allow" },
        { "plan", AgentPolicySubject.Web, "allow" },
        { "plan", AgentPolicySubject.WorkspaceMutation, "deny" },
        { "plan", AgentPolicySubject.BoundedLocalTerminal, "deny" },
        { "plan", AgentPolicySubject.ExternalStructuredFile, "ask" },
        { "plan", AgentPolicySubject.RiskyTerminal, "deny" },
        { "plan", AgentPolicySubject.StructuredProtectedPath, "deny" },
        { "plan", AgentPolicySubject.HardInvalid, "deny" },
        { "standard", AgentPolicySubject.WorkspaceRead, "allow" },
        { "standard", AgentPolicySubject.Web, "allow" },
        { "standard", AgentPolicySubject.WorkspaceMutation, "allow" },
        { "standard", AgentPolicySubject.BoundedLocalTerminal, "allow" },
        { "standard", AgentPolicySubject.ExternalStructuredFile, "ask" },
        { "standard", AgentPolicySubject.RiskyTerminal, "ask" },
        { "standard", AgentPolicySubject.StructuredProtectedPath, "deny" },
        { "standard", AgentPolicySubject.HardInvalid, "deny" },
        { "full_access", AgentPolicySubject.WorkspaceRead, "allow" },
        { "full_access", AgentPolicySubject.Web, "allow" },
        { "full_access", AgentPolicySubject.WorkspaceMutation, "allow" },
        { "full_access", AgentPolicySubject.BoundedLocalTerminal, "allow" },
        { "full_access", AgentPolicySubject.ExternalStructuredFile, "allow" },
        { "full_access", AgentPolicySubject.RiskyTerminal, "allow" },
        { "full_access", AgentPolicySubject.StructuredProtectedPath, "deny" },
        { "full_access", AgentPolicySubject.HardInvalid, "deny" },
    };

    [Fact]
    public void Standard_boundary_asks_outside_and_allows_inside_after_grant()
    {
        var external = Path.Combine(Path.GetTempPath(), "ru-policy-external-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        try
        {
            var snapshot = PolicyEvalSnapshots.Empty();
            var (lease, factory) = Bind(snapshot);
            var grants = new SessionDirectoryGrantRegistry();
            var evaluator = new PolicySnapshotEvaluator(grants);
            var path = Path.Combine(external, "note.txt");

            var before = evaluator.Evaluate(
                lease.Snapshot,
                FileCall(factory, "files.delete", path),
                PolicyRequestInvariants.Valid,
                Now);
            Assert.Equal("ask", before.Decision);
            Assert.Equal("external_directory_required", before.ReasonCode);

            grants.Add(PolicyEvalSnapshots.SessionId, CanonicalDirectoryIdentity.CreateExisting(external));
            var after = evaluator.Evaluate(
                lease.Snapshot,
                FileCall(factory, "files.delete", path),
                PolicyRequestInvariants.Valid,
                Now);
            Assert.Equal("allow", after.Decision);
        }
        finally
        {
            Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public void Standard_preset_asks_only_for_terminal_risks()
    {
        var snapshot = PolicyEvalSnapshots.Empty();
        var (lease, factory) = Bind(snapshot);
        var evaluator = new PolicySnapshotEvaluator(new SessionDirectoryGrantRegistry());

        var git = evaluator.Evaluate(
            lease.Snapshot, Terminal(factory, "git", "status"), PolicyRequestInvariants.Valid, Now);
        var delete = evaluator.Evaluate(
            lease.Snapshot, Terminal(factory, "rm", "old.txt"), PolicyRequestInvariants.Valid, Now);
        var network = evaluator.Evaluate(
            lease.Snapshot, Terminal(factory, "git", "push"), PolicyRequestInvariants.Valid, Now);
        var unknown = evaluator.Evaluate(
            lease.Snapshot, Terminal(factory, "mystery-tool.exe"), PolicyRequestInvariants.Valid, Now);

        Assert.Equal("allow", git.Decision);
        Assert.Equal("ask", delete.Decision);
        Assert.Equal("terminal_delete_requires_approval", delete.ReasonCode);
        Assert.Equal("ask", network.Decision);
        Assert.Equal("terminal_external_mutation_requires_approval", network.ReasonCode);
        Assert.Equal("ask", unknown.Decision);
        Assert.Equal("terminal_unknown_executable", unknown.ReasonCode);
    }

    [Theory]
    [MemberData(nameof(CanonicalProfileMatrix))]
    public void Canonical_profile_matrix_matches_each_policy_subject(
        string preset,
        AgentPolicySubject subject,
        string expectedDecision)
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.Empty(preset));

        var evaluation = new PolicySnapshotEvaluator().Evaluate(
            lease.Snapshot,
            ProfileMatrixContext(factory, subject),
            PolicyRequestInvariants.Valid,
            Now);

        Assert.Equal(expectedDecision, evaluation.Decision);
    }

    [Fact]
    public void Standard_file_folder_grant_does_not_authorize_external_terminal()
    {
        var external = Path.Combine(Path.GetTempPath(), "ru-policy-terminal-grant-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        try
        {
            var (lease, factory) = Bind(PolicyEvalSnapshots.Empty());
            var grants = new SessionDirectoryGrantRegistry();
            grants.Add(PolicyEvalSnapshots.SessionId, CanonicalDirectoryIdentity.CreateExisting(external));
            var context = factory.Create(
                "bash",
                "1.0.0",
                new JsonObject
                {
                    ["mode"] = "run",
                    ["executable"] = "git",
                    ["arguments"] = new JsonArray("status"),
                    ["working_directory"] = external,
                });

            var evaluation = new PolicySnapshotEvaluator(grants).Evaluate(
                lease.Snapshot,
                context,
                PolicyRequestInvariants.Valid,
                Now);

            Assert.Equal("ask", evaluation.Decision);
        }
        finally
        {
            Directory.Delete(external, recursive: true);
        }
    }

    [Theory]
    [InlineData("plan", "deny")]
    [InlineData("standard", "ask")]
    [InlineData("full_access", "allow")]
    public void Protected_terminal_cwd_uses_the_terminal_profile(
        string preset,
        string expectedDecision)
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-policy-protected-cwd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "harness_key"));
        try
        {
            var (lease, factory) = Bind(PolicyEvalSnapshots.Empty(preset), root);
            var context = factory.Create(
                "bash",
                "1.0.0",
                new JsonObject
                {
                    ["mode"] = "run",
                    ["executable"] = "git",
                    ["arguments"] = new JsonArray("status"),
                    ["working_directory"] = "harness_key",
                });

            var evaluation = new PolicySnapshotEvaluator().Evaluate(
                lease.Snapshot,
                context,
                PolicyRequestInvariants.Valid,
                Now);

            Assert.Equal(expectedDecision, evaluation.Decision);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("plan", "deny")]
    [InlineData("standard", "ask")]
    [InlineData("full_access", "allow")]
    public void External_terminal_cwd_uses_the_terminal_profile(
        string preset,
        string expectedDecision)
    {
        var parent = Path.Combine(Path.GetTempPath(), "ru-policy-external-cwd-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "workspace");
        var outside = Path.Combine(parent, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            var (lease, factory) = Bind(PolicyEvalSnapshots.Empty(preset), root);
            var context = factory.Create(
                "bash",
                "1.0.0",
                new JsonObject
                {
                    ["mode"] = "run",
                    ["executable"] = "git",
                    ["arguments"] = new JsonArray("status"),
                    ["working_directory"] = outside,
                });

            var evaluation = new PolicySnapshotEvaluator().Evaluate(
                lease.Snapshot,
                context,
                PolicyRequestInvariants.Valid,
                Now);

            Assert.Equal(expectedDecision, evaluation.Decision);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("standard")]
    [InlineData("full_access")]
    public void Protected_structured_file_target_remains_a_hard_deny(string preset)
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.Empty(preset));

        var evaluation = new PolicySnapshotEvaluator().Evaluate(
            lease.Snapshot,
            FileCall(factory, "files.write", "runtime_descriptor.json"),
            PolicyRequestInvariants.Valid,
            Now);

        AssertDeny(evaluation, "protected_path");
    }

    [Fact]
    public void Full_access_allows_terminal_risks_web_and_external()
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.FullAccessEmpty());
        var evaluator = new PolicySnapshotEvaluator(new SessionDirectoryGrantRegistry());

        var git = evaluator.Evaluate(
            lease.Snapshot, Terminal(factory, "git", "status"), PolicyRequestInvariants.Valid, Now);
        var delete = evaluator.Evaluate(
            lease.Snapshot, Terminal(factory, "rm", "old.txt"), PolicyRequestInvariants.Valid, Now);
        var network = evaluator.Evaluate(
            lease.Snapshot, Terminal(factory, "git", "push"), PolicyRequestInvariants.Valid, Now);
        var unknown = evaluator.Evaluate(
            lease.Snapshot, Terminal(factory, "mystery-tool.exe"), PolicyRequestInvariants.Valid, Now);
        var web = evaluator.Evaluate(
            lease.Snapshot, factory.Create("web_search", "1.0.0", new JsonObject { ["query"] = "ru" }),
            PolicyRequestInvariants.Valid, Now);
        var external = evaluator.Evaluate(
            lease.Snapshot, FileCall(factory, "files.read", @"C:\Windows\outside.txt"),
            PolicyRequestInvariants.Valid, Now);
        var shell = evaluator.Evaluate(
            lease.Snapshot, Shell(factory, "Get-Date"), PolicyRequestInvariants.Valid, Now);

        Assert.Equal("allow", git.Decision);
        Assert.Equal("full_access_allow", git.ReasonCode);
        Assert.Equal("allow", delete.Decision);
        Assert.Equal("full_access_allow", delete.ReasonCode);
        Assert.Equal("allow", network.Decision);
        Assert.Equal("full_access_allow", network.ReasonCode);
        Assert.Equal("allow", unknown.Decision);
        Assert.Equal("full_access_allow", unknown.ReasonCode);
        Assert.Equal("allow", web.Decision);
        Assert.Equal("full_access_allow", web.ReasonCode);
        Assert.Equal("allow", external.Decision);
        Assert.Equal("full_access_allow", external.ReasonCode);
        Assert.Equal("allow", shell.Decision);
        Assert.Equal("full_access_allow", shell.ReasonCode);
    }

    [Theory]
    [InlineData("Get-Date", "ask", "terminal_unknown_executable")]
    [InlineData("Remove-Item ./old.txt", "ask", "terminal_delete_requires_approval")]
    [InlineData("& $tool", "ask", "terminal_unknown_executable")]
    public void Shell_always_asks_regardless_of_ast_classification(string script, string decision, string reason)
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.Empty());
        var evaluation = new PolicySnapshotEvaluator(new SessionDirectoryGrantRegistry()).Evaluate(
            lease.Snapshot,
            Shell(factory, script),
            PolicyRequestInvariants.Valid,
            Now);

        Assert.Equal(decision, evaluation.Decision);
        Assert.Equal(reason, evaluation.ReasonCode);
    }

    [Fact]
    public void Web_tools_allow_on_plan_preset()
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.Empty("plan"));
        var arguments = new JsonObject { ["query"] = "ru" };

        var evaluation = new PolicySnapshotEvaluator(new SessionDirectoryGrantRegistry()).Evaluate(
            lease.Snapshot,
            factory.Create("web_search", "1.0.0", arguments),
            PolicyRequestInvariants.Valid,
            Now);
        Assert.Equal("allow", evaluation.Decision);
    }

    [Fact]
    public void Standard_preset_allows_web_without_approval()
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.Empty());
        var verdict = new PolicySnapshotEvaluator(new SessionDirectoryGrantRegistry()).Evaluate(
            lease.Snapshot,
            factory.Create("web_search", "1.0.0", new JsonObject { ["query"] = "ru" }),
            PolicyRequestInvariants.Valid,
            Now);
        Assert.Equal("allow", verdict.Decision);
    }

    [Fact]
    public void Plan_web_allow_rule_does_not_reintroduce_the_old_gate()
    {
        var snapshot = PolicyEvalSnapshots.SnapshotWith(
                PolicyEvalSnapshots.Rule(
                    "r-allow-web",
                    "allow",
                    PolicyEvalSnapshots.Matcher(toolId: "web_search")))
            with { Preset = "plan" };
        var (lease, factory) = Bind(snapshot);

        var evaluation = new PolicySnapshotEvaluator().Evaluate(
            lease.Snapshot,
            factory.Create("web_search", "1.0.0", new JsonObject { ["query"] = "ru" }),
            PolicyRequestInvariants.Valid,
            Now);

        Assert.Equal("allow", evaluation.Decision);
    }

    [Fact]
    public void Web_approval_rule_overrides_still_win()
    {
        var snapshot = PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.Rule(
                "r-deny-web",
                "deny",
                PolicyEvalSnapshots.Matcher(toolId: "web_search")));
        var (lease, factory) = Bind(snapshot);
        var arguments = new JsonObject { ["query"] = "ru" };

        var gated = new PolicySnapshotEvaluator(new SessionDirectoryGrantRegistry());
        var denied = gated.Evaluate(
            lease.Snapshot,
            factory.Create("web_search", "1.0.0", arguments),
            PolicyRequestInvariants.Valid,
            Now);
        Assert.Equal("deny", denied.Decision);
        Assert.Equal("r-deny-web", denied.MatchedRuleId);
    }

    [Fact]
    public void Protocol_invariants_deny_before_any_rule_evaluation()
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.AllowAllRule()));
        var context = factory.Create("read", "1.0.0", new JsonObject { ["path"] = "notes/a.txt" });
        var evaluator = new PolicySnapshotEvaluator();

        AssertDeny(evaluator.Evaluate(lease.Snapshot, context, Inv(schemaValid: false), Now), "schema_invalid");
        AssertDeny(evaluator.Evaluate(lease.Snapshot, context, Inv(requestBound: false), Now), "request_unbound");
        AssertDeny(evaluator.Evaluate(lease.Snapshot, context, Inv(notExpired: false), Now), "request_expired");
        AssertDeny(evaluator.Evaluate(lease.Snapshot, context, Inv(intentHash: false), Now), "intent_hash_invalid");
    }

    [Fact]
    public void Deny_beats_allow_regardless_of_snapshot_order()
    {
        var allowThenDeny = PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.Rule("r-allow", "allow", PolicyEvalSnapshots.Matcher(toolId: "read")),
            PolicyEvalSnapshots.Rule("r-deny", "deny", PolicyEvalSnapshots.Matcher(toolId: "read")));
        var denyThenAllow = PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.Rule("r-deny", "deny", PolicyEvalSnapshots.Matcher(toolId: "read")),
            PolicyEvalSnapshots.Rule("r-allow", "allow", PolicyEvalSnapshots.Matcher(toolId: "read")));

        var (firstLease, firstFactory) = Bind(allowThenDeny);
        var first = new PolicySnapshotEvaluator().Evaluate(
            firstLease.Snapshot,
            firstFactory.Create("read", "1.0.0", new JsonObject { ["path"] = "a.txt" }),
            PolicyRequestInvariants.Valid,
            Now);
        Assert.Equal("deny", first.Decision);
        Assert.Equal("r-deny", first.MatchedRuleId);

        var (secondLease, secondFactory) = Bind(denyThenAllow);
        var second = new PolicySnapshotEvaluator().Evaluate(
            secondLease.Snapshot,
            secondFactory.Create("read", "1.0.0", new JsonObject { ["path"] = "a.txt" }),
            PolicyRequestInvariants.Valid,
            Now);
        Assert.Equal("deny", second.Decision);
        Assert.Equal("r-deny", second.MatchedRuleId);
    }

    [Fact]
    public void Ask_beats_allow_regardless_of_snapshot_order()
    {
        var allowThenAsk = PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.Rule("r-allow", "allow", PolicyEvalSnapshots.Matcher(toolId: "read")),
            PolicyEvalSnapshots.Rule("r-ask", "ask", PolicyEvalSnapshots.Matcher(toolId: "read")));
        var askThenAllow = PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.Rule("r-ask", "ask", PolicyEvalSnapshots.Matcher(toolId: "read")),
            PolicyEvalSnapshots.Rule("r-allow", "allow", PolicyEvalSnapshots.Matcher(toolId: "read")));

        var (firstLease, firstFactory) = Bind(allowThenAsk);
        var first = new PolicySnapshotEvaluator().Evaluate(
            firstLease.Snapshot,
            firstFactory.Create("read", "1.0.0", new JsonObject { ["path"] = "a.txt" }),
            PolicyRequestInvariants.Valid,
            Now);
        Assert.Equal("ask", first.Decision);
        Assert.Equal("r-ask", first.MatchedRuleId);

        var (secondLease, secondFactory) = Bind(askThenAllow);
        var second = new PolicySnapshotEvaluator().Evaluate(
            secondLease.Snapshot,
            secondFactory.Create("read", "1.0.0", new JsonObject { ["path"] = "a.txt" }),
            PolicyRequestInvariants.Valid,
            Now);
        Assert.Equal("ask", second.Decision);
        Assert.Equal("r-ask", second.MatchedRuleId);
    }

    [Fact]
    public void Expired_rules_are_skipped_and_fallback_applies()
    {
        var expired = PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.Rule(
                "r-expired",
                "deny",
                PolicyEvalSnapshots.Matcher(toolId: "read"),
                expiresAt: "2026-08-24T00:00:00.0000000+00:00"));

        var (lease, factory) = Bind(expired);
        var evaluation = new PolicySnapshotEvaluator().Evaluate(
            lease.Snapshot,
            factory.Create("read", "1.0.0", new JsonObject { ["path"] = "a.txt" }),
            PolicyRequestInvariants.Valid,
            Now);

        Assert.Equal("allow", evaluation.Decision);
        Assert.Equal("standard_workspace_allow", evaluation.ReasonCode);
        Assert.Null(evaluation.MatchedRuleId);
    }

    [Fact]
    public void Standard_fallback_allows_ordinary_workspace_operations()
    {
        var cases = new (string PiId, JsonObject Args)[]
        {
            ("list", new JsonObject { ["path"] = "." }),
            ("glob", new JsonObject { ["pattern"] = "*.txt", ["path"] = "." }),
            ("grep", new JsonObject { ["query"] = "x", ["path"] = "." }),
            ("read", new JsonObject { ["path"] = "src/read.txt" }),
            ("write", new JsonObject { ["op"] = "write", ["path"] = "src/write.txt" }),
            ("edit", new JsonObject { ["path"] = "src/edit.txt", ["old_string"] = "a" }),
        };
        foreach (var (piId, args) in cases)
        {
            var (lease, factory) = Bind(PolicyEvalSnapshots.Empty());
            var evaluation = new PolicySnapshotEvaluator().Evaluate(
                lease.Snapshot,
                factory.Create(piId, "1.0.0", args),
                PolicyRequestInvariants.Valid,
                Now);
            Assert.Equal("allow", evaluation.Decision);
            Assert.Equal("standard_workspace_allow", evaluation.ReasonCode);
        }
    }

    [Fact]
    public void External_file_operation_asks_without_a_session_directory_grant()
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.Empty());
        var evaluation = new PolicySnapshotEvaluator().Evaluate(
            lease.Snapshot,
            factory.Create("read", "1.0.0", new JsonObject { ["path"] = @"C:\Windows\outside.txt" }),
            PolicyRequestInvariants.Valid,
            Now);

        Assert.Equal("ask", evaluation.Decision);
        Assert.Equal("external_directory_required", evaluation.ReasonCode);
    }

    [Fact]
    public void Explicit_deny_rule_denies_an_external_file_under_full_access()
    {
        // A user-authored deny over a path outside the workspace must be honoured.
        // full_access allows external structured files by default, so evaluating
        // the profile before the ordered rules turned this deny into an allow.
        var (lease, factory) = Bind(PolicyEvalSnapshots.FullAccessWith(
            PolicyEvalSnapshots.Rule(
                "r-deny-client-secrets",
                "deny",
                PolicyEvalSnapshots.Matcher(toolId: "read", pathPattern: @"C:\ClientSecrets\**"))));
        var evaluation = new PolicySnapshotEvaluator().Evaluate(
            lease.Snapshot,
            factory.Create("read", "1.0.0", new JsonObject { ["path"] = @"C:\ClientSecrets\layout.docx" }),
            PolicyRequestInvariants.Valid,
            Now);

        Assert.Equal("deny", evaluation.Decision);
        Assert.Equal("rule_denied", evaluation.ReasonCode);
        Assert.Equal("r-deny-client-secrets", evaluation.MatchedRuleId);
    }

    [Fact]
    public void Explicit_deny_rule_downgrades_the_external_file_ask_under_standard()
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.Rule(
                "r-deny-client-secrets",
                "deny",
                PolicyEvalSnapshots.Matcher(toolId: "read", pathPattern: @"C:\ClientSecrets\**"))));
        var evaluation = new PolicySnapshotEvaluator().Evaluate(
            lease.Snapshot,
            factory.Create("read", "1.0.0", new JsonObject { ["path"] = @"C:\ClientSecrets\layout.docx" }),
            PolicyRequestInvariants.Valid,
            Now);

        Assert.Equal("deny", evaluation.Decision);
        Assert.Equal("rule_denied", evaluation.ReasonCode);
    }

    [Fact]
    public void Explicit_allow_rule_does_not_bypass_the_external_directory_ask()
    {
        // The counterweight to the two tests above: hoisting the rule loop must not
        // let an allow rule grant external access the profile would have asked for.
        var (lease, factory) = Bind(PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.Rule(
                "r-allow-reads",
                "allow",
                PolicyEvalSnapshots.Matcher(toolId: "read", pathPattern: @"C:\ClientSecrets\**"))));
        var evaluation = new PolicySnapshotEvaluator().Evaluate(
            lease.Snapshot,
            factory.Create("read", "1.0.0", new JsonObject { ["path"] = @"C:\ClientSecrets\layout.docx" }),
            PolicyRequestInvariants.Valid,
            Now);

        Assert.Equal("ask", evaluation.Decision);
        Assert.Equal("external_directory_required", evaluation.ReasonCode);
    }

    [Fact]
    public void Explicit_deny_rule_denies_an_external_file_despite_a_later_allow()
    {
        // Deny beats allow regardless of snapshot order, including outside
        // the workspace: a remembered allow can never override an explicit
        // deny.
        var (lease, factory) = Bind(PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.Rule(
                "r-deny-client-secrets",
                "deny",
                PolicyEvalSnapshots.Matcher(toolId: "read", pathPattern: @"C:\ClientSecrets\**")),
            PolicyEvalSnapshots.Rule(
                "r-allow-client-secrets",
                "allow",
                PolicyEvalSnapshots.Matcher(toolId: "read", pathPattern: @"C:\ClientSecrets\**"))));
        var evaluation = new PolicySnapshotEvaluator().Evaluate(
            lease.Snapshot,
            factory.Create("read", "1.0.0", new JsonObject { ["path"] = @"C:\ClientSecrets\layout.docx" }),
            PolicyRequestInvariants.Valid,
            Now);

        Assert.Equal("deny", evaluation.Decision);
        Assert.Equal("rule_denied", evaluation.ReasonCode);
    }

    [Fact]
    public void Malformed_calls_carry_a_repair_hint()
    {
        var (_, factory) = Bind(PolicyEvalSnapshots.Empty());

        // read no longer takes an op, so the malformed case is write without one.
        var writeWithoutOp = Assert.Throws<PolicyContextException>(
            () => factory.Create("write", "1.0.0", new JsonObject { ["path"] = "notes/a.txt", ["content"] = "x" }));
        Assert.Equal("malformed_call", writeWithoutOp.Code);
        Assert.Contains("write requires op", writeWithoutOp.Hint);

        var bashWithoutExecutable = Assert.Throws<PolicyContextException>(
            () => factory.Create("bash", "1.0.0", new JsonObject { ["working_directory"] = "." }));
        Assert.Equal("malformed_call", bashWithoutExecutable.Code);
        Assert.Contains("executable", bashWithoutExecutable.Hint);

        Assert.Null(new PolicyContextException("invalid_path").Hint);
    }

    [Fact]
    public void Protected_path_denies_but_file_mutations_and_network_metadata_are_automatic()
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.Empty());
        var protectedPath = new PolicySnapshotEvaluator().Evaluate(
            lease.Snapshot,
            factory.Create("read", "1.0.0", new JsonObject { ["path"] = "runtime_descriptor.json" }),
            PolicyRequestInvariants.Valid,
            Now);
        var envFile = new PolicySnapshotEvaluator().Evaluate(
            lease.Snapshot,
            factory.Create("read", "1.0.0", new JsonObject { ["path"] = ".env.local" }),
            PolicyRequestInvariants.Valid,
            Now);
        var delete = new PolicySnapshotEvaluator().Evaluate(
            lease.Snapshot,
            factory.Create("write", "1.0.0", new JsonObject { ["op"] = "delete", ["path"] = "src/old.txt" }),
            PolicyRequestInvariants.Valid,
            Now);
        var restore = new PolicySnapshotEvaluator().Evaluate(
            lease.Snapshot,
            factory.Create("write", "1.0.0", new JsonObject { ["op"] = "restore", ["path"] = "src/old.txt" }),
            PolicyRequestInvariants.Valid,
            Now);
        var networked = new PolicySnapshotEvaluator().Evaluate(
            lease.Snapshot,
            factory.Create("write", "1.0.0", new JsonObject { ["op"] = "write", ["path"] = "out.txt", ["network"] = true }),
            PolicyRequestInvariants.Valid,
            Now);

        Assert.Equal("deny", protectedPath.Decision);
        Assert.Equal("protected_path", protectedPath.ReasonCode);
        // .env files follow the preset profile now: the workspace read is a
        // normal auto-allowed read instead of a hard deny.
        Assert.Equal("allow", envFile.Decision);
        Assert.Equal("allow", delete.Decision);
        Assert.Equal("allow", restore.Decision);
        Assert.Equal("allow", networked.Decision);
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
        Assert.Equal("allow", gitCommit.Decision);

        var dotnetTest = evaluator.Evaluate(lease.Snapshot, Terminal(factory, "dotnet", "test"), PolicyRequestInvariants.Valid, Now);
        Assert.Equal("allow", dotnetTest.Decision);
        Assert.True(dotnetTest.AnalyzerRiskPending);

        var pytest = evaluator.Evaluate(lease.Snapshot, Terminal(factory, "python", "-m", "pytest"), PolicyRequestInvariants.Valid, Now);
        Assert.Equal("allow", pytest.Decision);
        Assert.True(pytest.AnalyzerRiskPending);

        var unknown = evaluator.Evaluate(lease.Snapshot, Terminal(factory, "frobnicate.exe"), PolicyRequestInvariants.Valid, Now);
        Assert.Equal("ask", unknown.Decision);
        Assert.Equal("terminal_unknown_executable", unknown.ReasonCode);
    }

    [Fact]
    public void Standard_terminal_fallback_uses_analyzer_classes_for_the_extended_matrix()
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.Empty());
        var evaluator = new PolicySnapshotEvaluator();

        var gitPush = evaluator.Evaluate(lease.Snapshot, Terminal(factory, "git", "push"), PolicyRequestInvariants.Valid, Now);
        Assert.Equal("ask", gitPush.Decision);
        Assert.Equal("terminal_external_mutation_requires_approval", gitPush.ReasonCode);

        var dotnetRestore = evaluator.Evaluate(lease.Snapshot, Terminal(factory, "dotnet", "restore"), PolicyRequestInvariants.Valid, Now);
        Assert.Equal("ask", dotnetRestore.Decision);
        Assert.Equal("terminal_external_mutation_requires_approval", dotnetRestore.ReasonCode);

        var ruffCheck = evaluator.Evaluate(lease.Snapshot, Terminal(factory, "ruff", "check", "."), PolicyRequestInvariants.Valid, Now);
        Assert.Equal("allow", ruffCheck.Decision);
        Assert.True(ruffCheck.AnalyzerRiskPending);

        var pipInstall = evaluator.Evaluate(lease.Snapshot, Terminal(factory, "python", "-m", "pip", "install", "requests"), PolicyRequestInvariants.Valid, Now);
        Assert.Equal("ask", pipInstall.Decision);
        Assert.Equal("terminal_external_mutation_requires_approval", pipInstall.ReasonCode);

        var rgReplace = evaluator.Evaluate(lease.Snapshot, Terminal(factory, "rg", "--replace", "x", "pattern"), PolicyRequestInvariants.Valid, Now);
        Assert.Equal("ask", rgReplace.Decision);
        Assert.Equal("terminal_unknown_executable", rgReplace.ReasonCode);

        var pythonScript = evaluator.Evaluate(lease.Snapshot, Terminal(factory, "python", "script.py"), PolicyRequestInvariants.Valid, Now);
        Assert.Equal("ask", pythonScript.Decision);
        Assert.Equal("terminal_external_mutation_requires_approval", pythonScript.ReasonCode);
    }

    [Fact]
    public void Standard_submodule_status_is_bounded_but_script_and_update_forms_ask()
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.Empty());
        var evaluator = new PolicySnapshotEvaluator();

        var status = evaluator.Evaluate(
            lease.Snapshot,
            Terminal(factory, "git", "submodule", "status"),
            PolicyRequestInvariants.Valid,
            Now);
        Assert.Equal("allow", status.Decision);

        string[][] riskyForms =
        [
            ["submodule", "foreach", "git status"],
            ["submodule", "update", "--remote"],
            ["submodule", "update", "--fetch"],
            ["submodule", "sync"],
        ];
        foreach (var form in riskyForms)
        {
            var evaluation = evaluator.Evaluate(
                lease.Snapshot,
                Terminal(factory, "git", form.Cast<object>().ToArray()),
                PolicyRequestInvariants.Valid,
                Now);
            Assert.Equal("ask", evaluation.Decision);
            Assert.Equal("terminal_external_mutation_requires_approval", evaluation.ReasonCode);
        }
    }

    [Theory]
    [InlineData("rm")]
    [InlineData("del")]
    [InlineData("erase")]
    public void Direct_delete_executables_have_a_dedicated_terminal_class(string executable)
    {
        var analysis = new CommandAnalyzerRegistry().Analyze(executable + ".exe", ["target.txt"]);

        Assert.Equal(TerminalOperationClass.DirectDelete, analysis.OperationClass);
        Assert.Equal("terminal_direct_delete", analysis.ReasonCode);
        Assert.Equal("2", analysis.AnalyzerVersion);
    }

    [Theory]
    [InlineData("allow")]
    [InlineData("ask")]
    public void Hard_timeout_deny_precedes_explicit_rules(string decision)
    {
        var snapshot = PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.Rule("r-timeout", decision, PolicyEvalSnapshots.Matcher()));
        var (lease, factory) = Bind(snapshot);
        var context = Terminal(
            factory,
            "git",
            "status",
            TerminalLimits.HardMaxTimeoutMs + 1);

        var evaluation = new PolicySnapshotEvaluator().Evaluate(
            lease.Snapshot,
            context,
            PolicyRequestInvariants.Valid,
            Now);

        AssertDeny(evaluation, "timeout_exceeds_hard_max");
    }

    [Fact]
    public void Hard_timeout_denies_every_preset_and_valid_timeouts_use_profile_policy()
    {
        var valid = PolicyRequestInvariants.Valid;

        var (standardLease, standardFactory) = Bind(PolicyEvalSnapshots.Empty());
        var standard = new PolicySnapshotEvaluator();

        var standardWindow = standard.Evaluate(
            standardLease.Snapshot, Terminal(standardFactory, "dotnet", "test", 120_000), valid, Now);
        Assert.Equal("allow", standardWindow.Decision);

        var extended = standard.Evaluate(
            standardLease.Snapshot, Terminal(standardFactory, "dotnet", "test", 1_200_000), valid, Now);
        // Above the standard window the protocol requires an explicit approval, so an
        // otherwise-allowed bounded command is not silently granted a 20-minute run.
        Assert.Equal("ask", extended.Decision);
        Assert.Equal("terminal_timeout_requires_approval", extended.ReasonCode);

        var atThreshold = standard.Evaluate(
            standardLease.Snapshot, Terminal(standardFactory, "dotnet", "test", TerminalLimits.StandardApprovalThresholdMs), valid, Now);
        Assert.Equal("allow", atThreshold.Decision);

        var overCap = standard.Evaluate(
            standardLease.Snapshot, Terminal(standardFactory, "dotnet", "test", 1_900_000), valid, Now);
        AssertDeny(overCap, "timeout_exceeds_hard_max");

        var shell = standard.Evaluate(standardLease.Snapshot, Shell(standardFactory, "Get-Date"), valid, Now);
        Assert.Equal("ask", shell.Decision);
        Assert.Equal("terminal_unknown_executable", shell.ReasonCode);

        var extendedShell = standard.Evaluate(
            standardLease.Snapshot, Shell(standardFactory, "Get-Date", 1_200_000), valid, Now);
        Assert.Equal("ask", extendedShell.Decision);
        // The timeout gate is decided before the terminal classifier runs, because it
        // applies to every terminal call above the standard window, not only the ones
        // the classifier would have asked about anyway.
        Assert.Equal("terminal_timeout_requires_approval", extendedShell.ReasonCode);

        var (fullLease, fullFactory) = Bind(PolicyEvalSnapshots.FullAccessEmpty());
        var full = new PolicySnapshotEvaluator();

        var fullStandardWindow = full.Evaluate(
            fullLease.Snapshot, Terminal(fullFactory, "dotnet", "test", 120_000), valid, Now);
        Assert.Equal("allow", fullStandardWindow.Decision);
        Assert.Equal("full_access_allow", fullStandardWindow.ReasonCode);

        var fullExtended = full.Evaluate(
            fullLease.Snapshot, Terminal(fullFactory, "dotnet", "test", 1_200_000), valid, Now);
        // full_access is the skip-all mode: the profile already auto-allows
        // arbitrary terminal, so the standard-window gate does not ask. The hard
        // maximum still bounds the run.
        Assert.Equal("allow", fullExtended.Decision);
        Assert.Equal("full_access_allow", fullExtended.ReasonCode);

        var fullOverCap = full.Evaluate(
            fullLease.Snapshot, Terminal(fullFactory, "dotnet", "test", 1_900_000), valid, Now);
        AssertDeny(fullOverCap, "timeout_exceeds_hard_max");

        var fullShell = full.Evaluate(fullLease.Snapshot, Shell(fullFactory, "Get-Date"), valid, Now);
        Assert.Equal("allow", fullShell.Decision);
        Assert.Equal("full_access_allow", fullShell.ReasonCode);
    }

    [Fact]
    public void Background_terminal_uses_its_lifetime_cap_instead_of_the_window_gate()
    {
        var valid = PolicyRequestInvariants.Valid;
        var (lease, factory) = Bind(PolicyEvalSnapshots.Empty());
        var evaluator = new PolicySnapshotEvaluator();

        var background = factory.Create("bash", "1.0.0", new JsonObject
        {
            ["mode"] = "run",
            ["executable"] = "dotnet",
            ["arguments"] = new JsonArray("test"),
            ["background"] = true,
            ["timeout_ms"] = TerminalLimits.BackgroundDefaultLifetimeMs,
        });
        var allowed = evaluator.Evaluate(lease.Snapshot, background, valid, Now);
        Assert.Equal("allow", allowed.Decision);

        var overCap = factory.Create("bash", "1.0.0", new JsonObject
        {
            ["mode"] = "run",
            ["executable"] = "dotnet",
            ["arguments"] = new JsonArray("test"),
            ["background"] = true,
            ["timeout_ms"] = TerminalLimits.BackgroundMaxLifetimeMs + 1,
        });
        AssertDeny(evaluator.Evaluate(lease.Snapshot, overCap, valid, Now), "timeout_exceeds_hard_max");

        // Foreground keeps both gates: the standard-window ask and the 30-minute
        // hard maximum.
        var foreground = factory.Create("bash", "1.0.0", new JsonObject
        {
            ["mode"] = "run",
            ["executable"] = "dotnet",
            ["arguments"] = new JsonArray("test"),
            ["timeout_ms"] = TerminalLimits.StandardApprovalThresholdMs + 1,
        });
        var foregroundAsk = evaluator.Evaluate(lease.Snapshot, foreground, valid, Now);
        Assert.Equal("ask", foregroundAsk.Decision);
        Assert.Equal("terminal_timeout_requires_approval", foregroundAsk.ReasonCode);
    }

    [Fact]
    public void Task_kill_and_spool_read_classify_as_reads()
    {
        var valid = PolicyRequestInvariants.Valid;
        var (lease, factory) = Bind(PolicyEvalSnapshots.Empty());
        var evaluator = new PolicySnapshotEvaluator();

        var kill = evaluator.Evaluate(
            lease.Snapshot,
            factory.Create("bash", "1.0.0", new JsonObject
            {
                ["mode"] = "kill",
                ["execution_id"] = "req-background-1",
            }),
            valid,
            Now);
        Assert.Equal("allow", kill.Decision);

        var read = evaluator.Evaluate(
            lease.Snapshot,
            factory.Create("bash", "1.0.0", new JsonObject
            {
                ["mode"] = "read_output",
                ["execution_id"] = "req-background-1",
            }),
            valid,
            Now);
        Assert.Equal("allow", read.Decision);
    }

    [Fact]
    public void Safe_terminal_run_is_automatic_shell_always_asks()
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.Empty());
        var evaluation = new PolicySnapshotEvaluator().Evaluate(
            lease.Snapshot,
            Terminal(factory, "git", "status"),
            PolicyRequestInvariants.Valid,
            Now);
        Assert.Equal("allow", evaluation.Decision);

        var shell = new PolicySnapshotEvaluator().Evaluate(
            lease.Snapshot,
            Shell(factory, "Get-Date"),
            PolicyRequestInvariants.Valid,
            Now);
        Assert.Equal("ask", shell.Decision);
        Assert.Equal("terminal_unknown_executable", shell.ReasonCode);

        Assert.Equal("allow", evaluation.Decision);
        Assert.Equal("recognized_safe_form", evaluation.ReasonCode);

        var withExactRule = PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.Rule(
                "r-shell-exact",
                "allow",
                PolicyEvalSnapshots.Matcher(toolId: "bash", cwdPattern: LeaseRoot)));
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
    public void Git_commit_is_automatic_for_standard_and_full_access()
    {
        var evaluator = new PolicySnapshotEvaluator();
        var valid = PolicyRequestInvariants.Valid;

        var (standardLease, standardFactory) = Bind(PolicyEvalSnapshots.Empty());
        var standard = evaluator.Evaluate(
            standardLease.Snapshot, Terminal(standardFactory, "git", "commit", "-m", "x"), valid, Now);
        Assert.Equal("allow", standard.Decision);

        var (fullLease, fullFactory) = Bind(PolicyEvalSnapshots.FullAccessEmpty());
        var allow = evaluator.Evaluate(
            fullLease.Snapshot, Terminal(fullFactory, "git", "commit", "-m", "x"), valid, Now);
        Assert.Equal("allow", allow.Decision);
    }

    [Fact]
    public void Ladder_presets_wire_binds_and_canonicalizes()
    {
        Assert.Equal("plan", BrokerPolicyContracts.CanonicalizePreset("plan"));
        Assert.Equal("standard", BrokerPolicyContracts.CanonicalizePreset("standard"));
        Assert.Equal("full_access", BrokerPolicyContracts.CanonicalizePreset("full_access"));
        Assert.Throws<AgentProtocolException>(() => BrokerPolicyContracts.CanonicalizePreset("trusted"));
        Assert.Throws<AgentProtocolException>(() => BrokerPolicyContracts.CanonicalizePreset("trust"));

        var (planLease, _) = Bind(PolicyEvalSnapshots.Empty("plan"));
        Assert.Equal("plan", planLease.Snapshot.Preset);
    }

    [Fact]
    public void Standard_workspace_risk_remains_ask_without_repository_state()
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.Empty());
        var evaluator = new PolicySnapshotEvaluator(new SessionDirectoryGrantRegistry());

        var unknown = evaluator.Evaluate(lease.Snapshot, Terminal(factory, "mystery-tool.exe"), PolicyRequestInvariants.Valid, Now);
        Assert.Equal("ask", unknown.Decision);
        Assert.Equal("terminal_unknown_executable", unknown.ReasonCode);
    }

    [Fact]
    public void Plan_preset_respects_explicit_deny_rules()
    {
        var snapshot = PolicyEvalSnapshots.SnapshotWith(
                PolicyEvalSnapshots.Rule("r-deny-write", "deny", PolicyEvalSnapshots.Matcher(toolId: "files.write")))
            with { Preset = "plan" };
        var (lease, factory) = Bind(snapshot);
        var verdict = new PolicySnapshotEvaluator(new SessionDirectoryGrantRegistry()).Evaluate(
            lease.Snapshot,
            FileCall(factory, "files.write", "src/a.txt"),
            PolicyRequestInvariants.Valid,
            Now);
        Assert.Equal("deny", verdict.Decision);
        Assert.Equal("rule_denied", verdict.ReasonCode);
    }

    [Fact]
    public void Plan_preset_denies_all_mutations_without_auto()
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.Empty("plan"));
        var evaluator = new PolicySnapshotEvaluator(new SessionDirectoryGrantRegistry());

        var write = evaluator.Evaluate(
            lease.Snapshot,
            FileCall(factory, "files.write", "src/write.txt"),
            PolicyRequestInvariants.Valid,
            Now);
        Assert.Equal("deny", write.Decision);
        Assert.Equal("plan_preset_read_only", write.ReasonCode);

        var edit = evaluator.Evaluate(
            lease.Snapshot,
            FileCall(factory, "files.edit", "src/edit.txt"),
            PolicyRequestInvariants.Valid,
            Now);
        Assert.Equal("deny", edit.Decision);
        Assert.Equal("plan_preset_read_only", edit.ReasonCode);

        var terminal = evaluator.Evaluate(
            lease.Snapshot,
            Terminal(factory, "git", "status"),
            PolicyRequestInvariants.Valid,
            Now);
        Assert.Equal("deny", terminal.Decision);
        Assert.Equal("plan_preset_read_only", terminal.ReasonCode);

        var shell = evaluator.Evaluate(
            lease.Snapshot,
            Shell(factory, "Get-Date"),
            PolicyRequestInvariants.Valid,
            Now);
        Assert.Equal("deny", shell.Decision);
        Assert.Equal("plan_preset_read_only", shell.ReasonCode);

        var web = evaluator.Evaluate(
            lease.Snapshot,
            factory.Create("web_search", "1.0.0", new JsonObject { ["query"] = "ru" }),
            PolicyRequestInvariants.Valid,
            Now);
        Assert.Equal("allow", web.Decision);
        Assert.Equal("plan_web_allow", web.ReasonCode);

        // Reads still flow: protocol invariants and protected paths keep their codes.
        var read = evaluator.Evaluate(
            lease.Snapshot,
            FileCall(factory, "files.read", "src/read.txt"),
            PolicyRequestInvariants.Valid,
            Now);
        Assert.Equal("allow", read.Decision);

        var protectedRead = evaluator.Evaluate(
            lease.Snapshot,
            FileCall(factory, "files.read", "runtime_descriptor.json"),
            PolicyRequestInvariants.Valid,
            Now);
        Assert.Equal("deny", protectedRead.Decision);
        Assert.Equal("protected_path", protectedRead.ReasonCode);
    }

    [Fact]
    public void Full_access_preset_allows_terminal_without_asking()
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.FullAccessEmpty());
        var evaluation = new PolicySnapshotEvaluator(new SessionDirectoryGrantRegistry()).Evaluate(
            lease.Snapshot,
            Terminal(factory, "git", "status"),
            PolicyRequestInvariants.Valid,
            Now);

        Assert.Equal("allow", evaluation.Decision);
    }

    [Fact]
    public void Full_access_allows_everything_but_still_denies_hard_limits()
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.FullAccessEmpty());
        var evaluator = new PolicySnapshotEvaluator(new SessionDirectoryGrantRegistry());
        var valid = PolicyRequestInvariants.Valid;

        Assert.Equal("allow", evaluator.Evaluate(lease.Snapshot, FileCall(factory, "files.delete", "src/old.txt"), valid, Now).Decision);
        Assert.Equal("allow", evaluator.Evaluate(lease.Snapshot, FileCall(factory, "files.read", @"C:\Windows\outside.txt"), valid, Now).Decision);
        Assert.Equal("allow", evaluator.Evaluate(lease.Snapshot, Shell(factory, "Remove-Item /"), valid, Now).Decision);
        var networked = FileCall(factory, "files.write", "out.txt");
        networked = networked with { NetworkIntent = "declared", NetworkDeclared = true };
        Assert.Equal("allow", evaluator.Evaluate(lease.Snapshot, networked, valid, Now).Decision);

        Assert.Equal("allow", evaluator.Evaluate(lease.Snapshot, FileCall(factory, "files.write", ".env.local"), valid, Now).Decision);
        AssertDeny(evaluator.Evaluate(lease.Snapshot, FileCall(factory, "files.write", "runtime_descriptor.json"), valid, Now), "protected_path");
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
        Assert.Equal("allow", labelled.Decision);
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

    [Fact]
    public void Spool_read_classifies_as_a_target_free_read()
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.Empty());
        var context = factory.Create(
            "bash",
            "1.0.0",
            new JsonObject { ["mode"] = "read_output", ["execution_id"] = "req-1" });

        Assert.Equal(PolicyOperationClasses.Read, context.OperationClass);
        Assert.Null(context.Target);
    }

    [Fact]
    public void Spool_read_is_allowed_without_approval_in_standard()
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.Empty());
        var evaluation = new PolicySnapshotEvaluator().Evaluate(
            lease.Snapshot,
            factory.Create(
                "bash",
                "1.0.0",
                new JsonObject { ["mode"] = "read_output", ["execution_id"] = "req-1" }),
            PolicyRequestInvariants.Valid,
            Now);

        Assert.Equal("allow", evaluation.Decision);
    }

    [Fact]
    public void Explicit_deny_on_bash_still_denies_a_spool_read()
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.Rule(
                "r-deny-bash",
                "deny",
                PolicyEvalSnapshots.Matcher(toolId: "bash"))));
        var evaluation = new PolicySnapshotEvaluator().Evaluate(
            lease.Snapshot,
            factory.Create(
                "bash",
                "1.0.0",
                new JsonObject { ["mode"] = "read_output", ["execution_id"] = "req-1" }),
            PolicyRequestInvariants.Valid,
            Now);

        Assert.Equal("deny", evaluation.Decision);
        Assert.Equal("rule_denied", evaluation.ReasonCode);
    }

    private static PolicyContext ProfileMatrixContext(
        PolicyContextFactory factory,
        AgentPolicySubject subject) => subject switch
    {
        AgentPolicySubject.WorkspaceRead => factory.Create(
            "read",
            "1.0.0",
            new JsonObject { ["path"] = "src/read.txt" }),
        AgentPolicySubject.Web => factory.Create(
            "web_search",
            "1.0.0",
            new JsonObject { ["query"] = "ru" }),
        AgentPolicySubject.WorkspaceMutation => factory.Create(
            "write",
            "1.0.0",
            new JsonObject { ["op"] = "write", ["path"] = "src/write.txt" }),
        AgentPolicySubject.BoundedLocalTerminal => Terminal(factory, "git", "status"),
        AgentPolicySubject.ExternalStructuredFile => factory.Create(
            "read",
            "1.0.0",
            new JsonObject { ["path"] = Path.Combine(Path.GetTempPath(), "ru-policy-outside.txt") }),
        AgentPolicySubject.RiskyTerminal => Terminal(factory, "git", "push"),
        AgentPolicySubject.StructuredProtectedPath => factory.Create(
            "read",
            "1.0.0",
            new JsonObject { ["path"] = "runtime_descriptor.json" }),
        AgentPolicySubject.HardInvalid => Terminal(
            factory,
            "git",
            "status",
            TerminalLimits.HardMaxTimeoutMs + 1),
        _ => throw new ArgumentOutOfRangeException(nameof(subject)),
    };

    private static PolicyEvaluation EvaluateDefault(
        BrokerRunLease lease,
        PolicyContextFactory factory,
        string toolId,
        JsonObject arguments)
    {
        var (piId, piArgs) = MapToPiArgs(toolId, arguments);
        return new PolicySnapshotEvaluator().Evaluate(
            lease.Snapshot,
            factory.Create(piId, "1.0.0", piArgs),
            PolicyRequestInvariants.Valid,
            Now);
    }

    private static (string PiId, JsonObject PiArgs) MapToPiArgs(string toolId, JsonObject arguments)
    {
        var mapped = (JsonObject)arguments.DeepClone();
        string piId = toolId;
        string? op = null;
        string? mode = null;
        switch (toolId)
        {
            case "files.list": piId = "read"; op = "list"; break;
            case "files.glob": piId = "glob"; mapped.Remove("op"); break;
            case "files.search": piId = "grep"; mapped.Remove("op"); break;
            case "files.read": piId = "read"; op = "read"; break;
            case "files.write": piId = "write"; op = "write"; break;
            case "files.edit": piId = "edit"; break;
            case "files.delete": piId = "write"; op = "delete"; break;
            case "files.restore": piId = "write"; op = "restore"; break;
            case "terminal.run": piId = "bash"; mode = "run"; break;
            case "terminal.shell": piId = "bash"; mode = "shell"; break;
            case "web.search": piId = "web_search"; break;
            case "web.fetch": piId = "web_fetch"; break;
            case "help.docs": piId = "read"; op = "help"; break;
        }

        if (op is not null) mapped["op"] = op;
        if (mode is not null) mapped["mode"] = mode;
        if (piId is "web_search") mapped.Remove("op");
        if (piId is "web_fetch") mapped.Remove("op");
        return (piId, mapped);
    }

    private static PolicyContext FileCall(PolicyContextFactory factory, string toolId, string path)
    {
        var (piId, op) = toolId switch
        {
            "read" or "write" or "edit" or "bash" or "grep" or "glob" => (toolId, path),
            "files.list" => ("read", "list"),
            "files.glob" => ("glob", "glob"),
            "files.search" => ("grep", "grep"),
            "files.read" => ("read", "read"),
            "files.write" => ("write", "write"),
            "files.edit" => ("edit", "edit"),
            "files.delete" => ("write", "delete"),
            "files.restore" => ("write", "restore"),
            "help.docs" => ("read", "help"),
            "web.search" => ("web_search", "web_search"),
            "web.fetch" => ("web_fetch", "web_fetch"),
            _ => (toolId, path),
        };
        var args = piId is "web_search"
            ? new JsonObject { ["query"] = path }
            : piId is "web_fetch"
                ? new JsonObject { ["url"] = path }
                : piId is "grep"
                    ? new JsonObject { ["query"] = path }
                    : piId is "glob"
                        ? new JsonObject { ["pattern"] = path }
                        : new JsonObject { ["op"] = op, ["path"] = path };
        return factory.Create(piId, "1.0.0", args);
    }

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
            ["mode"] = "run",
            ["executable"] = executable,
            ["arguments"] = new JsonArray(argvList.Select(a => JsonValue.Create(a)).ToArray()),
            ["working_directory"] = ".",
        };
        if (timeoutMs is not null)
        {
            arguments["timeout_ms"] = timeoutMs.Value;
        }

        return factory.Create("bash", "1.0.0", arguments);    }

    private static PolicyContext Shell(PolicyContextFactory factory, string script, int? timeoutMs = null)
    {
        var arguments = new JsonObject
        {
            ["mode"] = "shell",
            ["script"] = script,
            ["working_directory"] = ".",
        };
        if (timeoutMs is not null)
        {
            arguments["timeout_ms"] = timeoutMs.Value;
        }

        return factory.Create("bash", "1.0.0", arguments);
    }

    private static void AssertDeny(PolicyEvaluation evaluation, string reasonCode)
    {
        Assert.Equal("deny", evaluation.Decision);
        Assert.Equal(reasonCode, evaluation.ReasonCode);
        Assert.Null(evaluation.MatchedRuleId);
    }

    [Theory]
    [InlineData("standard", true, "ask")]
    [InlineData("full_access", true, "allow")]
    [InlineData("standard", false, "ask")]
    [InlineData("full_access", false, "allow")]
    public void Git_output_target_outside_the_workspace_uses_profile_policy(
        string preset,
        bool joinedArgument,
        string expectedDecision)
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.Empty(preset));
        var outside = Path.Combine(Path.GetTempPath(), "ru-outside-" + Guid.NewGuid().ToString("N"), "leak.txt");
        var argv = new List<string>();
        if (joinedArgument)
        {
            argv.AddRange(["diff", "--no-index", "--output=" + outside]);
        }
        else
        {
            argv.AddRange(["log", "--output", outside]);
        }

        var evaluation = new PolicySnapshotEvaluator().Evaluate(
            lease.Snapshot,
            Terminal(factory, "git", argv.Cast<object>().ToArray()),
            PolicyRequestInvariants.Valid,
            Now);

        Assert.Equal(expectedDecision, evaluation.Decision);
    }

    [Fact]
    public void Git_output_target_inside_the_workspace_still_resolves()
    {
        var (_, factory) = Bind(PolicyEvalSnapshots.Empty());

        // The bounded in-workspace form the analyzer already documents must keep
        // working: this is the export case, not the escape.
        var context = Terminal(
            factory,
            "git",
            "diff",
            "--no-index",
            "--output=export.txt",
            Path.Combine(LeaseRoot, "src"),
            Path.Combine(LeaseRoot, "src"));

        Assert.Equal(TerminalOperationClass.GitMutation, context.TerminalAnalysis!.OperationClass);
    }

    [Fact]
    public void Non_git_commands_keep_their_own_output_arguments()
    {
        var (_, factory) = Bind(PolicyEvalSnapshots.Empty());

        // The rule is Git-specific: another tool's `--output` is not a filesystem
        // target in the same sense, and narrowing it here would be a guess.
        var context = Terminal(factory, "dotnet", "build", "--output", @"C:\elsewhere");

        Assert.NotNull(context);
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

    private static (BrokerRunLease Lease, PolicyContextFactory Factory) Bind(BrokerPolicySnapshotWire snapshot) =>
        Bind(snapshot, LeaseRoot);

    private static (BrokerRunLease Lease, PolicyContextFactory Factory) Bind(
        BrokerPolicySnapshotWire snapshot,
        string workspaceRoot)
    {
        var lease = BrokerRunLease.CreateValidated(
            PolicyEvalSnapshots.RunId,
            PolicyEvalSnapshots.SessionId,
            PolicyEvalSnapshots.WorkspaceId,
            workspaceRoot,
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

    public static BrokerPolicySnapshotWire Empty(string preset = "standard") => Snapshot(1, preset, []);

    public static BrokerPolicySnapshotWire FullAccessEmpty(long version = 1) => Snapshot(version, "full_access", []);

    public static BrokerPolicySnapshotWire FullAccessWith(params BrokerPolicyRuleWire[] rules) =>
        Snapshot(1, "full_access", rules);

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
