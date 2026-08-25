using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class ToolExecutionServiceTests
{
    [Fact]
    public async Task Approved_full_terminal_run_can_use_an_outside_working_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-terminal-service-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var outsideDirectory = Path.Combine(root, "outside");
        Directory.CreateDirectory(outsideDirectory);
        var workspaceRoot = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspaceRoot);
        var now = DateTimeOffset.UtcNow;
        var descriptor = new BrokerSessionDescriptor(
            "http://127.0.0.1:18086",
            "broker-token",
            1,
            Path.Combine(root, "session"),
            now.AddMinutes(5));
        Directory.CreateDirectory(descriptor.SessionRoot);
        var descriptorPath = Path.Combine(root, "broker.json");
        File.WriteAllText(
            descriptorPath,
            JsonSerializer.Serialize(descriptor, BrokerSessionDescriptor.JsonOptions));
        var session = BrokerSession.Load(descriptorPath, now);
        session.Open(
            "session-terminal-unrestricted",
            new HashSet<string>(
                [RuAgent.Broker.Policy.ToolScope.TerminalExecute, RuAgent.Broker.Policy.ToolScope.TerminalNetwork],
                StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));
        var service = new ToolExecutionService(session, workspaceRoot);
        try
        {
            var malformed = await service.ExecuteAsync(
                new AgentEnvelope(
                    AgentEnvelopeCodec.ProtocolVersion,
                    "malformed-" + Guid.NewGuid().ToString("N"),
                    AgentMessageTypes.ToolRequest,
                    "session-terminal-unrestricted",
                    null,
                    30_000,
                    new JsonObject
                    {
                        ["tool_id"] = "terminal.run",
                        ["tool_version"] = "1.0.0",
                        ["capability_class"] = "internal",
                        ["arguments"] = new JsonObject { ["network"] = true },
                    }),
                CancellationToken.None);
            Assert.Equal(ToolResultStatuses.Failed, malformed.Status);
            Assert.Equal("invalid_arguments", malformed.ErrorCode);
            Assert.False(malformed.AdapterInvoked);
            Assert.False(malformed.Output.ContainsKey("approval_id"));

            var envelope = new AgentEnvelope(
                AgentEnvelopeCodec.ProtocolVersion,
                "req-" + Guid.NewGuid().ToString("N"),
                AgentMessageTypes.ToolRequest,
                "session-terminal-unrestricted",
                null,
                30_000,
                new JsonObject
                {
                    ["tool_id"] = "terminal.run",
                    ["tool_version"] = "1.0.0",
                    ["capability_class"] = "internal",
                    ["arguments"] = new JsonObject
                    {
                        ["executable"] = "python",
                        ["arguments"] = new JsonArray("-c", "print(1)"),
                        ["working_directory"] = outsideDirectory,
                        ["network_intent"] = true,
                    },
                });

            var pending = await service.ExecuteAsync(envelope, CancellationToken.None);

            Assert.Equal(ToolResultStatuses.ApprovalRequired, pending.Status);
            Assert.False(pending.AdapterInvoked);
            var approvalId = pending.Output["approval_id"]!.GetValue<string>();
            var result = await service.RespondToApprovalAsync(
                new AgentEnvelope(
                    AgentEnvelopeCodec.ProtocolVersion,
                    "approval-" + Guid.NewGuid().ToString("N"),
                    AgentMessageTypes.ApprovalResponse,
                    "session-terminal-unrestricted",
                    null,
                    30_000,
                    new JsonObject
                    {
                        ["approval_id"] = approvalId,
                        ["approved"] = true,
                    }),
                CancellationToken.None);

            Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
            Assert.True(result.AdapterInvoked);
            Assert.Equal(
                Path.GetFullPath(outsideDirectory),
                result.Output["working_directory"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Full_access_shell_requires_approval_before_execution()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-shell-full-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var outsideDirectory = Path.Combine(root, "outside");
        Directory.CreateDirectory(outsideDirectory);
        var workspaceRoot = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspaceRoot);
        var descriptor = new BrokerSessionDescriptor(
            "http://127.0.0.1:18086",
            "broker-token",
            1,
            Path.Combine(root, "session"),
            DateTimeOffset.UtcNow.AddMinutes(5));
        Directory.CreateDirectory(descriptor.SessionRoot);
        var descriptorPath = Path.Combine(root, "broker.json");
        File.WriteAllText(
            descriptorPath,
            JsonSerializer.Serialize(descriptor, BrokerSessionDescriptor.JsonOptions));
        var session = BrokerSession.Load(descriptorPath, DateTimeOffset.UtcNow);
        session.Open(
            "session-shell-full",
            new HashSet<string>(
                [RuAgent.Broker.Policy.ToolScope.TerminalExecute, RuAgent.Broker.Policy.ToolScope.TerminalNetwork],
                StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));
        var service = new ToolExecutionService(session, workspaceRoot);
        try
        {
            var envelope = new AgentEnvelope(
                AgentEnvelopeCodec.ProtocolVersion,
                "req-" + Guid.NewGuid().ToString("N"),
                AgentMessageTypes.ToolRequest,
                "session-shell-full",
                null,
                30_000,
                new JsonObject
                {
                    ["tool_id"] = "terminal.shell",
                    ["tool_version"] = "1.0.0",
                    ["capability_class"] = "internal",
                    ["arguments"] = new JsonObject
                    {
                        ["script"] = "Get-Date",
                        ["working_directory"] = outsideDirectory,
                    },
                });

            var result = await service.ExecuteAsync(envelope, CancellationToken.None);

            Assert.Equal(ToolResultStatuses.ApprovalRequired, result.Status);
            Assert.False(result.AdapterInvoked);
            Assert.NotNull(result.Output["approval_id"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Agent_grant_shell_still_requires_approval_without_token()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-shell-agent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspaceRoot = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspaceRoot);
        var descriptor = new BrokerSessionDescriptor(
            "http://127.0.0.1:18086",
            "broker-token",
            1,
            Path.Combine(root, "session"),
            DateTimeOffset.UtcNow.AddMinutes(5));
        Directory.CreateDirectory(descriptor.SessionRoot);
        var descriptorPath = Path.Combine(root, "broker.json");
        File.WriteAllText(
            descriptorPath,
            JsonSerializer.Serialize(descriptor, BrokerSessionDescriptor.JsonOptions));
        var session = BrokerSession.Load(descriptorPath, DateTimeOffset.UtcNow);
        session.Open(
            "session-shell-agent",
            new HashSet<string>(
                [RuAgent.Broker.Policy.ToolScope.TerminalExecute],
                StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));
        var service = new ToolExecutionService(session, workspaceRoot);
        try
        {
            var envelope = new AgentEnvelope(
                AgentEnvelopeCodec.ProtocolVersion,
                "req-" + Guid.NewGuid().ToString("N"),
                AgentMessageTypes.ToolRequest,
                "session-shell-agent",
                null,
                30_000,
                new JsonObject
                {
                    ["tool_id"] = "terminal.shell",
                    ["tool_version"] = "1.0.0",
                    ["capability_class"] = "internal",
                    ["arguments"] = new JsonObject
                    {
                        ["script"] = "Get-Date",
                        ["working_directory"] = ".",
                    },
                });

            var result = await service.ExecuteAsync(envelope, CancellationToken.None);

            Assert.Equal("approval_required", result.Status);
            Assert.NotNull(result.Output["approval_id"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Files_edit_approval_advertises_its_safe_action()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-edit-safe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspaceRoot = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspaceRoot);
        var descriptor = new BrokerSessionDescriptor(
            "http://127.0.0.1:18086",
            "broker-token",
            1,
            Path.Combine(root, "session"),
            DateTimeOffset.UtcNow.AddMinutes(5));
        Directory.CreateDirectory(descriptor.SessionRoot);
        var descriptorPath = Path.Combine(root, "broker.json");
        File.WriteAllText(
            descriptorPath,
            JsonSerializer.Serialize(descriptor, BrokerSessionDescriptor.JsonOptions));
        var session = BrokerSession.Load(descriptorPath, DateTimeOffset.UtcNow);
        session.Open(
            "session-edit-safe",
            new HashSet<string>(
                [RuAgent.Broker.Policy.ToolScope.WorkspaceWrite],
                StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));
        var service = new ToolExecutionService(session, workspaceRoot);
        try
        {
            File.WriteAllText(Path.Combine(workspaceRoot, "a.txt"), "hello world");
            var envelope = new AgentEnvelope(
                AgentEnvelopeCodec.ProtocolVersion,
                "req-" + Guid.NewGuid().ToString("N"),
                AgentMessageTypes.ToolRequest,
                "session-edit-safe",
                null,
                30_000,
                new JsonObject
                {
                    ["tool_id"] = "files.edit",
                    ["tool_version"] = "1.0.0",
                    ["capability_class"] = "tool",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = "a.txt",
                        ["old_string"] = "world",
                        ["new_string"] = "Ru",
                    },
                });

            var result = await service.ExecuteAsync(envelope, CancellationToken.None);

            Assert.Equal("approval_required", result.Status);
            Assert.Equal(
                "Chỉnh sửa file.",
                result.Output["safe_action"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Schema_validator_rejects_missing_required_property()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("operation"),
            ["properties"] = new JsonObject
            {
                ["operation"] = new JsonObject { ["type"] = "string" },
            },
        };

        var error = Assert.Throws<ToolSchemaException>(() =>
            ToolSchemaValidator.Validate(schema, new JsonObject()));

        Assert.Equal("invalid_arguments", error.Code);
    }

    [Fact]
    public void Schema_validator_rejects_unsupported_keywords()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["oneOf"] = new JsonArray(),
        };

        var error = Assert.Throws<ToolSchemaException>(() =>
            ToolSchemaValidator.Validate(schema, new JsonObject()));

        Assert.Equal("unsupported_tool_schema", error.Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Schema_validator_enforces_integer_bounds(int level)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["level"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["minimum"] = 0,
                    ["maximum"] = 100,
                },
            },
        };

        var error = Assert.Throws<ToolSchemaException>(() =>
            ToolSchemaValidator.Validate(schema, new JsonObject { ["level"] = level }));

        Assert.Equal("invalid_arguments", error.Code);
    }

    [Fact]
    public void Schema_validator_accepts_an_in_range_integer()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["level"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["minimum"] = 0,
                    ["maximum"] = 100,
                },
            },
        };

        ToolSchemaValidator.Validate(schema, new JsonObject { ["level"] = 2 });
    }

    [Fact]
    public async Task Approval_respond_with_stale_id_never_invokes_adapter()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-stale-approval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspaceRoot = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspaceRoot);
        var descriptor = new BrokerSessionDescriptor(
            "http://127.0.0.1:18086",
            "broker-token",
            1,
            Path.Combine(root, "session"),
            DateTimeOffset.UtcNow.AddMinutes(5));
        Directory.CreateDirectory(descriptor.SessionRoot);
        var descriptorPath = Path.Combine(root, "broker.json");
        File.WriteAllText(
            descriptorPath,
            System.Text.Json.JsonSerializer.Serialize(descriptor, BrokerSessionDescriptor.JsonOptions));
        var session = BrokerSession.Load(descriptorPath, DateTimeOffset.UtcNow);
        session.Open("session-stale", new HashSet<string>([RuAgent.Broker.Policy.ToolScope.TerminalExecute], StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
        var service = new ToolExecutionService(session, workspaceRoot);
        try
        {
            var result = await service.RespondToApprovalAsync(
                new AgentEnvelope(
                    AgentEnvelopeCodec.ProtocolVersion,
                    "req-stale",
                    AgentMessageTypes.ApprovalResponse,
                    "session-stale",
                    null,
                    30_000,
                    new System.Text.Json.Nodes.JsonObject { ["approval_id"] = "nonexistent", ["approved"] = true }),
                CancellationToken.None);
            Assert.Equal("approval_stale", result.ErrorCode);
            Assert.False(result.AdapterInvoked);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Approved_recognized_terminal_run_executes_with_streaming_receipt_fields()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-terminal-recognized-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspaceRoot = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspaceRoot);
        File.WriteAllText(Path.Combine(workspaceRoot, "listed.txt"), "x");
        var descriptor = new BrokerSessionDescriptor(
            "http://127.0.0.1:18086",
            "broker-token",
            1,
            Path.Combine(root, "session"),
            DateTimeOffset.UtcNow.AddMinutes(5));
        Directory.CreateDirectory(descriptor.SessionRoot);
        var descriptorPath = Path.Combine(root, "broker.json");
        File.WriteAllText(
            descriptorPath,
            JsonSerializer.Serialize(descriptor, BrokerSessionDescriptor.JsonOptions));
        var session = BrokerSession.Load(descriptorPath, DateTimeOffset.UtcNow);
        session.Open(
            "session-terminal-recognized",
            new HashSet<string>(
                [RuAgent.Broker.Policy.ToolScope.TerminalExecute, RuAgent.Broker.Policy.ToolScope.TerminalNetwork],
                StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));
        var service = new ToolExecutionService(session, workspaceRoot);
        try
        {
            var pending = await service.ExecuteAsync(
                TerminalRequest("session-terminal-recognized", new JsonObject
                {
                    ["executable"] = "rg",
                    ["arguments"] = new JsonArray("--files"),
                }),
                CancellationToken.None);
            Assert.Equal(ToolResultStatuses.ApprovalRequired, pending.Status);
            Assert.False(pending.AdapterInvoked);

            var result = await service.RespondToApprovalAsync(
                ApprovalResponse("session-terminal-recognized", pending),
                CancellationToken.None);

            Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
            Assert.True(result.AdapterInvoked);
            Assert.Equal(0, result.Output["exit_code"]!.GetValue<int>());
            Assert.Contains("listed.txt", result.Output["stdout_head"]!.GetValue<string>());
            Assert.True(result.Output["stdout_bytes"]!.GetValue<long>() > 0);
            Assert.Equal(
                Path.GetFullPath(workspaceRoot),
                result.Output["working_directory"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Terminal_timeout_over_the_hard_maximum_denies_before_the_adapter()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-terminal-max-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspaceRoot = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspaceRoot);
        var descriptor = new BrokerSessionDescriptor(
            "http://127.0.0.1:18086",
            "broker-token",
            1,
            Path.Combine(root, "session"),
            DateTimeOffset.UtcNow.AddMinutes(5));
        Directory.CreateDirectory(descriptor.SessionRoot);
        var descriptorPath = Path.Combine(root, "broker.json");
        File.WriteAllText(
            descriptorPath,
            JsonSerializer.Serialize(descriptor, BrokerSessionDescriptor.JsonOptions));
        var session = BrokerSession.Load(descriptorPath, DateTimeOffset.UtcNow);
        session.Open(
            "session-terminal-max",
            new HashSet<string>([RuAgent.Broker.Policy.ToolScope.TerminalExecute], StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));
        var service = new ToolExecutionService(session, workspaceRoot);
        try
        {
            // The v1 contract schema caps timeout_ms at the 30-minute hard maximum, so an
            // over-the-cap request is rejected during validation, before any adapter work.
            var result = await service.ExecuteAsync(
                TerminalRequest("session-terminal-max", new JsonObject
                {
                    ["executable"] = "rg",
                    ["arguments"] = new JsonArray("--files"),
                    ["timeout_ms"] = 1_900_000,
                }),
                CancellationToken.None);

            Assert.Equal(ToolResultStatuses.Failed, result.Status);
            Assert.False(result.AdapterInvoked);
            Assert.Equal("invalid_arguments", result.ErrorCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AgentEnvelope TerminalRequest(string sessionId, JsonObject arguments) => new(
        AgentEnvelopeCodec.ProtocolVersion,
        "req-" + Guid.NewGuid().ToString("N"),
        AgentMessageTypes.ToolRequest,
        sessionId,
        null,
        30_000,
        new JsonObject
        {
            ["tool_id"] = "terminal.run",
            ["tool_version"] = "1.0.0",
            ["capability_class"] = "internal",
            ["arguments"] = arguments,
        });

    private static AgentEnvelope ApprovalResponse(string sessionId, ToolResult pending) => new(
        AgentEnvelopeCodec.ProtocolVersion,
        "approval-" + Guid.NewGuid().ToString("N"),
        AgentMessageTypes.ApprovalResponse,
        sessionId,
        null,
        30_000,
        new JsonObject
        {
            ["approval_id"] = pending.Output["approval_id"]!.GetValue<string>(),
            ["approved"] = true,
        });

    [Fact]
    public async Task Tool_execute_without_valid_authorization_is_stale()
    {
        // V2 path: approval token must be valid bound to snapshot+intent; without it tool must not execute.
        // Here we simulate by trying to consume an ApprovalTokenService token with mismatched intent hash.
        var service = new RuAgent.Broker.Policy.ApprovalTokenService();
        var token = service.Issue("run_11111111111111111111111111111111", "session-1", "files.read", "1.0.0", "hash", TimeSpan.FromSeconds(30), snapshotVersion: 5, intentHash: new string('a', 64));
        var wrong = new RuAgent.Broker.Policy.ApprovalBinding("run_11111111111111111111111111111111", "session-1", "files.read", "1.0.0", "hash", DateTimeOffset.UtcNow.AddMinutes(5), SnapshotVersion: 5, IntentHash: new string('b', 64));
        Assert.False(service.TryConsume(token, wrong, out _));
    }
}
