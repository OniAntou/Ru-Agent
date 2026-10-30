using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Runtime;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Broker.Tests;

/// <summary>
/// An allow rule for mcp call/list_tools, computer request_access/focus and code_intelligence is honored
/// only when it names the executable and argv it authorizes. Terminal allows are not guarded (a cwd-only
/// rule is an explicit decision), and deny and ask rules keep their identity-less wildcard semantics.
/// </summary>
public sealed class ApprovalIdentityGuardTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Identity_less_allow_rule_is_not_honored_for_an_mcp_call()
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.Rule("r-any", "allow", PolicyEvalSnapshots.Matcher())));

        var evaluation = Evaluate(lease, factory, "mcp", new JsonObject
        {
            ["op"] = "call", ["server"] = "github", ["tool"] = "delete_repo",
        });

        Assert.Equal("ask", evaluation.Decision);
        Assert.Null(evaluation.MatchedRuleId);
    }

    [Fact]
    public void Identity_less_allow_rule_is_not_honored_for_a_computer_app_request()
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.Rule("r-any", "allow", PolicyEvalSnapshots.Matcher())));

        var evaluation = Evaluate(lease, factory, "computer", new JsonObject
        {
            ["action"] = "request_access", ["apps"] = new JsonArray("notepad.exe"),
        });

        Assert.Equal("ask", evaluation.Decision);
        Assert.Null(evaluation.MatchedRuleId);
    }

    [Fact]
    public void Cwd_only_terminal_allow_rule_is_still_honored()
    {
        // Terminal allows are not identity-guarded: a user-authored rule scoped to the workspace cwd
        // is an explicit decision and must keep working.
        var root = WorkspaceRoot();
        var arguments = new JsonObject
        {
            ["executable"] = "git", ["arguments"] = new JsonArray("status"), ["working_directory"] = ".",
        };
        var (_, probeFactory) = Bind(PolicyEvalSnapshots.Empty("standard"), root);
        var probe = probeFactory.Create("run_process", "1.0.0", arguments);
        var (lease, factory) = Bind(PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.Rule("r-cwd", "allow", PolicyEvalSnapshots.Matcher(
                toolId: "run_process", cwdPattern: probe.CanonicalCwd))), root);

        var evaluation = Evaluate(lease, factory, "run_process", arguments);

        Assert.Equal("allow", evaluation.Decision);
        Assert.Equal("r-cwd", evaluation.MatchedRuleId);
    }

    [Fact]
    public void Path_only_allow_rule_is_not_honored_for_a_code_intelligence_target()
    {
        // code_intelligence resolves a workspace Target, so a path-only allow rule would
        // otherwise match it and authorize a language-server launch without approval.
        var (lease, factory) = BindWithLanguageServer(PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.Rule("r-path", "allow", PolicyEvalSnapshots.Matcher(pathPattern: "src/Example.cs"))));

        var evaluation = Evaluate(lease, factory, "code_intelligence", CodeIntelligenceArguments());

        Assert.Equal("ask", evaluation.Decision);
        Assert.Equal("lsp_launch_requires_approval", evaluation.ReasonCode);
        Assert.Null(evaluation.MatchedRuleId);
    }

    [Fact]
    public void Identity_less_deny_rule_still_denies_a_code_intelligence_target()
    {
        var (lease, factory) = BindWithLanguageServer(PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.Rule("r-deny", "deny", PolicyEvalSnapshots.Matcher(toolId: "code_intelligence"))));

        var evaluation = Evaluate(lease, factory, "code_intelligence", CodeIntelligenceArguments());

        Assert.Equal("deny", evaluation.Decision);
        Assert.Equal("r-deny", evaluation.MatchedRuleId);
    }

    [Fact]
    public void Identity_bound_allow_rule_is_honored_for_an_mcp_call()
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.Rule("r-mcp", "allow", PolicyEvalSnapshots.Matcher(
                toolId: "mcp", resolvedExecutable: "mcp:github", argvPattern: ["call", "create_issue"]))));

        var evaluation = Evaluate(lease, factory, "mcp", new JsonObject
        {
            ["op"] = "call", ["server"] = "github", ["tool"] = "create_issue",
        });

        Assert.Equal("allow", evaluation.Decision);
        Assert.Equal("rule_matched", evaluation.ReasonCode);
        Assert.Equal("r-mcp", evaluation.MatchedRuleId);
    }

    [Fact]
    public void Identity_bound_allow_rule_is_honored_for_a_computer_app_request()
    {
        // The matcher is the one the Broker plan remembers for this exact app list.
        var root = WorkspaceRoot();
        var arguments = new JsonObject { ["action"] = "request_access", ["apps"] = new JsonArray("notepad.exe") };
        var (_, probeFactory) = Bind(PolicyEvalSnapshots.Empty("standard"), root);
        var probe = probeFactory.Create("computer", "1.0.0", arguments);
        var plan = ApprovalRememberPolicy.Build(probe, "computer", "1.0.0", null, externalDirectoryRequested: false);
        Assert.NotNull(plan.SessionMatcher);

        var (lease, factory) = Bind(PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.Rule("r-computer", "allow", plan.SessionMatcher!)), root);

        var evaluation = Evaluate(lease, factory, "computer", arguments);

        Assert.Equal("allow", evaluation.Decision);
        Assert.Equal("r-computer", evaluation.MatchedRuleId);
    }

    [Fact]
    public void Identity_bound_allow_rule_is_honored_for_a_code_intelligence_target()
    {
        var (lease, factory) = BindWithLanguageServer(PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.Rule("r-lsp", "allow", PolicyEvalSnapshots.Matcher(
                toolId: "code_intelligence",
                pathPattern: "src/Example.cs",
                resolvedExecutable: "csharp-ls.exe",
                argvPattern: ["lsp", "csharp", "definition", "--stdio", "--flag"],
                cwdPattern: null,
                operationClass: "mutation"))));

        var evaluation = Evaluate(lease, factory, "code_intelligence", CodeIntelligenceArguments());

        Assert.Equal("allow", evaluation.Decision);
        Assert.Equal("r-lsp", evaluation.MatchedRuleId);
    }

    [Fact]
    public void Identity_bound_allow_rule_is_honored_for_a_terminal_run()
    {
        var root = WorkspaceRoot();
        var arguments = new JsonObject
        {
            ["executable"] = "git", ["arguments"] = new JsonArray("status"), ["working_directory"] = ".",
        };
        var (_, probeFactory) = Bind(PolicyEvalSnapshots.Empty("standard"), root);
        var probe = probeFactory.Create("run_process", "1.0.0", arguments);
        var (lease, factory) = Bind(PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.Rule("r-git", "allow", PolicyEvalSnapshots.Matcher(
                toolId: "run_process",
                resolvedExecutable: probe.ResolvedExecutable,
                argvPattern: probe.Argv,
                cwdPattern: probe.CanonicalCwd,
                operationClass: "terminal",
                networkIntent: "none"))), root);

        var evaluation = Evaluate(lease, factory, "run_process", arguments);

        Assert.Equal("allow", evaluation.Decision);
        Assert.Equal("r-git", evaluation.MatchedRuleId);
    }

    [Fact]
    public void Identity_less_deny_rule_still_denies_an_mcp_call()
    {
        var (lease, factory) = Bind(PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.Rule("r-deny", "deny", PolicyEvalSnapshots.Matcher())));

        var evaluation = Evaluate(lease, factory, "mcp", new JsonObject
        {
            ["op"] = "call", ["server"] = "github", ["tool"] = "delete_repo",
        });

        Assert.Equal("deny", evaluation.Decision);
        Assert.Equal("rule_denied", evaluation.ReasonCode);
        Assert.Equal("r-deny", evaluation.MatchedRuleId);
    }

    [Fact]
    public void Identity_less_allow_rule_still_honors_a_non_process_tool()
    {
        // The guard is scoped to process-identity tools: a read carries no executable or argv,
        // so its allow rule keeps the existing matcher semantics.
        var (lease, factory) = Bind(PolicyEvalSnapshots.SnapshotWith(
            PolicyEvalSnapshots.Rule("r-any", "allow", PolicyEvalSnapshots.Matcher())));

        var evaluation = Evaluate(lease, factory, "read_file", new JsonObject { ["path"] = "src\\Example.cs" });

        Assert.Equal("allow", evaluation.Decision);
        Assert.Equal("r-any", evaluation.MatchedRuleId);
    }

    private static PolicyEvaluation Evaluate(
        RuAgent.Broker.Runtime.BrokerRunLease lease,
        PolicyContextFactory factory,
        string toolId,
        JsonObject arguments) =>
        new PolicySnapshotEvaluator().Evaluate(
            lease.Snapshot,
            factory.Create(toolId, "1.0.0", arguments),
            PolicyRequestInvariants.Valid,
            Now);

    private static JsonObject CodeIntelligenceArguments() => new()
    {
        ["operation"] = "definition", ["language"] = "csharp", ["path"] = "src\\Example.cs", ["line"] = 2, ["character"] = 4,
    };

    private static (RuAgent.Broker.Runtime.BrokerRunLease Lease, PolicyContextFactory Factory) Bind(
        BrokerPolicySnapshotWire snapshot,
        string? workspaceRoot = null)
    {
        var root = workspaceRoot ?? WorkspaceRoot();
        var lease = RuAgent.Broker.Runtime.BrokerRunLease.CreateValidated(
            PolicyEvalSnapshots.RunId,
            PolicyEvalSnapshots.SessionId,
            PolicyEvalSnapshots.WorkspaceId,
            root,
            PolicyEvalSnapshots.VolumeSerial,
            PolicyEvalSnapshots.RootFileId,
            PolicyEvalSnapshots.DisplayHash,
            snapshot.Preset,
            snapshot.Version,
            BrokerPolicyContracts.ComputeCanonicalSnapshotHash(snapshot),
            DateTimeOffset.UtcNow.AddMinutes(5),
            snapshot);
        return (lease, new PolicyContextFactory(lease));
    }

    private static (RuAgent.Broker.Runtime.BrokerRunLease Lease, PolicyContextFactory Factory) BindWithLanguageServer(
        BrokerPolicySnapshotWire snapshot)
    {
        var root = WorkspaceRoot();
        var profile = Path.Combine(root, "profile");
        Directory.CreateDirectory(Path.Combine(profile, ".ru"));
        File.WriteAllText(Path.Combine(profile, ".ru", "lsp.json"),
            """{"servers":{"csharp":{"command":"csharp-ls.exe","args":["--stdio","--flag"]}}}""");
        var lease = RuAgent.Broker.Runtime.BrokerRunLease.CreateValidated(
            PolicyEvalSnapshots.RunId,
            PolicyEvalSnapshots.SessionId,
            PolicyEvalSnapshots.WorkspaceId,
            root,
            PolicyEvalSnapshots.VolumeSerial,
            PolicyEvalSnapshots.RootFileId,
            PolicyEvalSnapshots.DisplayHash,
            snapshot.Preset,
            snapshot.Version,
            BrokerPolicyContracts.ComputeCanonicalSnapshotHash(snapshot),
            DateTimeOffset.UtcNow.AddMinutes(5),
            snapshot);
        return (lease, new PolicyContextFactory(lease, userHome: () => profile));
    }

    private static string WorkspaceRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-approval-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        return root;
    }
}
