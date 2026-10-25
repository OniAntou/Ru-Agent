using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Policy.Commands;
using RuAgent.Broker.Runtime;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using Xunit;
using BrokerRunLease = RuAgent.Broker.Runtime.BrokerRunLease;

namespace RuAgent.Broker.Tests;

public sealed class TerminalIntentClassifierTests
{
    private readonly TerminalIntentClassifier _classifier = new();

    [Fact]
    public void Remembered_shell_intent_matches_powerShell_identity_without_allowing_changed_calls()
    {
        using var workspace = new TemporaryWorkspace();
        var factory = CreateFactory(workspace.Root);
        const string script = "Write-Output 'session approval'";
        var args = new JsonObject { ["script"] = script };
        var context = factory.Create("powershell", "1.0.0", args);
        var now = DateTimeOffset.UtcNow;
        var rule = new PolicyRuleSpec("rule_" + new string('1', 32), "run", "allow",
            new MatcherSpec("bash", "1.0.0", null, context.ResolvedExecutable,
                ["-NoProfile", "-NonInteractive", "-Command", script], context.CanonicalCwd,
                "terminal", "none"), 1, now.AddMinutes(30));

        Assert.True(PolicyRuleMatcher.Matches(rule, context, now));
        args["script"] = "Write-Output 'another command'";
        var changed = factory.Create("powershell", "1.0.0", args);
        Assert.False(PolicyRuleMatcher.Matches(rule, changed, now));
        Assert.NotEqual(context.IntentHash, changed.IntentHash);
        Assert.False(PolicyRuleMatcher.Matches(rule, context with { CanonicalCwd = workspace.Outside }, now));
        Assert.False(PolicyRuleMatcher.Matches(rule, context with { NetworkIntent = "declared" }, now));
        Assert.False(PolicyRuleMatcher.Matches(rule, context, now.AddMinutes(31)));
        var defaultShell = factory.Create("powershell", "1.0.0", args).ResolvedExecutable!;
        Assert.True(Path.IsPathFullyQualified(defaultShell));
        Assert.Equal("powershell", Path.GetFileNameWithoutExtension(defaultShell), ignoreCase: true);
    }

    [Theory]
    [InlineData("Write-Output 'a*b'", "Write-Output 'axb'")]
    [InlineData("Write-Output 'A'", "Write-Output 'a'")]
    public void Remembered_shell_script_is_literal_and_case_sensitive(string script, string different)
    {
        using var workspace = new TemporaryWorkspace();
        var factory = CreateFactory(workspace.Root);
        var args = new JsonObject { ["script"] = script };
        var context = factory.Create("powershell", "1.0.0", args);
        var now = DateTimeOffset.UtcNow;
        var rule = new PolicyRuleSpec("rule_" + new string('1', 32), "run", "allow",
            new MatcherSpec("bash", "1.0.0", null, "powershell", context.Argv,
                context.CanonicalCwd, "terminal", "none"), 1, null);
        Assert.True(PolicyRuleMatcher.Matches(rule, context, now));
        args["script"] = different;
        Assert.False(PolicyRuleMatcher.Matches(rule, factory.Create("powershell", "1.0.0", args), now));
        Assert.False(PolicyRuleMatcher.Matches(rule, context with { Argv = [.. context.Argv, "extra"] }, now));
        var restriction = rule with { Matcher = rule.Matcher with
        {
            ArgvPattern = ["-NoProfile", "-NonInteractive", "-Command", "**"],
        } };
        Assert.True(PolicyRuleMatcher.Matches(restriction with { Decision = "deny" }, context, now));
        Assert.True(PolicyRuleMatcher.Matches(restriction with { Decision = "ask" }, context, now));
    }

    [Fact]
    public void Remembered_flag_first_run_process_allow_rejects_appended_arguments()
    {
        using var workspace = new TemporaryWorkspace();
        var context = Terminal(CreateFactory(workspace.Root), "node", "-e", "console.log('reviewed')");
        var now = DateTimeOffset.UtcNow;
        var rule = new PolicyRuleSpec("rule_" + new string('2', 32), "project", "allow",
            new MatcherSpec("run_process", "1.0.0", null, context.ResolvedExecutable,
                context.Argv, context.CanonicalCwd, "terminal", "none"), 1, null);

        Assert.True(PolicyRuleMatcher.Matches(rule, context, now));
        Assert.False(PolicyRuleMatcher.Matches(
            rule,
            context with { Argv = [.. context.Argv, "--require", "unexpected.js"] },
            now));
    }

    [Fact]
    public void Remembered_windows_slash_flag_run_process_allow_rejects_appended_arguments()
    {
        using var workspace = new TemporaryWorkspace();
        var context = Terminal(CreateFactory(workspace.Root), "cmd.exe", "/d", "/c", "echo", "fixture");
        var now = DateTimeOffset.UtcNow;
        var rule = new PolicyRuleSpec("rule_" + new string('5', 32), "project", "allow",
            new MatcherSpec("run_process", "1.0.0", null, context.ResolvedExecutable,
                context.Argv, context.CanonicalCwd, "terminal", "none"), 1, null);

        Assert.True(PolicyRuleMatcher.Matches(rule, context, now));
        Assert.False(PolicyRuleMatcher.Matches(
            rule,
            context with { Argv = [.. context.Argv, "&", "whoami"] },
            now));
    }

    [Fact]
    public void Remembered_run_process_shell_wrapper_allow_rejects_appended_arguments()
    {
        using var workspace = new TemporaryWorkspace();
        const string script = "Write-Output 'reviewed script'";
        var context = Terminal(
            CreateFactory(workspace.Root),
            "powershell",
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            script);
        var now = DateTimeOffset.UtcNow;
        var rule = new PolicyRuleSpec("rule_" + new string('3', 32), "project", "allow",
            new MatcherSpec("run_process", "1.0.0", null, context.ResolvedExecutable,
                context.Argv, context.CanonicalCwd, "terminal", "none"), 1, null);

        Assert.True(PolicyRuleMatcher.Matches(rule, context, now));
        Assert.False(PolicyRuleMatcher.Matches(
            rule,
            context with { Argv = [.. context.Argv, "-EncodedCommand", "unexpected"] },
            now));
    }

    [Fact]
    public void Deny_and_ask_rules_keep_broad_argv_matching_for_executable_aliases()
    {
        using var workspace = new TemporaryWorkspace();
        var context = Terminal(CreateFactory(workspace.Root), "node", "-e", "console.log('reviewed')");
        var now = DateTimeOffset.UtcNow;
        var appended = context with { Argv = [.. context.Argv, "--require", "unexpected.js"] };

        foreach (var executableAlias in new[] { "node", context.ResolvedExecutable! })
        {
            var restriction = new PolicyRuleSpec("rule_" + new string('4', 32), "project", "deny",
                new MatcherSpec("run_process", "1.0.0", null, executableAlias,
                    ["-e", "**"], context.CanonicalCwd, "terminal", "none"), 1, null);

            Assert.True(PolicyRuleMatcher.Matches(restriction, appended, now));
            Assert.True(PolicyRuleMatcher.Matches(restriction with { Decision = "ask" }, appended, now));
        }
    }

    [Theory]
    [InlineData("git", "push")]
    [InlineData("python", "-c", "print('a;b|c')")]
    [InlineData("node", "-e", "console.log('hi')")]
    [InlineData("pip", "install", "python-pptx")]
    [InlineData("tool.exe", "--interactive")]
    public void Workspace_launches_do_not_depend_on_language_command_or_argument_syntax(
        string executable, params string[] arguments)
    {
        using var workspace = new TemporaryWorkspace();
        var context = Terminal(CreateFactory(workspace.Root), executable, arguments);
        Assert.Equal(new TerminalIntentClassification(true, "workspace_terminal_allow"), _classifier.Classify(context));
        Assert.Equal(new TerminalIntentClassification(true, "workspace_terminal_allow"),
            _classifier.Classify(context with { NetworkDeclared = true, NetworkIntent = "egress" }));
    }

    [Fact]
    public void Workspace_shell_is_allowed_without_parsing_its_text()
    {
        using var workspace = new TemporaryWorkspace();
        Assert.True(_classifier.Classify(Shell(CreateFactory(workspace.Root), "Get-Date; Write-Output 'a|b'")).IsWorkspaceLaunch);
    }

    [Fact]
    public void External_launch_directory_requires_approval()
    {
        using var workspace = new TemporaryWorkspace();
        var context = TerminalAt(CreateFactory(workspace.Root), workspace.Outside, "git", "status");
        Assert.Equal(new TerminalIntentClassification(false, "terminal_external_cwd_requires_approval"), _classifier.Classify(context));
    }

    [Fact]
    public void Protected_launch_directory_has_its_own_approval_reason()
    {
        using var workspace = new TemporaryWorkspace();
        var context = Terminal(CreateFactory(workspace.Root), "git", "status");
        context = context with { WorkingDirectory = context.WorkingDirectory! with
            { Classification = RuAgent.Broker.Tools.Paths.PathClassification.Protected } };
        Assert.Equal(new TerminalIntentClassification(false, "terminal_protected_path_requires_approval"), _classifier.Classify(context));
    }

    [Fact]
    public void Missing_or_mismatched_launch_directory_is_not_authority()
    {
        using var workspace = new TemporaryWorkspace();
        var context = Terminal(CreateFactory(workspace.Root), "git", "status");
        Assert.False(_classifier.Classify(context with { WorkingDirectory = null }).IsWorkspaceLaunch);
        Assert.False(_classifier.Classify(context with { CanonicalCwd = workspace.Outside }).IsWorkspaceLaunch);
    }

    private static PolicyContextFactory CreateFactory(string root) =>
        CreateFactory(Bind(root).Lease);

    private static PolicyContextFactory CreateFactory(BrokerRunLease lease) =>
        new(lease);

    private static (BrokerRunLease Lease, PolicyContextFactory Factory) Bind(string root, string preset = BrokerPolicyContracts.StandardPresetWireValue)
    {
        var snapshot = new BrokerPolicySnapshotWire(
            1,
            preset,
            new BrokerPolicyWorkspaceIdentityWire(
                "ws_33333333333333333333333333333333",
                "VOL-CLASSIFIER",
                "fid-classifier",
                "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"),
            [],
            DateTimeOffset.UtcNow.ToString("O"));
        var lease = BrokerRunLease.CreateValidated(
            "run_55555555555555555555555555555555",
            "session-classifier",
            "ws_33333333333333333333333333333333",
            root,
            "VOL-CLASSIFIER",
            "fid-classifier",
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            snapshot.Preset,
            snapshot.Version,
            BrokerPolicyContracts.ComputeCanonicalSnapshotHash(snapshot),
            DateTimeOffset.UtcNow.AddMinutes(5),
            snapshot);
        return (lease, new PolicyContextFactory(lease));
    }

    private static PolicyContext Terminal(
        PolicyContextFactory factory,
        string executable,
        params string[] argv) =>
        TerminalAt(factory, ".", executable, argv);

    private static PolicyContext TerminalAt(
        PolicyContextFactory factory,
        string workingDirectory,
        string executable,
        params string[] argv) =>
        factory.Create(
            "run_process",
            "1.0.0",
            new JsonObject
            {
                ["executable"] = executable,
                ["arguments"] = new JsonArray(argv.Select(argument => (JsonNode?)JsonValue.Create(argument)).ToArray()),
                ["working_directory"] = workingDirectory,
            });

    private static PolicyContext Shell(PolicyContextFactory factory, string script) =>
        factory.Create(
            "powershell",
            "1.0.0",
            new JsonObject
            {
                ["script"] = script,
                ["working_directory"] = ".",
            });

    private sealed class TemporaryWorkspace : IDisposable
    {
        private readonly string _parent;

        public TemporaryWorkspace()
        {
            _parent = Path.Combine(Path.GetTempPath(), "ru-terminal-classifier-" + Guid.NewGuid().ToString("N"));
            Root = Path.Combine(_parent, "workspace");
            Outside = Path.Combine(_parent, "outside");
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Outside);
        }

        public string Root { get; }
        public string Outside { get; }

        public void Dispose()
        {
            if (Directory.Exists(_parent))
            {
                Directory.Delete(_parent, recursive: true);
            }
        }
    }
}
