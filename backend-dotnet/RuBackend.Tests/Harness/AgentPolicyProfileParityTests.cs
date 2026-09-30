extern alias Broker;

using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using BrokerPolicy = Broker::RuAgent.Broker.Policy;
using BrokerRuntime = Broker::RuAgent.Broker.Runtime;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentPolicyProfileParityTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly AgentPolicySubject[] Subjects = Enum.GetValues<AgentPolicySubject>();

    [Fact]
    public void Prompt_projection_and_broker_evaluator_share_the_canonical_profile()
    {
        foreach (var profile in AgentPolicyProfileCatalog.Profiles)
        {
            using var workspace = new TemporaryWorkspace();
            var (lease, factory) = CreateFactory(workspace, profile.Preset);
            var evaluator = new BrokerPolicy.PolicySnapshotEvaluator();
            var prompt = EffectivePolicyPromptProjector.Project(profile);

            foreach (var subject in Subjects)
            {
                var expected = profile.DecisionFor(subject);
                var expectedLine = $"{AgentPolicyProfileCatalog.Describe(subject)}={Describe(expected)}";
                Assert.Contains(expectedLine, prompt, StringComparison.Ordinal);

                var context = CreateContext(factory, subject, workspace.Outside);
                var evaluation = evaluator.Evaluate(
                    lease.Snapshot,
                    context,
                    BrokerPolicy.PolicyRequestInvariants.Valid,
                    Now);
                Assert.Equal(Describe(expected), evaluation.Decision);
            }
        }
    }

    [Fact]
    public void Typed_prompt_context_binds_the_effective_policy_to_the_canonical_profile()
    {
        // The effective policy is not an independent input on the typed path: the
        // context derives it from the preset through the catalog, so a tampered
        // policy document has no channel to reach the model or the Broker.
        var context = new SystemPromptContext(
            SystemPromptContext.PromptContextSchemaVersion,
            SystemPromptContext.RequiredAgentProfile,
            "standard",
            new PromptEnvironment("test-model", "windows", "en-US", "UTC", "2026-01-01", null),
            Array.Empty<PromptInstructionBlock>(),
            Array.Empty<PromptCapability>(),
            new PromptRuntimeContext(null, null, Array.Empty<PromptReference>()));
        var canonical = AgentPolicyProfileCatalog.Get("standard");
        Assert.True(context.EffectivePolicy.HasSameSemanticsAs(canonical));

        var serialized = JsonNode.Parse(context.SerializeUtf8())!.AsObject();
        var decoded = AgentPolicyProfileCatalog.ReadProfile(serialized["effective_policy"]!.AsObject());
        Assert.True(decoded.HasSameSemanticsAs(canonical));

        using var workspace = new TemporaryWorkspace();
        var (lease, factory) = CreateFactory(workspace, "standard");
        var evaluator = new BrokerPolicy.PolicySnapshotEvaluator();
        var subject = AgentPolicySubject.WorkspaceMutation;
        var before = evaluator.Evaluate(
            lease.Snapshot,
            CreateContext(factory, subject, workspace.Outside),
            BrokerPolicy.PolicyRequestInvariants.Valid,
            Now);
        Assert.Equal("allow", before.Decision);
        Assert.Equal(
            AgentPolicyDecision.Allow,
            AgentPolicyProfileCatalog.Get(lease.Snapshot.Preset).DecisionFor(subject));

        var after = evaluator.Evaluate(
            lease.Snapshot,
            CreateContext(factory, subject, workspace.Outside),
            BrokerPolicy.PolicyRequestInvariants.Valid,
            Now);
        Assert.Equal(before, after);
    }

    private static BrokerPolicy.PolicyContext CreateContext(
        BrokerPolicy.PolicyContextFactory factory,
        AgentPolicySubject subject,
        string outsidePath) => subject switch
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
        AgentPolicySubject.BoundedLocalTerminal => factory.Create(
            "bash",
            "1.0.0",
            new JsonObject
            {
                ["mode"] = "run",
                ["executable"] = "dotnet",
                ["arguments"] = new JsonArray("test"),
                ["working_directory"] = ".",
            }),
        AgentPolicySubject.ExternalStructuredFile => factory.Create(
            "read",
            "1.0.0",
            new JsonObject { ["path"] = outsidePath }),
        AgentPolicySubject.RiskyTerminal => factory.Create(
            "bash",
            "1.0.0",
            new JsonObject
            {
                ["mode"] = "run",
                ["executable"] = "git",
                ["arguments"] = new JsonArray("push"),
                ["working_directory"] = ".",
            }),
        AgentPolicySubject.StructuredProtectedPath => factory.Create(
            "read",
            "1.0.0",
            new JsonObject { ["path"] = ".env.local" }),
        AgentPolicySubject.HardInvalid => factory.Create(
            "bash",
            "1.0.0",
            new JsonObject
            {
                ["mode"] = "run",
                ["executable"] = "git",
                ["arguments"] = new JsonArray("status"),
                ["working_directory"] = ".",
                ["timeout_ms"] = TerminalLimits.HardMaxTimeoutMs + 1,
            }),
        _ => throw new ArgumentOutOfRangeException(nameof(subject)),
    };

    private static (BrokerRuntime.BrokerRunLease Lease, BrokerPolicy.PolicyContextFactory Factory) CreateFactory(
        TemporaryWorkspace workspace,
        string preset)
    {
        var snapshot = new BrokerPolicySnapshotWire(
            1,
            preset,
            new BrokerPolicyWorkspaceIdentityWire(
                "ws_44444444444444444444444444444444",
                "VOL-PARITY",
                "fid-parity",
                "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"),
            [],
            Now.ToString("O"));
        var lease = BrokerRuntime.BrokerRunLease.CreateValidated(
            "run_66666666666666666666666666666666",
            "session-parity",
            "ws_44444444444444444444444444444444",
            workspace.Root,
            "VOL-PARITY",
            "fid-parity",
            "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
            preset,
            snapshot.Version,
            BrokerPolicyContracts.ComputeCanonicalSnapshotHash(snapshot),
            DateTimeOffset.UtcNow.AddMinutes(5),
            snapshot);
        return (lease, new BrokerPolicy.PolicyContextFactory(lease));
    }

    private static string Describe(AgentPolicyDecision decision) => decision switch
    {
        AgentPolicyDecision.Allow => BrokerPolicyContracts.AllowWireValue,
        AgentPolicyDecision.Ask => BrokerPolicyContracts.AskWireValue,
        AgentPolicyDecision.Deny => BrokerPolicyContracts.DenyWireValue,
        _ => throw new ArgumentOutOfRangeException(nameof(decision)),
    };

    private sealed class TemporaryWorkspace : IDisposable
    {
        private readonly string _parent;

        public TemporaryWorkspace()
        {
            _parent = Path.Combine(Path.GetTempPath(), "ru-policy-parity-" + Guid.NewGuid().ToString("N"));
            Root = Path.Combine(_parent, "workspace");
            Outside = Path.Combine(_parent, "outside");
            Directory.CreateDirectory(Path.Combine(Root, "src"));
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
