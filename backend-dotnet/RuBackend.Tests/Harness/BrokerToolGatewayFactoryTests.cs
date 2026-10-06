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
    private BrokerTestHostHost? _brokerHost;
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
        _brokerHost = await BrokerTestHostHost.StartAsync(_descriptorPath);
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("RU_WORKSPACE_ROOT", _previousWorkspaceRoot);
        if (_brokerHost is not null)
        {
            await _brokerHost.DisposeAsync();
        }

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
    public void ApprovalArgsKey_canonicalizes_null_properties_without_throwing()
    {
        using var document = JsonDocument.Parse("{\"path\":null,\"nested\":{\"z\":null,\"a\":1}}");

        var key = global::RuBackend.Harness.Broker.BrokerToolGateway.ApprovalArgsKey(
            "read",
            document.RootElement);

        Assert.Equal("read|{\"nested\":{\"a\":1,\"z\":null},\"path\":null}", key);
    }

    [Theory]
    [InlineData("lease")]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("generation")]
    [InlineData("session")]
    [InlineData("root")]
    [InlineData("url")]
    [InlineData("auth")]
    public void Cached_authorization_token_is_bound_to_the_current_run_lease(
        string changedField)
    {
        var state = new global::RuBackend.Harness.Broker.BrokerApprovalState();
        var runId = "run_" + new string('0', 32);
        var lease = new global::RuBackend.Harness.Broker.BrokerRunLeaseIdentity(
            runId,
            "session-a",
            "ws_" + new string('1', 32),
            "C:\\workspace",
            4,
            new string('a', 64),
            "lease-a",
            BrokerUrl: "ws://127.0.0.1:41001/agent",
            AuthTokenSha256: new string('b', 64));
        state.ActivateLease(lease);
        state.StoreAuthorizationToken(
            runId,
            lease,
            "read|{\"path\":\"README.md\"}",
            "authorization-token",
            DateTimeOffset.UtcNow.AddMinutes(1));
        var stale = changedField switch
        {
            "lease" => lease with { LeaseId = "lease-b" },
            "version" => lease with { PolicyVersion = 5 },
            "hash" => lease with { PolicySha256 = new string('b', 64) },
            "generation" => lease with { Generation = 2 },
            "session" => lease with { SessionId = "session-b" },
            "url" => lease with { BrokerUrl = "ws://127.0.0.1:41002/agent" },
            "auth" => lease with { AuthTokenSha256 = new string('c', 64) },
            _ => lease with { CanonicalRoot = "C:\\workspace-b" },
        };

        Assert.False(state.TryTakeAuthorizationToken(
            runId,
            stale,
            "read|{\"path\":\"README.md\"}",
            out var staleToken));
        Assert.Null(staleToken);
        Assert.True(state.TryTakeAuthorizationToken(
            runId,
            lease,
            "read|{\"path\":\"README.md\"}",
            out var token));
        Assert.Equal("authorization-token", token);
        Assert.False(state.TryTakeAuthorizationToken(
            runId,
            lease,
            "read|{\"path\":\"README.md\"}",
            out _));
    }

    [Fact]
    public void Invalidating_an_old_lease_does_not_remove_a_new_lease_token()
    {
        var state = new global::RuBackend.Harness.Broker.BrokerApprovalState();
        var runId = "run_" + new string('0', 32);
        var oldLease = new global::RuBackend.Harness.Broker.BrokerRunLeaseIdentity(
            runId,
            "session-a",
            "ws_" + new string('1', 32),
            "C:\\workspace",
            4,
            new string('a', 64),
            "lease-a",
            "ws://127.0.0.1:41001/agent",
            new string('b', 64));
        var newLease = oldLease with { LeaseId = "lease-b" };
        const string argsKey = "read|{}";
        state.ActivateLease(oldLease);
        state.StoreAuthorizationToken(
            runId,
            oldLease,
            argsKey,
            "old-token",
            DateTimeOffset.UtcNow.AddMinutes(1));
        state.ActivateLease(newLease);
        state.StoreAuthorizationToken(
            runId,
            newLease,
            argsKey,
            "new-token",
            DateTimeOffset.UtcNow.AddMinutes(1));

        state.InvalidateLease(oldLease);

        Assert.False(state.TryTakeAuthorizationToken(runId, oldLease, argsKey, out _));
        Assert.True(state.TryTakeAuthorizationToken(runId, newLease, argsKey, out var token));
        Assert.Equal("new-token", token);
    }

    [Fact]
    public void Stale_generation_response_cannot_be_registered_after_reconnect()
    {
        var state = new global::RuBackend.Harness.Broker.BrokerApprovalState();
        var runId = "run_" + new string('0', 32);
        var first = new global::RuBackend.Harness.Broker.BrokerRunLeaseIdentity(
            runId,
            "session-a",
            "ws_" + new string('1', 32),
            "C:\\workspace",
            4,
            new string('a', 64),
            "lease-a",
            "ws://127.0.0.1:41001/agent",
            new string('b', 64));
        var second = first with { Generation = 1 };
        const string argsKey = "read|{\"path\":\"README.md\"}";
        state.ActivateLease(first);

        Assert.True(state.RegisterAsk(
            runId,
            first,
            "apr_current",
            "broker-current",
            argsKey));
        Assert.True(state.StoreAuthorizationToken(
            runId,
            first,
            argsKey,
            "token-current",
            DateTimeOffset.UtcNow.AddMinutes(1)));

        state.ActivateLease(second);

        Assert.False(state.RegisterAsk(
            runId,
            first,
            "apr_stale",
            "broker-stale",
            argsKey));
        Assert.False(state.StoreAuthorizationToken(
            runId,
            first,
            argsKey,
            "token-stale",
            DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.False(state.TryGetAsk(
            runId,
            first,
            "apr_current",
            out _,
            out _));
        Assert.False(state.TryTakeAuthorizationToken(runId, first, argsKey, out _));
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
                Proposal(runId, "read", new System.Text.Json.Nodes.JsonObject
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

        var externalPath = Path.Combine(Path.GetTempPath(), "ru-broker-factory-external-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(externalPath, "factory-external");
        var readArgs = new System.Text.Json.Nodes.JsonObject
        {
            ["path"] = externalPath,
        };
        var decision = await gateway.EvaluateAsync(
            Proposal(runId, "read", readArgs),
            CancellationToken.None);
        Assert.Equal(PolicyDecision.Ask, decision.Decision);
        Assert.Equal("external_directory_required", decision.SafeCode);
        var hostApprovalId = decision.ApprovalId!.Value.Value;

        var (authorized, token) = await factory.RespondApprovalAsync(
            runId.Value,
            hostApprovalId,
            "allow_session_directory",
            CancellationToken.None);
        Assert.True(authorized);
        Assert.False(string.IsNullOrWhiteSpace(token));

        var resumed = Proposal(runId, "read", readArgs);
        var resumedDecision = await gateway.EvaluateAsync(resumed, CancellationToken.None);
        Assert.Equal(PolicyDecision.Allow, resumedDecision.Decision);
        var prepared = await gateway.PrepareAsync(
            new ApprovedToolExecution(
                resumed,
                new ExecutionId("exec_" + new string('0', 32)),
                resumedDecision.CanonicalIntentToken!,
                resumedDecision.GatewayGeneration),
            CancellationToken.None);
        var receipt = await gateway.ExecuteAsync(
            prepared,
            _ => ValueTask.CompletedTask,
            CancellationToken.None);
        // Session grants open execution: the approved external read runs
        // against the granted directory and returns its content.
        Assert.Equal(ToolExecutionOutcome.Succeeded, receipt.Outcome);
        var output = System.Text.Json.JsonDocument.Parse(
            System.Text.Encoding.UTF8.GetString(receipt.ModelContent.Span)).RootElement;
        Assert.Equal("factory-external", output.GetProperty("content").GetString());
        try { File.Delete(externalPath); } catch { }

        factory.CloseRun(runId.Value);
    }

    [Fact]
    public async Task Create_ReusesLeaseStateButRotatesItWhenSnapshotChanges()
    {
        var factory = new global::RuBackend.Harness.Broker.BrokerToolGatewayFactory(_descriptorPath);
        var runId = new RunId("run_" + new string('5', 32));
        var snapshot = PolicySnapshotCanonicalizer.Create(
            1,
            AgentPermissionPreset.Standard,
            SnapshotWorkspace(),
            [],
            DateTimeOffset.UtcNow);
        var externalPath = Path.Combine(
            Path.GetTempPath(),
            "ru-broker-lease-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(externalPath, "lease-state");
        try
        {
            var first = Assert.IsType<RuBackend.Harness.Broker.BrokerToolGateway>(
                factory.Create(_workspaceRoot, snapshot, runId));
            var proposal = Proposal(runId, "read", new System.Text.Json.Nodes.JsonObject
            {
                ["path"] = externalPath,
            });
            var ask = await first.EvaluateAsync(proposal, CancellationToken.None);
            Assert.Equal(PolicyDecision.Ask, ask.Decision);
            var authorized = await factory.RespondApprovalAsync(
                runId.Value,
                ask.ApprovalId!.Value.Value,
                "allow_once",
                CancellationToken.None);
            Assert.True(authorized.Authorized);

            var replacement = Assert.IsType<RuBackend.Harness.Broker.BrokerToolGateway>(
                factory.Create(_workspaceRoot, snapshot, runId));
            Assert.Same(first, replacement);
            var resumed = await replacement.EvaluateAsync(proposal, CancellationToken.None);
            Assert.Equal(PolicyDecision.Allow, resumed.Decision);

            var changedSnapshot = PolicySnapshotCanonicalizer.Create(
                snapshot.Version + 1,
                snapshot.Preset,
                snapshot.Workspace,
                snapshot.OrderedRules,
                DateTimeOffset.UtcNow);
            var rotated = Assert.IsType<RuBackend.Harness.Broker.BrokerToolGateway>(
                factory.Create(_workspaceRoot, changedSnapshot, runId));
            Assert.Same(first, rotated);
            var reevaluated = await rotated.EvaluateAsync(proposal, CancellationToken.None);
            Assert.Equal(PolicyDecision.Ask, reevaluated.Decision);
        }
        finally
        {
            try { File.Delete(externalPath); } catch { }
            factory.CloseRun(runId.Value);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ResumesEditAndWriteInsideGrantedDirectory()
    {
        var factory = new global::RuBackend.Harness.Broker.BrokerToolGatewayFactory(_descriptorPath);
        var runId = new RunId("run_" + new string('1', 32));
        var snapshot = PolicySnapshotCanonicalizer.Create(
            1,
            AgentPermissionPreset.Standard,
            SnapshotWorkspace(),
            [],
            DateTimeOffset.UtcNow);
        var gateway = Assert.IsType<global::RuBackend.Harness.Broker.BrokerToolGateway>(
            factory.Create(_workspaceRoot, snapshot, runId));

        var externalDir = Path.Combine(Path.GetTempPath(), "ru-broker-factory-grant-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(externalDir);
        var editPath = Path.Combine(externalDir, "editable.txt");
        await File.WriteAllTextAsync(editPath, "factory-edit-pre");
        try
        {
            var editProposal = Proposal(runId, "edit", new System.Text.Json.Nodes.JsonObject
            {
                ["path"] = editPath,
                ["old_string"] = "factory-edit-pre",
                ["new_string"] = "factory-edit-post",
            });
            var editDecision = await gateway.EvaluateAsync(editProposal, CancellationToken.None);
            Assert.Equal(PolicyDecision.Ask, editDecision.Decision);

            var notePath = Path.Combine(externalDir, "note.md");
            var writeProposal = Proposal(runId, "write", new System.Text.Json.Nodes.JsonObject
            {
                ["op"] = "write",
                ["path"] = notePath,
                ["content"] = "external-note-content",
                ["overwrite"] = false,
            });
            var writeDecision = await gateway.EvaluateAsync(writeProposal, CancellationToken.None);
            Assert.Equal(PolicyDecision.Ask, writeDecision.Decision);

            var editAuthorized = await factory.RespondApprovalAsync(
                runId.Value,
                editDecision.ApprovalId!.Value.Value,
                "allow_session_directory",
                CancellationToken.None);
            Assert.True(editAuthorized.Authorized);

            var editResumed = Proposal(runId, "edit", new System.Text.Json.Nodes.JsonObject
            {
                ["path"] = editPath,
                ["old_string"] = "factory-edit-pre",
                ["new_string"] = "factory-edit-post",
            });
            var editResumedDecision = await gateway.EvaluateAsync(editResumed, CancellationToken.None);
            Assert.Equal(PolicyDecision.Allow, editResumedDecision.Decision);
            var editPrepared = await gateway.PrepareAsync(
                new ApprovedToolExecution(
                    editResumed,
                    new ExecutionId("exec_" + new string('1', 32)),
                    editResumedDecision.CanonicalIntentToken!,
                    editResumedDecision.GatewayGeneration),
                CancellationToken.None);
            var editReceipt = await gateway.ExecuteAsync(
                editPrepared,
                _ => ValueTask.CompletedTask,
                CancellationToken.None);
            Assert.Equal(ToolExecutionOutcome.Succeeded, editReceipt.Outcome);
            Assert.Equal("factory-edit-post", await File.ReadAllTextAsync(editPath));

            var writeAuthorized = await factory.RespondApprovalAsync(
                runId.Value,
                writeDecision.ApprovalId!.Value.Value,
                "allow_session_directory",
                CancellationToken.None);
            Assert.True(writeAuthorized.Authorized);

            var writeResumed = Proposal(runId, "write", new System.Text.Json.Nodes.JsonObject
            {
                ["op"] = "write",
                ["path"] = notePath,
                ["content"] = "external-note-content",
                ["overwrite"] = false,
            });
            var writeResumedDecision = await gateway.EvaluateAsync(writeResumed, CancellationToken.None);
            Assert.Equal(PolicyDecision.Allow, writeResumedDecision.Decision);
            var writePrepared = await gateway.PrepareAsync(
                new ApprovedToolExecution(
                    writeResumed,
                    new ExecutionId("exec_" + new string('2', 32)),
                    writeResumedDecision.CanonicalIntentToken!,
                    writeResumedDecision.GatewayGeneration),
                CancellationToken.None);
            var writeReceipt = await gateway.ExecuteAsync(
                writePrepared,
                _ => ValueTask.CompletedTask,
                CancellationToken.None);
            Assert.Equal(ToolExecutionOutcome.Succeeded, writeReceipt.Outcome);
            Assert.True(File.Exists(notePath));
            var noteText = await File.ReadAllTextAsync(notePath);
            Assert.Contains("external-note-content", noteText);
        }
        finally
        {
            TryDelete(externalDir);
            factory.CloseRun(runId.Value);
        }
    }

    [Fact]
    public async Task Failed_edit_carries_the_broker_repair_hint_into_model_content()
    {
        var factory = new global::RuBackend.Harness.Broker.BrokerToolGatewayFactory(_descriptorPath);
        var runId = new RunId("run_" + new string('e', 32));
        var snapshot = PolicySnapshotCanonicalizer.Create(
            1,
            AgentPermissionPreset.Standard,
            SnapshotWorkspace(),
            [],
            DateTimeOffset.UtcNow);
        var gateway = Assert.IsType<global::RuBackend.Harness.Broker.BrokerToolGateway>(
            factory.Create(_workspaceRoot, snapshot, runId));
        var editPath = Path.Combine(_workspaceRoot, "edit-hint.txt");
        await File.WriteAllTextAsync(editPath, "present");
        try
        {
            var proposal = Proposal(runId, "edit", new System.Text.Json.Nodes.JsonObject
            {
                ["path"] = editPath,
                ["old_string"] = "absent",
                ["new_string"] = "replacement",
            });
            var decision = await gateway.EvaluateAsync(proposal, CancellationToken.None);
            Assert.Equal(PolicyDecision.Allow, decision.Decision);
            var prepared = await gateway.PrepareAsync(
                new ApprovedToolExecution(
                    proposal,
                    new ExecutionId("exec_" + new string('e', 32)),
                    decision.CanonicalIntentToken!,
                    decision.GatewayGeneration),
                CancellationToken.None);
            var receipt = await gateway.ExecuteAsync(
                prepared,
                _ => ValueTask.CompletedTask,
                CancellationToken.None);

            Assert.Equal(ToolExecutionOutcome.Failed, receipt.Outcome);
            Assert.Equal("edit_no_match", receipt.Code);
            var content = System.Text.Json.Nodes.JsonNode.Parse(
                System.Text.Encoding.UTF8.GetString(receipt.ModelContent.Span))!.AsObject();
            Assert.Equal("edit_no_match", content["error"]!.GetValue<string>());
            var hint = content["hint"]!.GetValue<string>();
            Assert.Contains("old_string was not found in the file", hint, StringComparison.Ordinal);
            Assert.True(
                hint.Length <= global::RuBackend.Harness.Broker.BrokerToolGateway.MaximumRepairHintLength);
            Assert.DoesNotContain('\n', hint);
            Assert.DoesNotContain('\r', hint);
        }
        finally
        {
            factory.CloseRun(runId.Value);
        }
    }

    [Fact]
    public async Task Failed_execution_without_a_broker_summary_has_no_hint()
    {
        var factory = new global::RuBackend.Harness.Broker.BrokerToolGatewayFactory(_descriptorPath);
        var runId = new RunId("run_" + new string('b', 32));
        var snapshot = PolicySnapshotCanonicalizer.Create(
            1,
            AgentPermissionPreset.Standard,
            SnapshotWorkspace(),
            [],
            DateTimeOffset.UtcNow);
        var gateway = Assert.IsType<global::RuBackend.Harness.Broker.BrokerToolGateway>(
            factory.Create(_workspaceRoot, snapshot, runId));
        try
        {
            var proposal = Proposal(runId, "read", new System.Text.Json.Nodes.JsonObject
            {
                ["path"] = "notes/missing.txt",
            });
            var decision = await gateway.EvaluateAsync(proposal, CancellationToken.None);
            Assert.Equal(PolicyDecision.Allow, decision.Decision);
            var prepared = await gateway.PrepareAsync(
                new ApprovedToolExecution(
                    proposal,
                    new ExecutionId("exec_" + new string('b', 32)),
                    decision.CanonicalIntentToken!,
                    decision.GatewayGeneration),
                CancellationToken.None);
            gateway.ExecutionReplyTextTransform = text =>
            {
                var envelope = System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject();
                envelope["payload"]!.AsObject().Remove("safe_summary");
                return envelope.ToJsonString();
            };

            var receipt = await gateway.ExecuteAsync(
                prepared,
                _ => ValueTask.CompletedTask,
                CancellationToken.None);

            Assert.Equal(ToolExecutionOutcome.Failed, receipt.Outcome);
            Assert.Equal("file_not_found", receipt.Code);
            var content = System.Text.Json.Nodes.JsonNode.Parse(
                System.Text.Encoding.UTF8.GetString(receipt.ModelContent.Span))!.AsObject();
            Assert.Equal("file_not_found", content["error"]!.GetValue<string>());
            Assert.False(content.ContainsKey("hint"));
        }
        finally
        {
            factory.CloseRun(runId.Value);
        }
    }

    [Fact]
    public async Task Failed_execution_keeps_a_bounded_spool_id_for_paged_diagnostics()
    {
        var factory = new global::RuBackend.Harness.Broker.BrokerToolGatewayFactory(_descriptorPath);
        var runId = new RunId("run_" + new string('5', 32));
        var snapshot = PolicySnapshotCanonicalizer.Create(
            1,
            AgentPermissionPreset.Standard,
            SnapshotWorkspace(),
            [],
            DateTimeOffset.UtcNow);
        var gateway = Assert.IsType<global::RuBackend.Harness.Broker.BrokerToolGateway>(
            factory.Create(_workspaceRoot, snapshot, runId));
        try
        {
            gateway.ExecutionReplyTextTransform = text =>
            {
                var envelope = System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject();
                envelope["payload"]!.AsObject()["output"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["spool_id"] = "req-123",
                    ["stderr_head"] = "raw diagnostic that must not cross",
                };
                return envelope.ToJsonString();
            };

            var proposal = Proposal(runId, "read", new System.Text.Json.Nodes.JsonObject
            {
                ["path"] = "notes/missing.txt",
            });
            var decision = await gateway.EvaluateAsync(proposal, CancellationToken.None);
            Assert.Equal(PolicyDecision.Allow, decision.Decision);
            var prepared = await gateway.PrepareAsync(
                new ApprovedToolExecution(
                    proposal,
                    new ExecutionId("exec_" + new string('5', 32)),
                    decision.CanonicalIntentToken!,
                    decision.GatewayGeneration),
                CancellationToken.None);

            var receipt = await gateway.ExecuteAsync(
                prepared,
                _ => ValueTask.CompletedTask,
                CancellationToken.None);

            Assert.Equal(ToolExecutionOutcome.Failed, receipt.Outcome);
            var content = System.Text.Json.Nodes.JsonNode.Parse(
                System.Text.Encoding.UTF8.GetString(receipt.ModelContent.Span))!.AsObject();
            Assert.Equal("req-123", content["spool_id"]!.GetValue<string>());
            Assert.False(content.ContainsKey("stderr_head"));
        }
        finally
        {
            factory.CloseRun(runId.Value);
        }
    }

    [Fact]
    public void BoundRepairHint_strips_lines_clamps_and_rejects_forbidden_fields()
    {
        Assert.Null(global::RuBackend.Harness.Broker.BrokerToolGateway.BoundRepairHint(null));
        Assert.Null(global::RuBackend.Harness.Broker.BrokerToolGateway.BoundRepairHint("   "));
        Assert.Null(global::RuBackend.Harness.Broker.BrokerToolGateway.BoundRepairHint("copy \"output\" from the file"));

        var bounded = global::RuBackend.Harness.Broker.BrokerToolGateway.BoundRepairHint(
            "line one\r\nline two " + new string('x', 600));

        Assert.NotNull(bounded);
        Assert.DoesNotContain('\r', bounded!);
        Assert.DoesNotContain('\n', bounded!);
        Assert.Equal(
            global::RuBackend.Harness.Broker.BrokerToolGateway.MaximumRepairHintLength,
            bounded!.Length);
        Assert.Equal(
            "old_string was not found in the file. Re-read the file and copy the exact text including whitespace.",
            global::RuBackend.Harness.Broker.BrokerToolGateway.BoundRepairHint(
                "  old_string was not found in the file. Re-read the file and copy the exact text including whitespace.  "));
    }

    [Fact]
    public async Task RepeatedResumedProposal_ReevaluatesAfterTokenConsumption()
    {
        var factory = new global::RuBackend.Harness.Broker.BrokerToolGatewayFactory(_descriptorPath);
        var runId = new RunId("run_" + new string('3', 32));
        var snapshot = PolicySnapshotCanonicalizer.Create(
            1,
            AgentPermissionPreset.Standard,
            SnapshotWorkspace(),
            [],
            DateTimeOffset.UtcNow);
        var gateway = Assert.IsType<global::RuBackend.Harness.Broker.BrokerToolGateway>(
            factory.Create(_workspaceRoot, snapshot, runId));

        var externalPath = Path.Combine(
            Path.GetTempPath(),
            "ru-broker-replay-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(externalPath, "replay-once");
        try
        {
            var readArgs = new System.Text.Json.Nodes.JsonObject
            {
                ["path"] = externalPath,
            };
            var decision = await gateway.EvaluateAsync(
                Proposal(runId, "read", readArgs),
                CancellationToken.None);
            Assert.Equal(PolicyDecision.Ask, decision.Decision);
            var authorized = await factory.RespondApprovalAsync(
                runId.Value,
                decision.ApprovalId!.Value.Value,
                "allow_session_directory",
                CancellationToken.None);
            Assert.True(authorized.Authorized);

            async Task<ToolExecutionReceipt> ExecuteResumedAsync()
            {
                var resumed = Proposal(runId, "read", readArgs);
                var resumedDecision = await gateway.EvaluateAsync(resumed, CancellationToken.None);
                Assert.Equal(PolicyDecision.Allow, resumedDecision.Decision);
                var prepared = await gateway.PrepareAsync(
                    new ApprovedToolExecution(
                        resumed,
                        new ExecutionId("exec_" + Guid.NewGuid().ToString("N")),
                        resumedDecision.CanonicalIntentToken!,
                        resumedDecision.GatewayGeneration),
                    CancellationToken.None);
                return await gateway.ExecuteAsync(
                    prepared,
                    _ => ValueTask.CompletedTask,
                    CancellationToken.None);
            }

            var first = await ExecuteResumedAsync();
            Assert.Equal(ToolExecutionOutcome.Succeeded, first.Outcome);

            // The Broker consumes its authorization token on first use, so the
            // cached copy is single-use too: a repeated identical proposal must
            // re-evaluate through the session directory grant instead of
            // replaying the consumed token into decision_stale.
            var second = await ExecuteResumedAsync();
            Assert.Equal(ToolExecutionOutcome.Succeeded, second.Outcome);
        }
        finally
        {
            try { File.Delete(externalPath); } catch { }
            factory.CloseRun(runId.Value);
        }
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

    [Fact]
    public async Task A_restarted_broker_is_reached_again_on_the_same_gateway()
    {
        var factory = new global::RuBackend.Harness.Broker.BrokerToolGatewayFactory(_descriptorPath);
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
            var first = await gateway.EvaluateAsync(ReadProposal(runId), CancellationToken.None);
            Assert.Equal(PolicyDecision.Allow, first.Decision);

            // The socket is dead from here on. It used to stay in the gateway's field, so
            // EnsureReadyAsync never reconnected and every later call on this run failed with
            // broker_connection_closed until the run ended.
            //
            // Stopping the Broker removes its own descriptor as part of its cleanup, which
            // production never does while a host is attached to it, so the descriptor is put
            // back before the restart. The gateway itself holds the url and token it read at
            // Create time and is unaffected either way.
            var descriptorJson = await File.ReadAllTextAsync(_descriptorPath);
            if (_brokerHost is not null)
            {
                await _brokerHost.DisposeAsync();
                _brokerHost = null;
            }

            await Assert.ThrowsAnyAsync<Exception>(
                () => gateway.EvaluateAsync(ReadProposal(runId), CancellationToken.None));

            await File.WriteAllTextAsync(_descriptorPath, descriptorJson);
            _brokerHost = await BrokerTestHostHost.StartAsync(_descriptorPath);

            var afterRestart = await gateway.EvaluateAsync(ReadProposal(runId), CancellationToken.None);
            Assert.Equal(PolicyDecision.Allow, afterRestart.Decision);
        }
        finally
        {
            factory.CloseRun(runId.Value);
        }
    }

    [Fact]
    public async Task Cached_token_is_not_consumed_after_same_scope_broker_rebind()
    {
        var factory = new global::RuBackend.Harness.Broker.BrokerToolGatewayFactory(_descriptorPath);
        var runId = new RunId("run_" + new string('6', 32));
        var snapshot = PolicySnapshotCanonicalizer.Create(
            1,
            AgentPermissionPreset.Standard,
            SnapshotWorkspace(),
            [],
            DateTimeOffset.UtcNow);
        var gateway = Assert.IsType<RuBackend.Harness.Broker.BrokerToolGateway>(
            factory.Create(_workspaceRoot, snapshot, runId));
        var externalPath = Path.Combine(
            Path.GetTempPath(),
            "ru-broker-rebind-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(externalPath, "rebind");
        try
        {
            var proposal = Proposal(runId, "read", new System.Text.Json.Nodes.JsonObject
            {
                ["path"] = externalPath,
            });
            var ask = await gateway.EvaluateAsync(proposal, CancellationToken.None);
            Assert.Equal(PolicyDecision.Ask, ask.Decision);
            var authorized = await factory.RespondApprovalAsync(
                runId.Value,
                ask.ApprovalId!.Value.Value,
                "allow_once",
                CancellationToken.None);
            Assert.True(authorized.Authorized);
            var generationBeforeRebind = gateway.Lease!.Generation;

            var descriptorJson = await File.ReadAllTextAsync(_descriptorPath);
            if (_brokerHost is not null)
            {
                await _brokerHost.DisposeAsync();
                _brokerHost = null;
            }

            await File.WriteAllTextAsync(_descriptorPath, descriptorJson);
            _brokerHost = await BrokerTestHostHost.StartAsync(_descriptorPath);

            var reevaluated = await gateway.EvaluateAsync(proposal, CancellationToken.None);
            Assert.True(gateway.Lease!.Generation > generationBeforeRebind);
            Assert.Equal(PolicyDecision.Ask, reevaluated.Decision);
        }
        finally
        {
            try { File.Delete(externalPath); } catch { }
            factory.CloseRun(runId.Value);
        }
    }

    [Fact]
    public async Task Rebind_between_evaluate_and_prepare_rejects_stale_generation_before_execute()
    {
        var factory = new global::RuBackend.Harness.Broker.BrokerToolGatewayFactory(_descriptorPath);
        var runId = new RunId("run_" + new string('9', 32));
        var snapshot = PolicySnapshotCanonicalizer.Create(
            1,
            AgentPermissionPreset.Standard,
            SnapshotWorkspace(),
            [],
            DateTimeOffset.UtcNow);
        BrokerTestHostHost? rotatedBroker = null;
        var writePath = Path.Combine(_workspaceRoot, "toctou.txt");
        await File.WriteAllTextAsync(writePath, "before");
        try
        {
            var gateway = Assert.IsType<RuBackend.Harness.Broker.BrokerToolGateway>(
                factory.Create(_workspaceRoot, snapshot, runId));
            var proposal = Proposal(runId, "write", new System.Text.Json.Nodes.JsonObject
            {
                ["op"] = "write",
                ["path"] = writePath,
                ["content"] = "after",
                ["overwrite"] = true,
            });
            var allow = await gateway.EvaluateAsync(proposal, CancellationToken.None);
            Assert.Equal(PolicyDecision.Allow, allow.Decision);
            var evaluatedGeneration = gateway.Lease!.Generation;
            Assert.Equal(evaluatedGeneration, allow.GatewayGeneration);
            Assert.True(evaluatedGeneration > 0);

            var rotatedSessionRoot = Path.Combine(_brokerRoot!, "toctou-session");
            Directory.CreateDirectory(rotatedSessionRoot);
            var descriptor = new BrokerSessionDescriptor(
                "http://127.0.0.1:" + GetFreePort(),
                "broker-toctou-" + Guid.NewGuid().ToString("N"),
                1,
                rotatedSessionRoot,
                DateTimeOffset.UtcNow.AddMinutes(30));
            await File.WriteAllTextAsync(
                _descriptorPath,
                JsonSerializer.Serialize(descriptor, BrokerSessionDescriptor.JsonOptions));
            rotatedBroker = await BrokerTestHostHost.StartAsync(_descriptorPath);
            var rebound = Assert.IsType<RuBackend.Harness.Broker.BrokerToolGateway>(
                factory.Create(_workspaceRoot, snapshot, runId));
            Assert.Same(gateway, rebound);
            var current = await rebound.EvaluateAsync(ReadProposal(runId), CancellationToken.None);
            Assert.Equal(PolicyDecision.Allow, current.Decision);
            Assert.True(rebound.Lease!.Generation > evaluatedGeneration);

            var error = await Assert.ThrowsAsync<ToolTransportException>(() => rebound.PrepareAsync(
                new ApprovedToolExecution(
                    proposal,
                    new ExecutionId("exec_" + new string('9', 32)),
                    allow.CanonicalIntentToken!,
                    allow.GatewayGeneration),
                CancellationToken.None));
            Assert.Equal("broker_lease_generation_changed", error.Message);
            Assert.Equal("before", await File.ReadAllTextAsync(writePath));
        }
        finally
        {
            if (rotatedBroker is not null)
            {
                await rotatedBroker.DisposeAsync();
            }

            factory.CloseRun(runId.Value);
        }
    }

    [Fact]
    public async Task Post_send_envelope_parse_failure_is_unknown_for_mutation()
    {
        var (gateway, prepared, factory, runId) = await CreateMutationExecutionAsync(
            'b',
            "envelope-parse-failure.txt");
        try
        {
            gateway.ExecutionReplyTextTransform = _ => "not-an-envelope";

            var receipt = await gateway.ExecuteAsync(
                prepared,
                _ => ValueTask.CompletedTask,
                CancellationToken.None);

            Assert.Equal(ToolExecutionOutcome.OutcomeUnknown, receipt.Outcome);
            Assert.True(receipt.SideEffectsPossible);
            Assert.Equal("transport_failure", receipt.Code);
        }
        finally
        {
            factory.CloseRun(runId);
        }
    }

    [Fact]
    public async Task Post_send_invalid_operation_is_unknown_for_mutation()
    {
        var (gateway, prepared, factory, runId) = await CreateMutationExecutionAsync(
            'c',
            "invalid-operation.txt");
        try
        {
            gateway.ExecutionReplyTextTransform = text =>
            {
                var envelope = System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject();
                envelope["payload"]!.AsObject()["status"] = new System.Text.Json.Nodes.JsonObject();
                return envelope.ToJsonString();
            };

            var receipt = await gateway.ExecuteAsync(
                prepared,
                _ => ValueTask.CompletedTask,
                CancellationToken.None);

            Assert.Equal(ToolExecutionOutcome.OutcomeUnknown, receipt.Outcome);
            Assert.True(receipt.SideEffectsPossible);
            Assert.Equal("transport_failure", receipt.Code);
        }
        finally
        {
            factory.CloseRun(runId);
        }
    }

    [Fact]
    public async Task Post_send_cancellation_is_unknown_for_mutation()
    {
        var (gateway, prepared, factory, runId) = await CreateMutationExecutionAsync(
            'd',
            "post-send-cancellation.txt");
        try
        {
            using var cancellation = new CancellationTokenSource();
            gateway.AfterExecutionSendAsync = token =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(token);
            };

            var receipt = await gateway.ExecuteAsync(
                prepared,
                _ => ValueTask.CompletedTask,
                cancellation.Token);

            Assert.Equal(ToolExecutionOutcome.OutcomeUnknown, receipt.Outcome);
            Assert.True(receipt.SideEffectsPossible);
            Assert.Equal("transport_failure", receipt.Code);
        }
        finally
        {
            factory.CloseRun(runId);
        }
    }

    [Fact]
    public async Task Pre_send_programmer_failure_is_not_normalized()
    {
        var (gateway, prepared, factory, runId) = await CreateMutationExecutionAsync(
            'e',
            "pre-send-failure.txt");
        try
        {
            gateway.BeforeExecutionSendAsync = (_, _) =>
                throw new InvalidOperationException("pre_send_failure");

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                gateway.ExecuteAsync(
                    prepared,
                    _ => ValueTask.CompletedTask,
                    CancellationToken.None));

            Assert.Equal("pre_send_failure", error.Message);
        }
        finally
        {
            factory.CloseRun(runId);
        }
    }

    [Fact]
    public async Task Pre_send_cancellation_is_not_normalized()
    {
        var (gateway, prepared, factory, runId) = await CreateMutationExecutionAsync(
            'f',
            "pre-send-cancellation.txt");
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            gateway.BeforeExecutionSendAsync = (_, token) => Task.FromCanceled(token);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                gateway.ExecuteAsync(
                    prepared,
                    _ => ValueTask.CompletedTask,
                    cancellation.Token));
        }
        finally
        {
            factory.CloseRun(runId);
        }
    }

    [Theory]
    [InlineData("write")]
    [InlineData("read")]
    public async Task Post_send_socket_loss_preserves_access_kind_semantics(string toolId)
    {
        var factory = new global::RuBackend.Harness.Broker.BrokerToolGatewayFactory(_descriptorPath);
        var runId = new RunId("run_" + new string('a', 32));
        var snapshot = PolicySnapshotCanonicalizer.Create(
            1,
            AgentPermissionPreset.Standard,
            SnapshotWorkspace(),
            [],
            DateTimeOffset.UtcNow);
        BrokerTestHostHost? rotatedBroker = null;
        TaskCompletionSource<bool>? releaseReceive = null;
        var writePath = Path.Combine(_workspaceRoot, "post-send-loss.txt");
        await File.WriteAllTextAsync(writePath, "before");
        try
        {
            var gateway = Assert.IsType<RuBackend.Harness.Broker.BrokerToolGateway>(
                factory.Create(_workspaceRoot, snapshot, runId));
            var proposal = Proposal(
                runId,
                toolId,
                toolId == "write"
                    ? new System.Text.Json.Nodes.JsonObject
                    {
                        ["op"] = "write",
                        ["path"] = writePath,
                        ["content"] = "after",
                        ["overwrite"] = true,
                    }
                    : new System.Text.Json.Nodes.JsonObject
                    {
                        ["path"] = "notes/a.txt",
                    });
            var allow = await gateway.EvaluateAsync(proposal, CancellationToken.None);
            Assert.Equal(PolicyDecision.Allow, allow.Decision);
            var prepared = await gateway.PrepareAsync(
                new ApprovedToolExecution(
                    proposal,
                    new ExecutionId("exec_" + new string('a', 32)),
                    allow.CanonicalIntentToken!,
                    allow.GatewayGeneration),
                CancellationToken.None);

            var sendCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            releaseReceive = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            gateway.AfterExecutionSendAsync = async cancellationToken =>
            {
                sendCompleted.TrySetResult(true);
                await releaseReceive.Task.WaitAsync(cancellationToken);
            };
            var execution = gateway.ExecuteAsync(
                prepared,
                _ => ValueTask.CompletedTask,
                CancellationToken.None);
            await sendCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var rotatedSessionRoot = Path.Combine(_brokerRoot!, "post-send-loss-session");
            Directory.CreateDirectory(rotatedSessionRoot);
            var descriptor = new BrokerSessionDescriptor(
                "http://127.0.0.1:" + GetFreePort(),
                "broker-post-send-loss-" + Guid.NewGuid().ToString("N"),
                1,
                rotatedSessionRoot,
                DateTimeOffset.UtcNow.AddMinutes(30));
            await File.WriteAllTextAsync(
                _descriptorPath,
                JsonSerializer.Serialize(descriptor, BrokerSessionDescriptor.JsonOptions));
            rotatedBroker = await BrokerTestHostHost.StartAsync(_descriptorPath);
            var rebound = Assert.IsType<RuBackend.Harness.Broker.BrokerToolGateway>(
                factory.Create(_workspaceRoot, snapshot, runId));
            Assert.Same(gateway, rebound);

            releaseReceive.TrySetResult(true);
            if (toolId == "write")
            {
                var receipt = await execution;
                Assert.Equal(ToolExecutionOutcome.OutcomeUnknown, receipt.Outcome);
                Assert.True(receipt.SideEffectsPossible);
                Assert.Equal("transport_failure", receipt.Code);
            }
            else
            {
                var receipt = await execution;
                Assert.Equal(ToolExecutionOutcome.Failed, receipt.Outcome);
                Assert.False(receipt.SideEffectsPossible);
                Assert.Equal("transport_failure", receipt.Code);
                var retry = await gateway.ExecuteAsync(
                    prepared,
                    _ => ValueTask.CompletedTask,
                    CancellationToken.None);
                Assert.Equal(ToolExecutionOutcome.Failed, retry.Outcome);
                Assert.False(retry.SideEffectsPossible);
                Assert.Equal("decision_stale", retry.Code);
            }
        }
        finally
        {
            releaseReceive?.TrySetResult(true);
            if (rotatedBroker is not null)
            {
                await rotatedBroker.DisposeAsync();
            }

            factory.CloseRun(runId.Value);
        }
    }

    [Fact]
    public async Task Rebind_racing_execution_rejects_old_generation_before_broker_rpc()
    {
        var factory = new global::RuBackend.Harness.Broker.BrokerToolGatewayFactory(_descriptorPath);
        var runId = new RunId("run_" + new string('7', 32));
        var snapshot = PolicySnapshotCanonicalizer.Create(
            1,
            AgentPermissionPreset.Standard,
            SnapshotWorkspace(),
            [],
            DateTimeOffset.UtcNow);
        BrokerTestHostHost? rotatedBroker = null;
        TaskCompletionSource<bool>? releaseExecution = null;
        var externalPath = Path.Combine(
            Path.GetTempPath(),
            "ru-broker-race-external-" + Guid.NewGuid().ToString("N") + ".txt");
        var workspaceWritePath = Path.Combine(_workspaceRoot, "race.txt");
        await File.WriteAllTextAsync(externalPath, "external");
        await File.WriteAllTextAsync(workspaceWritePath, "before");
        try
        {
            var gateway = Assert.IsType<RuBackend.Harness.Broker.BrokerToolGateway>(
                factory.Create(_workspaceRoot, snapshot, runId));
            var externalProposal = Proposal(runId, "read", new System.Text.Json.Nodes.JsonObject
            {
                ["path"] = externalPath,
            });
            var ask = await gateway.EvaluateAsync(externalProposal, CancellationToken.None);
            Assert.Equal(PolicyDecision.Ask, ask.Decision);
            var authorized = await factory.RespondApprovalAsync(
                runId.Value,
                ask.ApprovalId!.Value.Value,
                "allow_once",
                CancellationToken.None);
            Assert.True(authorized.Authorized);

            var writeProposal = Proposal(runId, "write", new System.Text.Json.Nodes.JsonObject
            {
                ["op"] = "write",
                ["path"] = workspaceWritePath,
                ["content"] = "after",
                ["overwrite"] = true,
            });
            var allow = await gateway.EvaluateAsync(writeProposal, CancellationToken.None);
            Assert.Equal(PolicyDecision.Allow, allow.Decision);
            var racingExecution = new ExecutionId("exec_" + new string('7', 32));
            var prepared = await gateway.PrepareAsync(
                new ApprovedToolExecution(
                    writeProposal,
                    racingExecution,
                    allow.CanonicalIntentToken!,
                    allow.GatewayGeneration),
                CancellationToken.None);
            var cancelledExecution = new ExecutionId("exec_" + new string('8', 32));
            var cancelled = await gateway.PrepareAsync(
                new ApprovedToolExecution(
                    writeProposal,
                    cancelledExecution,
                    allow.CanonicalIntentToken!,
                    allow.GatewayGeneration),
                CancellationToken.None);
            await gateway.CancelAsync(cancelledExecution, CancellationToken.None);

            var executionEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            releaseExecution = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            gateway.BeforeExecutionSendAsync = async (_, cancellationToken) =>
            {
                executionEntered.TrySetResult(true);
                await releaseExecution.Task.WaitAsync(cancellationToken);
            };
            var executionTask = gateway.ExecuteAsync(
                prepared,
                _ => ValueTask.CompletedTask,
                CancellationToken.None);
            await executionEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var rotatedSessionRoot = Path.Combine(_brokerRoot!, "rotated-session");
            Directory.CreateDirectory(rotatedSessionRoot);
            var descriptor = new BrokerSessionDescriptor(
                "http://127.0.0.1:" + GetFreePort(),
                "broker-race-" + Guid.NewGuid().ToString("N"),
                1,
                rotatedSessionRoot,
                DateTimeOffset.UtcNow.AddMinutes(30));
            await File.WriteAllTextAsync(
                _descriptorPath,
                JsonSerializer.Serialize(descriptor, BrokerSessionDescriptor.JsonOptions));
            rotatedBroker = await BrokerTestHostHost.StartAsync(_descriptorPath);
            var rebound = Assert.IsType<RuBackend.Harness.Broker.BrokerToolGateway>(
                factory.Create(_workspaceRoot, snapshot, runId));
            Assert.Same(gateway, rebound);

            var cancelledReceipt = await rebound.ExecuteAsync(
                cancelled,
                _ => ValueTask.CompletedTask,
                CancellationToken.None);
            Assert.Equal(ToolExecutionOutcome.Failed, cancelledReceipt.Outcome);
            Assert.Equal("decision_stale", cancelledReceipt.Code);

            releaseExecution.TrySetResult(true);
            var racingReceipt = await executionTask;
            Assert.Equal(ToolExecutionOutcome.Failed, racingReceipt.Outcome);
            Assert.Equal("decision_stale", racingReceipt.Code);
            Assert.Equal("before", await File.ReadAllTextAsync(workspaceWritePath));

            var reevaluatedExternal = await rebound.EvaluateAsync(
                externalProposal,
                CancellationToken.None);
            Assert.Equal(PolicyDecision.Ask, reevaluatedExternal.Decision);
            var reconnectedRead = await rebound.EvaluateAsync(
                ReadProposal(runId),
                CancellationToken.None);
            Assert.Equal(PolicyDecision.Allow, reconnectedRead.Decision);
            Assert.NotNull(rebound.Lease);
        }
        finally
        {
            releaseExecution?.TrySetResult(true);
            if (rotatedBroker is not null)
            {
                await rotatedBroker.DisposeAsync();
            }

            try { File.Delete(externalPath); } catch { }
            factory.CloseRun(runId.Value);
        }
    }

    private async Task<(
        global::RuBackend.Harness.Broker.BrokerToolGateway Gateway,
        PreparedToolExecution Prepared,
        global::RuBackend.Harness.Broker.BrokerToolGatewayFactory Factory,
        string RunIdValue)> CreateMutationExecutionAsync(
        char runIdCharacter,
        string fileName)
    {
        var factory = new global::RuBackend.Harness.Broker.BrokerToolGatewayFactory(_descriptorPath);
        var runId = new RunId("run_" + new string(runIdCharacter, 32));
        var snapshot = PolicySnapshotCanonicalizer.Create(
            1,
            AgentPermissionPreset.Standard,
            SnapshotWorkspace(),
            [],
            DateTimeOffset.UtcNow);
        var gateway = Assert.IsType<global::RuBackend.Harness.Broker.BrokerToolGateway>(
            factory.Create(_workspaceRoot, snapshot, runId));
        var path = Path.Combine(_workspaceRoot, fileName);
        await File.WriteAllTextAsync(path, "before");
        var proposal = Proposal(
            runId,
            "write",
            new System.Text.Json.Nodes.JsonObject
            {
                ["op"] = "write",
                ["path"] = path,
                ["content"] = "after",
                ["overwrite"] = true,
            });
        var allow = await gateway.EvaluateAsync(proposal, CancellationToken.None);
        Assert.Equal(PolicyDecision.Allow, allow.Decision);
        var prepared = await gateway.PrepareAsync(
            new ApprovedToolExecution(
                proposal,
                new ExecutionId("exec_" + new string(runIdCharacter, 32)),
                allow.CanonicalIntentToken!,
                allow.GatewayGeneration),
            CancellationToken.None);
        return (gateway, prepared, factory, runId.Value);
    }

    private static ToolProposal ReadProposal(RunId runId) => Proposal(runId, "read", new System.Text.Json.Nodes.JsonObject
    {
        ["path"] = "notes/a.txt",
    });

    private static WorkspaceIdentity SnapshotWorkspace() => new(
        new WorkspaceId("ws_" + new string('1', 32)),
        "VOL-FACTORY",
        "fid-factory",
        new string('a', 64));

    private static ToolProposal Proposal(RunId runId, string toolId, System.Text.Json.Nodes.JsonObject arguments) => new(
        runId,
        new ModelStepId("step_" + new string('0', 32)),
        new ToolCallId("call_" + new string('0', 32)),
        "provider_call_1",
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
