extern alias Broker;

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using Xunit;
using BrokerTestHostHost = Broker::RuAgent.Broker.Runtime.BrokerHost;

namespace RuBackend.Tests.Harness;

public sealed class BrokerToolGatewayFactoryTests : IAsyncLifetime
{
    private string? _previousWorkspaceRoot;
    private string? _brokerRoot;
    private string _workspaceRoot = string.Empty;
    private string _descriptorPath = string.Empty;

    public async Task InitializeAsync()
    {
        _previousWorkspaceRoot = Environment.GetEnvironmentVariable("RU_WORKSPACE_ROOT");
        var root = Path.Combine(Path.GetTempPath(), "ru-broker-factory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _brokerRoot = root;
        _workspaceRoot = Path.Combine(root, "workspace");
        Directory.CreateDirectory(_workspaceRoot);
        Directory.CreateDirectory(Path.Combine(_workspaceRoot, "notes"));
        await File.WriteAllTextAsync(Path.Combine(_workspaceRoot, "notes", "a.txt"), "factory-leg-1d");

        var sessionRoot = Path.Combine(root, "session");
        Directory.CreateDirectory(sessionRoot);
        _descriptorPath = Path.Combine(root, "broker.json");
        var descriptor = new BrokerSessionDescriptor(
            "http://127.0.0.1:" + GetFreePort(),
            "broker-factory-" + Guid.NewGuid().ToString("N"),
            1,
            sessionRoot,
            DateTimeOffset.UtcNow.AddMinutes(30));
        await File.WriteAllTextAsync(
            _descriptorPath,
            JsonSerializer.Serialize(descriptor, BrokerSessionDescriptor.JsonOptions));
        Environment.SetEnvironmentVariable("RU_WORKSPACE_ROOT", _workspaceRoot);
        await BrokerTestHostHost.StartAsync(_descriptorPath);
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("RU_WORKSPACE_ROOT", _previousWorkspaceRoot);
        if (_brokerRoot is not null)
        {
            await Task.CompletedTask;
            TryDelete(_brokerRoot);
        }
    }

    [Fact]
    public void IsConfigured_False_WhenDescriptorPathMissing()
    {
        var factory = new global::RuBackend.Harness.Broker.BrokerToolGatewayFactory(null);
        Assert.False(factory.IsConfigured);
        var missing = new global::RuBackend.Harness.Broker.BrokerToolGatewayFactory(
            Path.Combine(Path.GetTempPath(), "ru-missing-" + Guid.NewGuid().ToString("N")));
        Assert.False(missing.IsConfigured);
    }

    [Fact]
    public async Task Create_EvaluatesReadOnlyToolThroughBrokerLease()
    {
        var factory = new global::RuBackend.Harness.Broker.BrokerToolGatewayFactory(_descriptorPath);
        Assert.True(factory.IsConfigured);
        var runId = new RunId("run_" + new string('0', 32));
        var snapshot = PolicySnapshotCanonicalizer.Create(
            1,
            AgentPermissionPreset.Standard,
            SnapshotWorkspace(),
            [],
            DateTimeOffset.UtcNow);

        var gateway = Assert.IsType<RuBackend.Harness.Broker.BrokerToolGateway>(
            factory.Create(_workspaceRoot, snapshot, runId));

        try
        {
            var decision = await gateway.EvaluateAsync(
                Proposal(runId, "files.read", new System.Text.Json.Nodes.JsonObject
                {
                    ["path"] = "notes/a.txt",
                }),
                CancellationToken.None);
            Assert.Equal(PolicyDecision.Allow, decision.Decision);
        }
        finally
        {
            factory.CloseRun(runId.Value);
        }
    }

    [Fact]
    public async Task RespondApprovalAsync_BridgesBrokerApprovalForResumedExecution()
    {
        var factory = new global::RuBackend.Harness.Broker.BrokerToolGatewayFactory(_descriptorPath);
        var runId = new RunId("run_" + new string('0', 32));
        var snapshot = PolicySnapshotCanonicalizer.Create(
            1,
            AgentPermissionPreset.Standard,
            SnapshotWorkspace(),
            [],
            DateTimeOffset.UtcNow);
        var gateway = Assert.IsType<global::RuBackend.Harness.Broker.BrokerToolGateway>(
            factory.Create(_workspaceRoot, snapshot, runId));

        var deleteArgs = new System.Text.Json.Nodes.JsonObject
        {
            ["path"] = "notes/a.txt",
        };
        var decision = await gateway.EvaluateAsync(
            Proposal(runId, "files.delete", deleteArgs),
            CancellationToken.None);
        Assert.Equal(PolicyDecision.Ask, decision.Decision);
        var hostApprovalId = decision.ApprovalId!.Value.Value;

        var (authorized, token) = await factory.RespondApprovalAsync(
            runId.Value,
            hostApprovalId,
            "allow_once",
            CancellationToken.None);
        Assert.True(authorized);
        Assert.False(string.IsNullOrWhiteSpace(token));

        var resumed = Proposal(runId, "files.delete", deleteArgs);
        var resumedDecision = await gateway.EvaluateAsync(resumed, CancellationToken.None);
        Assert.Equal(PolicyDecision.Allow, resumedDecision.Decision);
        var prepared = await gateway.PrepareAsync(
            new ApprovedToolExecution(
                resumed,
                new ExecutionId("exec_" + new string('0', 32)),
                resumedDecision.CanonicalIntentToken!),
            CancellationToken.None);
        var receipt = await gateway.ExecuteAsync(
            prepared,
            _ => ValueTask.CompletedTask,
            CancellationToken.None);
        Assert.Equal(ToolExecutionOutcome.Succeeded, receipt.Outcome);
        Assert.False(File.Exists(Path.Combine(_workspaceRoot, "notes", "a.txt")));

        factory.CloseRun(runId.Value);
    }

    [Fact]
    public async Task RespondApprovalAsync_ReturnsFalse_WhenApprovalUnknown()
    {
        var factory = new global::RuBackend.Harness.Broker.BrokerToolGatewayFactory(_descriptorPath);
        var runId = new RunId("run_" + new string('0', 32));
        var snapshot = PolicySnapshotCanonicalizer.Create(
            1,
            AgentPermissionPreset.Standard,
            SnapshotWorkspace(),
            [],
            DateTimeOffset.UtcNow);
        factory.Create(_workspaceRoot, snapshot, runId);

        var (authorized, token) = await factory.RespondApprovalAsync(
            runId.Value,
            "apr_" + new string('9', 32),
            "allow_once",
            CancellationToken.None);
        Assert.False(authorized);
        Assert.Null(token);
    }

    [Fact]
    public async Task Create_Throws_WhenSnapshotHashMissing()
    {
        var factory = new global::RuBackend.Harness.Broker.BrokerToolGatewayFactory(_descriptorPath);
        var runId = new RunId("run_" + new string('0', 32));
        var snapshot = new PolicySnapshot(
            1,
            AgentPermissionPreset.Standard,
            SnapshotWorkspace(),
            [],
            DateTimeOffset.UtcNow,
            string.Empty);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Task.FromResult(factory.Create(_workspaceRoot, snapshot, runId)));
    }

    private static WorkspaceIdentity SnapshotWorkspace() => new(
        new WorkspaceId("ws_" + new string('1', 32)),
        "VOL-FACTORY",
        "fid-factory",
        new string('a', 64));

    private static ToolProposal Proposal(RunId runId, string toolId, System.Text.Json.Nodes.JsonObject arguments) => new(
        runId,
        new ModelStepId("step_" + new string('0', 32)),
        new ToolCallId("call_" + new string('0', 32)),
        modelOrder: 1,
        toolId,
        "1.0.0",
        JsonSerializer.SerializeToElement(arguments));

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
