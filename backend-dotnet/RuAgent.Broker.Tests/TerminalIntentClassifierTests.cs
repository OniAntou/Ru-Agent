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
    [InlineData("git", "status")]
    [InlineData("dotnet", "test")]
    public void Recognized_local_commands_are_bounded(string executable, string argument)
    {
        using var workspace = new TemporaryWorkspace();
        var factory = CreateFactory(workspace.Root);

        var result = _classifier.Classify(Terminal(factory, executable, argument));

        Assert.Equal(new TerminalIntentClassification(true, "bounded_local"), result);
    }

    [Theory]
    [InlineData("git", "--version")]
    [InlineData("python", "--version")]
    [InlineData("dotnet", "--version")]
    public void Diagnostics_flags_are_bounded_without_asking(string executable, string flag)
    {
        using var workspace = new TemporaryWorkspace();
        var factory = CreateFactory(workspace.Root);

        var result = _classifier.Classify(Terminal(factory, executable, flag));

        Assert.Equal(new TerminalIntentClassification(true, "bounded_local"), result);
    }

    [Fact]
    public void Git_directory_override_inside_workspace_follows_the_inner_verb()
    {
        using var workspace = new TemporaryWorkspace();
        var factory = CreateFactory(workspace.Root);
        Directory.CreateDirectory(Path.Combine(workspace.Root, "subdir"));

        var result = _classifier.Classify(Terminal(factory, "git", "-C", "subdir", "status"));

        Assert.Equal(new TerminalIntentClassification(true, "bounded_local"), result);
    }

    [Fact]
    public void Git_directory_override_outside_workspace_still_asks()
    {
        using var workspace = new TemporaryWorkspace();
        var factory = CreateFactory(workspace.Root);

        var result = _classifier.Classify(Terminal(factory, "git", "-C", workspace.Outside, "status"));

        Assert.Equal(
            new TerminalIntentClassification(false, "git_directory_override_requires_approval"),
            result);
    }

    [Fact]
    public void Git_directory_override_with_network_verb_still_asks()
    {
        using var workspace = new TemporaryWorkspace();
        var factory = CreateFactory(workspace.Root);
        Directory.CreateDirectory(Path.Combine(workspace.Root, "subdir"));

        var result = _classifier.Classify(Terminal(factory, "git", "-C", "subdir", "push"));

        Assert.Equal(
            new TerminalIntentClassification(false, "terminal_external_mutation_requires_approval"),
            result);
    }

    [Fact]
    public void Arbitrary_shell_mode_is_not_bounded_local()
    {
        using var workspace = new TemporaryWorkspace();
        var factory = CreateFactory(workspace.Root);

        var result = _classifier.Classify(Shell(factory, "Get-Date"));

        Assert.Equal(
            new TerminalIntentClassification(false, "terminal_unknown_executable"),
            result);
    }

    [Fact]
    public void Network_executable_is_not_bounded_local()
    {
        using var workspace = new TemporaryWorkspace();
        var factory = CreateFactory(workspace.Root);

        var result = _classifier.Classify(Terminal(factory, "git", "push"));

        Assert.Equal(
            new TerminalIntentClassification(false, "terminal_external_mutation_requires_approval"),
            result);
    }

    [Fact]
    public void Unknown_executable_is_not_bounded_local()
    {
        using var workspace = new TemporaryWorkspace();
        var factory = CreateFactory(workspace.Root);

        var result = _classifier.Classify(Terminal(factory, "mystery-tool.exe"));

        Assert.Equal(
            new TerminalIntentClassification(false, "terminal_unknown_executable"),
            result);
    }

    [Fact]
    public void External_working_directory_is_not_bounded_local()
    {
        using var workspace = new TemporaryWorkspace();
        var factory = CreateFactory(workspace.Root);

        var result = _classifier.Classify(TerminalAt(
            factory,
            workspace.Outside,
            "git",
            "status"));

        Assert.Equal(
            new TerminalIntentClassification(false, "terminal_external_cwd_requires_approval"),
            result);
    }

    [Fact]
    public void External_argument_path_is_not_bounded_local()
    {
        using var workspace = new TemporaryWorkspace();
        var factory = CreateFactory(workspace.Root);
        var outsideFile = Path.Combine(workspace.Outside, "outside.csproj");

        var result = _classifier.Classify(Terminal(factory, "dotnet", "test", outsideFile));

        Assert.Equal(
            new TerminalIntentClassification(false, "terminal_external_argv_requires_approval"),
            result);
    }

    [Fact]
    public void Protected_argument_path_names_the_protected_reason()
    {
        using var workspace = new TemporaryWorkspace();
        var factory = CreateFactory(workspace.Root);
        var secret = Path.Combine(workspace.Root, ".env");

        var result = _classifier.Classify(Terminal(factory, "dotnet", "test", secret));

        Assert.Equal(
            new TerminalIntentClassification(false, "terminal_protected_path_requires_approval"),
            result);
    }

    [Fact]
    public void Protected_working_directory_names_the_protected_reason()
    {
        using var workspace = new TemporaryWorkspace();
        var factory = CreateFactory(workspace.Root);
        var protectedDirectory = Path.Combine(workspace.Outside, ".git");
        Directory.CreateDirectory(protectedDirectory);

        var result = _classifier.Classify(TerminalAt(
            factory,
            protectedDirectory,
            "git",
            "status"));

        Assert.Equal(
            new TerminalIntentClassification(false, "terminal_protected_path_requires_approval"),
            result);
    }

    [Fact]
    public void External_executable_path_is_not_bounded_local()
    {
        using var workspace = new TemporaryWorkspace();
        var factory = CreateFactory(workspace.Root);
        var executable = Path.Combine(workspace.Outside, "git.exe");

        var result = _classifier.Classify(Terminal(factory, executable, "status"));

        Assert.Equal(
            new TerminalIntentClassification(false, "terminal_external_executable_requires_approval"),
            result);
    }

    [Fact]
    public void Attached_external_argument_path_is_not_bounded_local()
    {
        using var workspace = new TemporaryWorkspace();
        var factory = CreateFactory(workspace.Root);
        var output = Path.Combine(workspace.Outside, "output");

        var result = _classifier.Classify(Terminal(factory, "dotnet", "build", "-o" + output));

        Assert.Equal(
            new TerminalIntentClassification(false, "terminal_external_argv_requires_approval"),
            result);
    }

    [Fact]
    public void Direct_delete_is_not_bounded_local()
    {
        using var workspace = new TemporaryWorkspace();
        var factory = CreateFactory(workspace.Root);

        var result = _classifier.Classify(Terminal(factory, "rm", "old.txt"));

        Assert.Equal(
            new TerminalIntentClassification(false, "terminal_delete_requires_approval"),
            result);
    }

    [Fact]
    public void Arbitrary_script_is_not_bounded_local()
    {
        using var workspace = new TemporaryWorkspace();
        var factory = CreateFactory(workspace.Root);

        var result = _classifier.Classify(Terminal(factory, "python", "script.py"));

        Assert.Equal(
            new TerminalIntentClassification(false, "terminal_external_mutation_requires_approval"),
            result);
    }

    [Theory]
    [InlineData("|")]
    [InlineData(">")]
    [InlineData("&&")]
    public void Unparsed_shell_syntax_is_not_bounded_local(string syntax)
    {
        using var workspace = new TemporaryWorkspace();
        var factory = CreateFactory(workspace.Root);

        var result = _classifier.Classify(Terminal(factory, "rg", "pattern", syntax));

        Assert.Equal(
            new TerminalIntentClassification(false, "terminal_unparsed_shell_syntax_requires_approval"),
            result);
    }

    private static PolicyContextFactory CreateFactory(string root) =>
        CreateFactory(Bind(root).Lease);

    private static PolicyContextFactory CreateFactory(BrokerRunLease lease) =>
        new(lease);

    private static (BrokerRunLease Lease, PolicyContextFactory Factory) Bind(string root)
    {
        var snapshot = new BrokerPolicySnapshotWire(
            1,
            BrokerPolicyContracts.StandardPresetWireValue,
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
