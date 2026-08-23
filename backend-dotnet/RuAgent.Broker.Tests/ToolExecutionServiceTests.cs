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
    public async Task Unrestricted_terminal_runs_report_outside_root_working_directories()
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
                        ["network"] = true,
                    },
                });

            var result = await service.ExecuteAsync(envelope, CancellationToken.None);

            Assert.Equal("succeeded", result.Status);
            Assert.Null(result.ErrorCode);
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
    public async Task Full_access_shell_executes_without_approval_or_token()
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
                        ["shell"] = "powershell",
                        ["script"] = "Get-Date",
                        ["working_directory"] = outsideDirectory,
                    },
                });

            var result = await service.ExecuteAsync(envelope, CancellationToken.None);

            Assert.Equal("succeeded", result.Status);
            Assert.Null(result.ErrorCode);
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
                        ["shell"] = "powershell",
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
}
