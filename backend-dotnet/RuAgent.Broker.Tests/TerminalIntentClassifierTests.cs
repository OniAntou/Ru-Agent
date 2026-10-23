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

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public void Remembered_shell_intent_matches_real_policy_context_without_allowing_changed_calls(string shell)
    {
        using var workspace = new TemporaryWorkspace();
        var factory = CreateFactory(workspace.Root);
        const string script = "Write-Output 'session approval'";
        var args = new JsonObject { ["mode"] = "shell", ["shell"] = shell, ["script"] = script };
        var context = factory.Create("bash", "1.0.0", args);
        var now = DateTimeOffset.UtcNow;
        var rule = new PolicyRuleSpec("rule_" + new string('1', 32), "run", "allow",
            new MatcherSpec("bash", "1.0.0", null, shell,
                ["-NoProfile", "-NonInteractive", "-Command", script], context.CanonicalCwd,
                "terminal", "none"), 1, now.AddMinutes(30));

        Assert.True(PolicyRuleMatcher.Matches(rule, context, now));
        args["script"] = "Write-Output 'another command'";
        var changed = factory.Create("bash", "1.0.0", args);
        Assert.False(PolicyRuleMatcher.Matches(rule, changed, now));
        Assert.NotEqual(context.IntentHash, changed.IntentHash);
        args["script"] = script;
        args["shell"] = shell == "powershell" ? "pwsh" : "powershell";
        Assert.False(PolicyRuleMatcher.Matches(rule, factory.Create("bash", "1.0.0", args), now));
        Assert.False(PolicyRuleMatcher.Matches(rule, context with { CanonicalCwd = workspace.Outside }, now));
        Assert.False(PolicyRuleMatcher.Matches(rule, context with { NetworkIntent = "declared" }, now));
        Assert.False(PolicyRuleMatcher.Matches(rule, context, now.AddMinutes(31)));
        args.Remove("shell");
        var defaultShell = factory.Create("bash", "1.0.0", args).ResolvedExecutable!;
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
        var args = new JsonObject { ["mode"] = "shell", ["script"] = script };
        var context = factory.Create("bash", "1.0.0", args);
        var now = DateTimeOffset.UtcNow;
        var rule = new PolicyRuleSpec("rule_" + new string('1', 32), "run", "allow",
            new MatcherSpec("bash", "1.0.0", null, "powershell", context.Argv,
                context.CanonicalCwd, "terminal", "none"), 1, null);
        Assert.True(PolicyRuleMatcher.Matches(rule, context, now));
        args["script"] = different;
        Assert.False(PolicyRuleMatcher.Matches(rule, factory.Create("bash", "1.0.0", args), now));
        Assert.False(PolicyRuleMatcher.Matches(rule, context with { Argv = [.. context.Argv, "extra"] }, now));
        var restriction = rule with { Matcher = rule.Matcher with
        {
            ArgvPattern = ["-NoProfile", "-NonInteractive", "-Command", "**"],
        } };
        Assert.True(PolicyRuleMatcher.Matches(restriction with { Decision = "deny" }, context, now));
        Assert.True(PolicyRuleMatcher.Matches(restriction with { Decision = "ask" }, context, now));
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
            "bash",
            "1.0.0",
            new JsonObject
            {
                ["mode"] = "run",
                ["executable"] = executable,
                ["arguments"] = new JsonArray(argv.Select(argument => (JsonNode?)JsonValue.Create(argument)).ToArray()),
                ["working_directory"] = workingDirectory,
            });

    private static PolicyContext Shell(PolicyContextFactory factory, string script) =>
        factory.Create(
            "bash",
            "1.0.0",
            new JsonObject
            {
                ["mode"] = "shell",
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
