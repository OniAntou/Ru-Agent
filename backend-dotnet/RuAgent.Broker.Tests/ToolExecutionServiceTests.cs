using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using BrokerRunLease = RuAgent.Broker.Runtime.BrokerRunLease;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class ToolExecutionServiceTests
{
    [Fact]
    public async Task Help_docs_executes_without_workspace_or_approval()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-help-service-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var descriptor = new BrokerSessionDescriptor(
            "http://127.0.0.1:18086",
            "broker-token",
            1,
            Path.Combine(root, "session"),
            DateTimeOffset.UtcNow.AddMinutes(5));
        Directory.CreateDirectory(descriptor.SessionRoot);
        var descriptorPath = Path.Combine(root, "broker.json");
        File.WriteAllText(descriptorPath, JsonSerializer.Serialize(descriptor, BrokerSessionDescriptor.JsonOptions));
        var session = BrokerSession.Load(descriptorPath, DateTimeOffset.UtcNow);
        session.Open(
            "session-help",
            new HashSet<string>([RuAgent.Broker.Policy.ToolScope.WorkspaceRead, RuAgent.Broker.Policy.ToolScope.SystemRead], StringComparer.Ordinal));
        var workspace = Path.Combine(root, "empty-workspace");
        Directory.CreateDirectory(workspace);
        try
        {
            var service = new ToolExecutionService(session, workspace);
            var result = await service.ExecuteAsync(
                new AgentEnvelope(
                    AgentEnvelopeCodec.ProtocolVersion,
                    "help-" + Guid.NewGuid().ToString("N"),
                    AgentMessageTypes.ToolRequest,
                    "session-help",
                    null,
                    30_000,
                    new JsonObject
                    {
                        ["tool_id"] = "help",
                        ["tool_version"] = "1.0.0",
                        ["capability_class"] = "internal",
                        ["run_id"] = "session-help",
                        ["arguments"] = new JsonObject { ["query"] = "approvals", ["max_results"] = 1 },
                    }),
                CancellationToken.None);

            Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
            Assert.Equal("approvals", result.Output["query"]!.GetValue<string>());
            Assert.True(result.Output["results"] is JsonArray);
            Assert.False(result.Output.ContainsKey("approval_id"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Full_access_external_read_uses_ephemeral_root_without_session_grant()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-full-read-" + Guid.NewGuid().ToString("N"));
        var workspaceRoot = Path.Combine(root, "workspace");
        var externalRoot = Path.Combine(root, "external");
        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(externalRoot);
        File.WriteAllText(Path.Combine(externalRoot, "probe.txt"), "external-content");
        var descriptor = new BrokerSessionDescriptor(
            "http://127.0.0.1:18086",
            "broker-token",
            1,
            Path.Combine(root, "session"),
            DateTimeOffset.UtcNow.AddMinutes(5));
        Directory.CreateDirectory(descriptor.SessionRoot);
        var descriptorPath = Path.Combine(root, "broker.json");
        File.WriteAllText(descriptorPath, JsonSerializer.Serialize(descriptor, BrokerSessionDescriptor.JsonOptions));
        var session = BrokerSession.Load(descriptorPath);
        var snapshot = EnvelopeTestSnapshots.FullAccessEmpty();
        const string sessionId = "session-full-read";
        const string runId = "run_full_read_11111111111111";
        var lease = BrokerRunLease.CreateValidated(
            runId,
            sessionId,
            snapshot.Workspace.WorkspaceId,
            workspaceRoot,
            snapshot.Workspace.VolumeSerialNumber,
            snapshot.Workspace.RootFileId,
            snapshot.Workspace.DisplayIdentityHash,
            snapshot.Preset,
            snapshot.Version,
            EnvelopeTestSnapshots.HashOf(snapshot),
            DateTimeOffset.UtcNow.AddMinutes(5),
            snapshot);
        Assert.True(session.TryOpenRunLease(lease, out var leaseError), leaseError);
        try
        {
            var service = new ToolExecutionService(session, workspaceRoot);
            var result = await service.ExecuteAuthenticatedV2Async(
                new AgentEnvelope(
                    AgentEnvelopeCodec.ProtocolVersionV2,
                    "read-full-1",
                    AgentMessageTypes.ToolExecute,
                    sessionId,
                    null,
                    30_000,
                    new JsonObject
                    {
                        ["tool_id"] = "read",
                        ["tool_version"] = "1.0.0",
                        ["capability_class"] = "internal",
                        ["run_id"] = runId,
                        ["arguments"] = new JsonObject
                        {
                            ["path"] = Path.Combine(externalRoot, "probe.txt"),
                        },
                    }),
                CancellationToken.None);

            Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
            Assert.Equal("external-content", result.Output["content"]!.GetValue<string>());
            Assert.Empty(session.DirectoryGrants.GetGrantedDirectories(sessionId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Full_access_external_roots_are_not_retained_in_global_toolset_cache()
    {
        var fixture = CreateFullAccessService("ru-full-cache-");
        try
        {
            for (var index = 0; index < 2; index++)
            {
                var external = Path.Combine(fixture.Root, "external-" + index);
                Directory.CreateDirectory(external);
                File.WriteAllText(Path.Combine(external, "probe.txt"), "cache-marker-" + index);
                var result = await fixture.Service.ExecuteAuthenticatedV2Async(
                    new AgentEnvelope(
                        AgentEnvelopeCodec.ProtocolVersionV2,
                        "full-cache-" + index,
                        AgentMessageTypes.ToolExecute,
                        fixture.SessionId,
                        null,
                        30_000,
                        new JsonObject
                        {
                            ["tool_id"] = "read",
                            ["tool_version"] = "1.0.0",
                            ["capability_class"] = "internal",
                            ["run_id"] = fixture.RunId,
                            ["arguments"] = new JsonObject
                            {
                                ["path"] = Path.Combine(external, "probe.txt"),
                            },
                        }),
                    CancellationToken.None);
                Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
            }

            Assert.Equal(1, fixture.Service.CachedRootToolSetCount);
        }
        finally
        {
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    [Fact]
    public async Task Authenticated_service_marker_does_not_authorize_a_legacy_envelope()
    {
        var fixture = CreateFullAccessService("ru-marker-legacy-");
        try
        {
            var external = Path.Combine(fixture.Root, "external");
            Directory.CreateDirectory(external);
            File.WriteAllText(Path.Combine(external, "probe.txt"), "must-not-be-read");
            var result = await fixture.Service.ExecuteAuthenticatedV2Async(
                new AgentEnvelope(
                    AgentEnvelopeCodec.ProtocolVersion,
                    "marker-legacy-read",
                    AgentMessageTypes.ToolRequest,
                    fixture.SessionId,
                    null,
                    30_000,
                    new JsonObject
                    {
                        ["tool_id"] = "read",
                        ["tool_version"] = "1.0.0",
                        ["run_id"] = fixture.RunId,
                        ["authorization_token"] = "forged-token",
                        ["arguments"] = new JsonObject
                        {
                            ["path"] = Path.Combine(external, "probe.txt"),
                        },
                    }),
                CancellationToken.None);

            Assert.Equal(ToolResultStatuses.Failed, result.Status);
            Assert.Equal("outside_workspace", result.ErrorCode);
        }
        finally
        {
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    [Fact]
    public async Task Legacy_tool_request_ignores_forged_full_access_fields_for_external_read()
    {
        var fixture = CreateFullAccessService("ru-legacy-read-");
        try
        {
            var external = Path.Combine(fixture.Root, "external");
            Directory.CreateDirectory(external);
            File.WriteAllText(Path.Combine(external, "probe.txt"), "must-not-be-read");
            var result = await fixture.Service.ExecuteAsync(
                new AgentEnvelope(
                    AgentEnvelopeCodec.ProtocolVersion,
                    "legacy-read-forged",
                    AgentMessageTypes.ToolRequest,
                    fixture.SessionId,
                    null,
                    30_000,
                    new JsonObject
                    {
                        ["tool_id"] = "read",
                        ["tool_version"] = "1.0.0",
                        ["capability_class"] = "internal",
                        ["run_id"] = fixture.RunId,
                        ["authorization_token"] = "forged-token",
                        ["arguments"] = new JsonObject
                        {
                            ["path"] = Path.Combine(external, "probe.txt"),
                        },
                    }),
                CancellationToken.None);

            Assert.Equal(ToolResultStatuses.Failed, result.Status);
            Assert.Equal("outside_workspace", result.ErrorCode);
        }
        finally
        {
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    [Fact]
    public async Task Legacy_tool_request_ignores_forged_full_access_fields_for_external_terminal()
    {
        var fixture = CreateFullAccessService("ru-legacy-terminal-");
        try
        {
            var external = Path.Combine(fixture.Root, "external");
            Directory.CreateDirectory(external);
            var result = await fixture.Service.ExecuteAsync(
                new AgentEnvelope(
                    AgentEnvelopeCodec.ProtocolVersion,
                    "legacy-terminal-forged",
                    AgentMessageTypes.ToolRequest,
                    fixture.SessionId,
                    null,
                    30_000,
                    new JsonObject
                    {
                        ["tool_id"] = "bash",
                        ["tool_version"] = "1.0.0",
                        ["capability_class"] = "internal",
                        ["run_id"] = fixture.RunId,
                        ["authorization_token"] = "forged-token",
                        ["arguments"] = new JsonObject
                        {
                            ["mode"] = "run",
                            ["executable"] = "dotnet",
                            ["arguments"] = new JsonArray("--version"),
                            ["working_directory"] = external,
                        },
                    }),
                CancellationToken.None);

            Assert.Equal(ToolResultStatuses.ApprovalRequired, result.Status);
            Assert.False(result.AdapterInvoked);
        }
        finally
        {
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    [Fact]
    public async Task Legacy_approval_response_keeps_external_terminal_confined()
    {
        var fixture = CreateFullAccessService("ru-legacy-approval-");
        try
        {
            var external = Path.Combine(fixture.Root, "external");
            Directory.CreateDirectory(external);
            var pending = await fixture.Service.ExecuteAsync(
                new AgentEnvelope(
                    AgentEnvelopeCodec.ProtocolVersion,
                    "legacy-terminal-pending",
                    AgentMessageTypes.ToolRequest,
                    fixture.SessionId,
                    null,
                    30_000,
                    new JsonObject
                    {
                        ["tool_id"] = "bash",
                        ["tool_version"] = "1.0.0",
                        ["capability_class"] = "internal",
                        ["run_id"] = fixture.RunId,
                        ["arguments"] = new JsonObject
                        {
                            ["mode"] = "run",
                            ["executable"] = "dotnet",
                            ["arguments"] = new JsonArray("--version"),
                            ["working_directory"] = external,
                        },
                    }),
                CancellationToken.None);
            Assert.Equal(ToolResultStatuses.ApprovalRequired, pending.Status);

            var result = await fixture.Service.RespondToApprovalAsync(
                new AgentEnvelope(
                    AgentEnvelopeCodec.ProtocolVersion,
                    "legacy-terminal-approval",
                    AgentMessageTypes.ApprovalResponse,
                    fixture.SessionId,
                    null,
                    30_000,
                    new JsonObject
                    {
                        ["approval_id"] = pending.Output["approval_id"]!.GetValue<string>(),
                        ["approved"] = true,
                    }),
                CancellationToken.None);

            Assert.Equal(ToolResultStatuses.Failed, result.Status);
            Assert.Equal("workspace_path_outside_root", result.ErrorCode);
        }
        finally
        {
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    [Fact]
    public async Task Standard_lease_legacy_token_does_not_skip_terminal_approval()
    {
        var fixture = CreateLeaseService("ru-standard-authority-", EnvelopeTestSnapshots.StandardEmpty());
        try
        {
            var external = Path.Combine(fixture.Root, "external");
            Directory.CreateDirectory(external);
            var result = await fixture.Service.ExecuteAsync(
                new AgentEnvelope(
                    AgentEnvelopeCodec.ProtocolVersion,
                    "standard-terminal-forged",
                    AgentMessageTypes.ToolRequest,
                    fixture.SessionId,
                    null,
                    30_000,
                    new JsonObject
                    {
                        ["tool_id"] = "bash",
                        ["tool_version"] = "1.0.0",
                        ["capability_class"] = "internal",
                        ["run_id"] = fixture.RunId,
                        ["authorization_token"] = "forged-token",
                        ["arguments"] = new JsonObject
                        {
                            ["mode"] = "run",
                            ["executable"] = "dotnet",
                            ["arguments"] = new JsonArray("--version"),
                            ["working_directory"] = external,
                        },
                    }),
                CancellationToken.None);

            Assert.Equal(ToolResultStatuses.ApprovalRequired, result.Status);
            Assert.False(result.AdapterInvoked);
        }
        finally
        {
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    [Fact]
    public async Task No_lease_legacy_token_does_not_skip_terminal_approval()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-no-lease-authority-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var external = Path.Combine(root, "external");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(external);
        var descriptor = new BrokerSessionDescriptor(
            "http://127.0.0.1:18086",
            "broker-token",
            1,
            Path.Combine(root, "session"),
            DateTimeOffset.UtcNow.AddMinutes(5));
        Directory.CreateDirectory(descriptor.SessionRoot);
        var descriptorPath = Path.Combine(root, "broker.json");
        File.WriteAllText(descriptorPath, JsonSerializer.Serialize(descriptor, BrokerSessionDescriptor.JsonOptions));
        var session = BrokerSession.Load(descriptorPath);
        session.Open(
            "session-no-lease",
            new HashSet<string>([RuAgent.Broker.Policy.ToolScope.TerminalExecute], StringComparer.Ordinal));
        var service = new ToolExecutionService(session, workspace);
        try
        {
            var result = await service.ExecuteAsync(
                new AgentEnvelope(
                    AgentEnvelopeCodec.ProtocolVersion,
                    "no-lease-terminal-forged",
                    AgentMessageTypes.ToolRequest,
                    "session-no-lease",
                    null,
                    30_000,
                    new JsonObject
                    {
                        ["tool_id"] = "bash",
                        ["tool_version"] = "1.0.0",
                        ["capability_class"] = "internal",
                        ["run_id"] = "forged-run",
                        ["authorization_token"] = "forged-token",
                        ["arguments"] = new JsonObject
                        {
                            ["mode"] = "run",
                            ["executable"] = "dotnet",
                            ["arguments"] = new JsonArray("--version"),
                            ["working_directory"] = external,
                        },
                    }),
                CancellationToken.None);

            Assert.Equal(ToolResultStatuses.ApprovalRequired, result.Status);
            Assert.False(result.AdapterInvoked);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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
                [RuAgent.Broker.Policy.ToolScope.TerminalExecute],
                StringComparer.Ordinal));        session.DirectoryGrants.Add(
            "session-terminal-unrestricted",
            RuAgent.Broker.Policy.CanonicalDirectoryIdentity.CreateExisting(outsideDirectory));
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
                        ["tool_id"] = "bash",
                        ["tool_version"] = "1.0.0",
                        ["capability_class"] = "internal",
                        ["arguments"] = new JsonObject { ["mode"] = "run", ["network"] = true },
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
                    ["tool_id"] = "bash",
                    ["tool_version"] = "1.0.0",
                    ["capability_class"] = "internal",
                    ["arguments"] = new JsonObject
                    {
                        ["mode"] = "run",
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
            Assert.True(
                result.Output["working_directory"]!.GetValue<string>() is "."
                || string.Equals(
                    result.Output["working_directory"]!.GetValue<string>(),
                    Path.GetFullPath(outsideDirectory),
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Session_with_terminal_network_still_enforces_confinement_without_grant()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-terminal-confine-" + Guid.NewGuid().ToString("N"));
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
            "session-terminal-network-confined",
            new HashSet<string>(
                [RuAgent.Broker.Policy.ToolScope.TerminalExecute],
                StringComparer.Ordinal));        var service = new ToolExecutionService(session, workspaceRoot);
        try
        {
            var envelope = new AgentEnvelope(
                AgentEnvelopeCodec.ProtocolVersion,
                "req-" + Guid.NewGuid().ToString("N"),
                AgentMessageTypes.ToolRequest,
                "session-terminal-network-confined",
                null,
                30_000,
                new JsonObject
                {
                    ["tool_id"] = "bash",
                    ["tool_version"] = "1.0.0",
                    ["capability_class"] = "internal",
                    ["arguments"] = new JsonObject
                    {
                        ["mode"] = "run",
                        ["executable"] = "dotnet",
                        ["arguments"] = new JsonArray("--version"),
                        ["working_directory"] = outsideDirectory,
                    },
                });

            var pending = await service.ExecuteAsync(envelope, CancellationToken.None);
            Assert.Equal(ToolResultStatuses.ApprovalRequired, pending.Status);
            var approvalId = pending.Output["approval_id"]!.GetValue<string>();
            var result = await service.RespondToApprovalAsync(
                new AgentEnvelope(
                    AgentEnvelopeCodec.ProtocolVersion,
                    "approval-" + Guid.NewGuid().ToString("N"),
                    AgentMessageTypes.ApprovalResponse,
                    "session-terminal-network-confined",
                    null,
                    30_000,
                    new JsonObject
                    {
                        ["approval_id"] = approvalId,
                        ["approved"] = true,
                    }),
                CancellationToken.None);

            Assert.Equal(ToolResultStatuses.Failed, result.Status);
            Assert.Equal("workspace_path_outside_root", result.ErrorCode);
            Assert.True(result.AdapterInvoked);
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
                [RuAgent.Broker.Policy.ToolScope.TerminalExecute],
                StringComparer.Ordinal));        var service = new ToolExecutionService(session, workspaceRoot);
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
                    ["tool_id"] = "bash",
                    ["tool_version"] = "1.0.0",
                    ["capability_class"] = "internal",
                    ["arguments"] = new JsonObject
                    {
                        ["mode"] = "shell",
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
                StringComparer.Ordinal));        var service = new ToolExecutionService(session, workspaceRoot);
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
                    ["tool_id"] = "bash",
                    ["tool_version"] = "1.0.0",
                    ["capability_class"] = "internal",
                    ["arguments"] = new JsonObject
                    {
                        ["mode"] = "shell",
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
                StringComparer.Ordinal));        var service = new ToolExecutionService(session, workspaceRoot);
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
                    ["tool_id"] = "edit",
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
    public async Task Edit_approval_preview_does_not_read_hard_linked_content()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-edit-preview-hardlink-" + Guid.NewGuid().ToString("N"));
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
        File.WriteAllText(descriptorPath, JsonSerializer.Serialize(descriptor, BrokerSessionDescriptor.JsonOptions));
        var session = BrokerSession.Load(descriptorPath, DateTimeOffset.UtcNow);
        session.Open(
            "session-edit-preview-hardlink",
            new HashSet<string>([RuAgent.Broker.Policy.ToolScope.WorkspaceWrite], StringComparer.Ordinal));
        var service = new ToolExecutionService(session, workspaceRoot);
        var outside = Path.Combine(Path.GetTempPath(), "ru-edit-preview-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        const string content = "hard-link-preview-marker old";
        var target = Path.Combine(outside, "secret.txt");
        File.WriteAllText(target, content);
        var link = Path.Combine(workspaceRoot, "leak.txt");
        if (!FileReadAdapterTests.TryCreateHardLink(link, target))
        {
            Directory.Delete(outside, recursive: true);
            Directory.Delete(root, recursive: true);
            return;
        }

        try
        {
            var envelope = new AgentEnvelope(
                AgentEnvelopeCodec.ProtocolVersion,
                "req-" + Guid.NewGuid().ToString("N"),
                AgentMessageTypes.ToolRequest,
                "session-edit-preview-hardlink",
                null,
                30_000,
                new JsonObject
                {
                    ["tool_id"] = "edit",
                    ["tool_version"] = "1.0.0",
                    ["capability_class"] = "tool",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = "leak.txt",
                        ["old_string"] = "old",
                        ["new_string"] = "new",
                    },
                });

            var result = await service.ExecuteAsync(envelope, CancellationToken.None);

            Assert.Equal("approval_required", result.Status);
            var preview = result.Output[ApprovalPreviewFields.Field]!.GetValue<string>();
            Assert.DoesNotContain("hard-link-preview-marker", preview);
        }
        finally
        {
            try { File.Delete(link); } catch { }
            try { Directory.Delete(outside, recursive: true); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
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
        session.Open("session-stale", new HashSet<string>([RuAgent.Broker.Policy.ToolScope.TerminalExecute], StringComparer.Ordinal));
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
                [RuAgent.Broker.Policy.ToolScope.TerminalExecute],
                StringComparer.Ordinal));        var service = new ToolExecutionService(session, workspaceRoot);
        try
        {
            var pending = await service.ExecuteAsync(
                TerminalRequest("session-terminal-recognized", new JsonObject
                {
                    ["mode"] = "run",
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
            Assert.True(
                result.Output["working_directory"]!.GetValue<string>() is "."
                || string.Equals(
                    result.Output["working_directory"]!.GetValue<string>(),
                    Path.GetFullPath(workspaceRoot),
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A truncated receipt names its spool so the swallowed middle pages back
    /// without re-running the command. The read itself needs no approval, and
    /// an unknown spool id fails closed instead of asking.
    /// </summary>
    [Fact]
    public async Task Truncated_terminal_output_pages_back_from_spool_without_rerunning()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-terminal-spool-" + Guid.NewGuid().ToString("N"));
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
        const string sessionId = "session-terminal-spool";
        session.Open(
            sessionId,
            new HashSet<string>(
                [RuAgent.Broker.Policy.ToolScope.TerminalExecute],
                StringComparer.Ordinal));
        var service = new ToolExecutionService(session, workspaceRoot);
        try
        {
            var runRequestId = "req-spool-" + Guid.NewGuid().ToString("N");
            var result = await service.ExecuteAuthenticatedV2Async(
                TerminalV2Request(sessionId, runRequestId, new JsonObject
                {
                    ["mode"] = "run",
                    ["executable"] = "cmd.exe",
                    ["arguments"] = new JsonArray("/c", "for /L %i in (1,1,3000) do @echo output-line-padding-0123456789"),
                }),
                CancellationToken.None);

            Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
            Assert.True(result.Output["stdout_dropped_bytes"]!.GetValue<long>() > 0);
            Assert.Equal(runRequestId, result.Output["spool_id"]!.GetValue<string>());
            var totalChars = result.Output["stdout_bytes"]!.GetValue<long>();
            Assert.True(totalChars > 9000 + 100);

            var page = await service.ExecuteAuthenticatedV2Async(
                TerminalV2Request(sessionId, "req-page-" + Guid.NewGuid().ToString("N"), new JsonObject
                {
                    ["mode"] = "read_output",
                    ["execution_id"] = runRequestId,
                    ["offset"] = 9000,
                    ["limit"] = 100,
                }),
                CancellationToken.None);

            Assert.Equal(ToolResultStatuses.Succeeded, page.Status);
            Assert.True(page.AdapterInvoked);
            Assert.Contains("output-line-padding", page.Output["page"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Equal(9000, page.Output["offset"]!.GetValue<int>());

            var missing = await service.ExecuteAuthenticatedV2Async(
                TerminalV2Request(sessionId, "req-missing-" + Guid.NewGuid().ToString("N"), new JsonObject
                {
                    ["mode"] = "read_output",
                    ["execution_id"] = "req-no-such-spool",
                }),
                CancellationToken.None);

            Assert.Equal(ToolResultStatuses.Failed, missing.Status);
            Assert.Equal("unknown_spool_output", missing.ErrorCode);
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
            new HashSet<string>([RuAgent.Broker.Policy.ToolScope.TerminalExecute], StringComparer.Ordinal));        var service = new ToolExecutionService(session, workspaceRoot);
        try
        {
            // The v1 contract schema caps timeout_ms at the 30-minute hard maximum, so an
            // over-the-cap request is rejected during validation, before any adapter work.
            var result = await service.ExecuteAsync(
                TerminalRequest("session-terminal-max", new JsonObject
                {
                    ["mode"] = "run",
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

    private static AgentEnvelope TerminalRequest(string sessionId, JsonObject arguments, string? requestId = null) => new(
        AgentEnvelopeCodec.ProtocolVersion,
        requestId ?? "req-" + Guid.NewGuid().ToString("N"),
        AgentMessageTypes.ToolRequest,
        sessionId,
        null,
        30_000,
        new JsonObject
        {
            ["tool_id"] = "bash",
            ["tool_version"] = "1.0.0",
            ["capability_class"] = "internal",
            ["arguments"] = arguments,
        });

    /// <summary>
    /// The product path: V2 lease-authorized calls skip the manifest approval
    /// gate because the connection leg already evaluated policy.
    /// </summary>
    private static AgentEnvelope TerminalV2Request(string sessionId, string requestId, JsonObject arguments) => new(
        AgentEnvelopeCodec.ProtocolVersionV2,
        requestId,
        AgentMessageTypes.ToolExecute,
        sessionId,
        null,
        30_000,
        new JsonObject
        {
            ["tool_id"] = "bash",
            ["tool_version"] = "1.0.0",
            ["capability_class"] = "internal",
            ["run_id"] = "run_" + Guid.NewGuid().ToString("N"),
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

    private static (string Root, string SessionId, string RunId, BrokerSession Session, ToolExecutionService Service) CreateFullAccessService(string prefix) =>
        CreateLeaseService(prefix, EnvelopeTestSnapshots.FullAccessEmpty());

    private static (string Root, string SessionId, string RunId, BrokerSession Session, ToolExecutionService Service) CreateLeaseService(
        string prefix,
        BrokerPolicySnapshotWire snapshot)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var descriptor = new BrokerSessionDescriptor(
            "http://127.0.0.1:18086",
            "broker-token",
            1,
            Path.Combine(root, "session"),
            DateTimeOffset.UtcNow.AddMinutes(5));
        Directory.CreateDirectory(descriptor.SessionRoot);
        var descriptorPath = Path.Combine(root, "broker.json");
        File.WriteAllText(descriptorPath, JsonSerializer.Serialize(descriptor, BrokerSessionDescriptor.JsonOptions));
        var session = BrokerSession.Load(descriptorPath);
        var sessionId = "session-" + snapshot.Preset + "-" + Guid.NewGuid().ToString("N");
        var runId = "run_" + Guid.NewGuid().ToString("N");
        var lease = BrokerRunLease.CreateValidated(
            runId,
            sessionId,
            snapshot.Workspace.WorkspaceId,
            workspace,
            snapshot.Workspace.VolumeSerialNumber,
            snapshot.Workspace.RootFileId,
            snapshot.Workspace.DisplayIdentityHash,
            snapshot.Preset,
            snapshot.Version,
            EnvelopeTestSnapshots.HashOf(snapshot),
            DateTimeOffset.UtcNow.AddMinutes(5),
            snapshot);
        Assert.True(session.TryOpenRunLease(lease, out var leaseError), leaseError);
        return (root, sessionId, runId, session, new ToolExecutionService(session, workspace));
    }

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

    [Fact]
    public async Task Ambiguous_edit_fails_with_match_count_repair_hint()
    {
        var fixture = CreateFullAccessService("ru-edit-hint-");
        var workspace = Path.Combine(fixture.Root, "workspace");
        try
        {
            File.WriteAllText(Path.Combine(workspace, "dup.txt"), "val = 1\nval = 1\n");
            var result = await fixture.Service.ExecuteAuthenticatedV2Async(
                new AgentEnvelope(
                    AgentEnvelopeCodec.ProtocolVersionV2,
                    "edit-hint-1",
                    AgentMessageTypes.ToolExecute,
                    fixture.SessionId,
                    null,
                    30_000,
                    new JsonObject
                    {
                        ["tool_id"] = "edit",
                        ["tool_version"] = "1.0.0",
                        ["capability_class"] = "internal",
                        ["run_id"] = fixture.RunId,
                        ["arguments"] = new JsonObject
                        {
                            ["path"] = "dup.txt",
                            ["old_string"] = "val = 1",
                            ["new_string"] = "val = 2",
                        },
                    }),
                CancellationToken.None);

            Assert.Equal(ToolResultStatuses.Failed, result.Status);
            Assert.Equal("edit_not_unique", result.ErrorCode);
            Assert.Contains("2", result.SafeSummary, StringComparison.Ordinal);
            Assert.Equal("val = 1\nval = 1\n", File.ReadAllText(Path.Combine(workspace, "dup.txt")));
        }
        finally
        {
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_edit_target_fails_with_reread_hint()
    {
        var fixture = CreateFullAccessService("ru-edit-miss-");
        var workspace = Path.Combine(fixture.Root, "workspace");
        try
        {
            File.WriteAllText(Path.Combine(workspace, "one.txt"), "hello\n");
            var result = await fixture.Service.ExecuteAuthenticatedV2Async(
                new AgentEnvelope(
                    AgentEnvelopeCodec.ProtocolVersionV2,
                    "edit-miss-1",
                    AgentMessageTypes.ToolExecute,
                    fixture.SessionId,
                    null,
                    30_000,
                    new JsonObject
                    {
                        ["tool_id"] = "edit",
                        ["tool_version"] = "1.0.0",
                        ["capability_class"] = "internal",
                        ["run_id"] = fixture.RunId,
                        ["arguments"] = new JsonObject
                        {
                            ["path"] = "one.txt",
                            ["old_string"] = "absent",
                            ["new_string"] = "x",
                        },
                    }),
                CancellationToken.None);

            Assert.Equal(ToolResultStatuses.Failed, result.Status);
            Assert.Equal("edit_no_match", result.ErrorCode);
            Assert.Contains("Re-read", result.SafeSummary, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixture.Root, recursive: true);
        }
    }
}
