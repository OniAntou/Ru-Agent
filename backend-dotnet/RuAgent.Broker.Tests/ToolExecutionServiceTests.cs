using System.Text.Json.Nodes;
using RuAgent.Broker.Configuration;
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
    public async Task Read_hashing_observes_tool_cancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-read-cancel-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var sessionRoot = Path.Combine(root, "session");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(sessionRoot);
        using (var sparse = File.Create(Path.Combine(workspace, "large.bin")))
        {
            sparse.SetLength(512L * 1024 * 1024);
        }

        var session = CreateSession(sessionRoot);
        const string sessionId = "session-read-cancel";
        session.Open(sessionId, new HashSet<string>([RuAgent.Broker.Policy.ToolScope.SystemRead], StringComparer.Ordinal));
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
            var result = await new ToolExecutionService(session, workspace).ExecuteAsync(
                new AgentEnvelope(
                    AgentEnvelopeCodec.ProtocolVersion,
                    "read-cancel-1",
                    AgentMessageTypes.ToolRequest,
                    sessionId,
                    null,
                    30_000,
                    new JsonObject
                    {
                        ["tool_id"] = "read",
                        ["tool_version"] = "1.0.0",
                        ["capability_class"] = "internal",
                        ["arguments"] = new JsonObject { ["path"] = "large.bin", ["limit"] = 1 },
                    }),
                cancellation.Token);

            Assert.Equal(ToolResultStatuses.Cancelled, result.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Help_docs_executes_without_workspace_or_approval()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-help-service-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var session = CreateSession(Path.Combine(root, "session"));
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
        var session = CreateSession(Path.Combine(root, "session"));
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
        var session = CreateSession(Path.Combine(root, "session"));
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
        var session = CreateSession(Path.Combine(root, "session"), now);
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
        var session = CreateSession(Path.Combine(root, "session"), now);
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
        var session = CreateSession(Path.Combine(root, "session"));
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
        var session = CreateSession(Path.Combine(root, "session"));
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
        var session = CreateSession(Path.Combine(root, "session"));
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
        var session = CreateSession(Path.Combine(root, "session"));
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
        var session = CreateSession(Path.Combine(root, "session"));
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
        var session = CreateSession(Path.Combine(root, "session"));
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
        var session = CreateSession(Path.Combine(root, "session"));
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
        var session = CreateSession(Path.Combine(root, "session"));
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

    /// <summary>
    /// A background run returns immediately with its execution id, pages live
    /// output while it runs, and stops on mode kill. The task is owned by the
    /// session registry, not by the tool call, so the call returns before the
    /// process finishes.
    /// </summary>
    [Fact]
    public async Task Background_terminal_task_starts_polls_live_output_and_kills()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-terminal-background-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspaceRoot = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspaceRoot);
        var session = CreateSession(Path.Combine(root, "session"));
        const string sessionId = "session-terminal-background";
        session.Open(
            sessionId,
            new HashSet<string>(
                [RuAgent.Broker.Policy.ToolScope.TerminalExecute],
                StringComparer.Ordinal));
        var service = new ToolExecutionService(session, workspaceRoot);
        try
        {
            var runRequestId = "req-bg-" + Guid.NewGuid().ToString("N");
            var started = await service.ExecuteAuthenticatedV2Async(
                TerminalV2Request(sessionId, runRequestId, new JsonObject
                {
                    ["mode"] = "run",
                    ["executable"] = "cmd.exe",
                    ["arguments"] = new JsonArray("/c", "echo bg-hello & ping -n 30 127.0.0.1 > nul"),
                    ["background"] = true,
                }),
                CancellationToken.None);

            Assert.Equal(ToolResultStatuses.Succeeded, started.Status);
            Assert.Equal(runRequestId, started.Output["execution_id"]!.GetValue<string>());
            Assert.Equal("running", started.Output["state"]!.GetValue<string>());
            Assert.Equal(runRequestId, started.Output["spool_id"]!.GetValue<string>());
            var listed = Assert.Single(service.HandleSessionTasks(sessionId, null)["tasks"]!.AsArray());
            Assert.Contains("bg-hello", listed!["command"]!.GetValue<string>());
            Assert.DoesNotContain("command", service.HandleSessionTasks("foreign-session", null).ToJsonString());

            // While the task runs, a read reports the live state. The output
            // itself may still be in flight under a loaded test host; the
            // completion test below pins the content.
            var live = await service.ExecuteAuthenticatedV2Async(
                TerminalV2Request(sessionId, "req-live-" + Guid.NewGuid().ToString("N"), new JsonObject
                {
                    ["mode"] = "read_output",
                    ["execution_id"] = runRequestId,
                }),
                CancellationToken.None);
            Assert.Equal(ToolResultStatuses.Succeeded, live.Status);
            Assert.Equal("running", live.Output["state"]!.GetValue<string>());

            var killed = await service.ExecuteAuthenticatedV2Async(
                TerminalV2Request(sessionId, "req-kill-" + Guid.NewGuid().ToString("N"), new JsonObject
                {
                    ["mode"] = "kill",
                    ["execution_id"] = runRequestId,
                }),
                CancellationToken.None);

            Assert.Equal(ToolResultStatuses.Succeeded, killed.Status);
            Assert.Equal("killing", killed.Output["state"]!.GetValue<string>());

            var final = await WaitForBackgroundStateAsync(service, sessionId, runRequestId, "killed");
            Assert.Equal("killed", final.Output["state"]!.GetValue<string>());
        }
        finally
        {
            await DeleteBackgroundRootAsync(root);
        }
    }

    [Fact]
    public async Task RemovingSessionReapsBackgroundProcessBeforeReturning()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-close-background-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var session = CreateSession(Path.Combine(root, "session"));
        const string sessionId = "session-close-background";
        session.Open(sessionId, new HashSet<string>([RuAgent.Broker.Policy.ToolScope.TerminalExecute], StringComparer.Ordinal));
        await using var service = new ToolExecutionService(session, root);
        var pidFile = Path.Combine(root, "pid.txt");
        try
        {
            var started = await service.ExecuteAuthenticatedV2Async(
                TerminalV2Request(sessionId, "req-close-background", new JsonObject
                {
                    ["mode"] = "run",
                    ["executable"] = TerminalTestSupport.PowerShellExecutable,
                    ["arguments"] = new JsonArray(TerminalTestSupport.PowerShellArgs(
                        $"[IO.File]::WriteAllText('{pidFile.Replace("'", "''")}', [string]$PID); Start-Sleep -Seconds 60")
                        .Select(value => JsonValue.Create(value)).ToArray()),
                    ["background"] = true,
                }), CancellationToken.None);
            Assert.Equal(ToolResultStatuses.Succeeded, started.Status);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(pidFile)) await Task.Delay(20, deadline.Token);
            // The file exists before PowerShell closes it; retry the read until the writer releases it.
            int processId;
            while (true)
            {
                try { processId = int.Parse(await File.ReadAllTextAsync(pidFile, deadline.Token)); break; }
                catch (Exception ex) when (ex is IOException or FormatException) { await Task.Delay(20, deadline.Token); }
            }
            using var process = System.Diagnostics.Process.GetProcessById(processId);

            await service.RemoveSessionAsync(sessionId);

            Assert.True(process.HasExited);
            Assert.Empty(service.HandleSessionTasks(sessionId, null)["tasks"]!.AsArray());
        }
        finally
        {
            await service.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Background_terminal_task_reports_exit_code_when_it_finishes()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-terminal-background-done-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspaceRoot = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspaceRoot);
        var session = CreateSession(Path.Combine(root, "session"));
        const string sessionId = "session-terminal-background-done";
        session.Open(
            sessionId,
            new HashSet<string>(
                [RuAgent.Broker.Policy.ToolScope.TerminalExecute],
                StringComparer.Ordinal));
        var service = new ToolExecutionService(session, workspaceRoot);
        try
        {
            var runRequestId = "req-bg-done-" + Guid.NewGuid().ToString("N");
            var started = await service.ExecuteAuthenticatedV2Async(
                TerminalV2Request(sessionId, runRequestId, new JsonObject
                {
                    ["mode"] = "run",
                    ["executable"] = "cmd.exe",
                    ["arguments"] = new JsonArray("/c", "echo bg-done"),
                    ["background"] = true,
                }),
                CancellationToken.None);

            Assert.Equal("running", started.Output["state"]!.GetValue<string>());

            var done = await WaitForBackgroundStateAsync(service, sessionId, runRequestId, "exited");
            Assert.Equal(0, done.Output["exit_code"]!.GetValue<int>());
            Assert.Contains("bg-done", done.Output["page"]!.GetValue<string>(), StringComparison.Ordinal);
        }
        finally
        {
            await DeleteBackgroundRootAsync(root);
        }
    }

    [Fact]
    public async Task Background_task_cap_and_unknown_ids_fail_closed()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-terminal-background-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspaceRoot = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspaceRoot);
        var session = CreateSession(Path.Combine(root, "session"));
        const string sessionId = "session-terminal-background-cap";
        session.Open(
            sessionId,
            new HashSet<string>(
                [RuAgent.Broker.Policy.ToolScope.TerminalExecute],
                StringComparer.Ordinal));
        var service = new ToolExecutionService(
            session,
            workspaceRoot,
            terminalConcurrencyOptions: new TerminalConcurrencyOptions(TerminalLimits.MaxBackgroundTasksPerSession));
        try
        {
            var ids = new List<string>();
            for (var index = 0; index < TerminalLimits.MaxBackgroundTasksPerSession; index++)
            {
                var id = $"req-bg-cap-{index}-" + Guid.NewGuid().ToString("N");
                ids.Add(id);
                var started = await service.ExecuteAuthenticatedV2Async(
                    TerminalV2Request(sessionId, id, new JsonObject
                    {
                        ["mode"] = "run",
                        ["executable"] = "cmd.exe",
                        ["arguments"] = new JsonArray("/c", "ping -n 30 127.0.0.1 > nul"),
                        ["background"] = true,
                    }),
                    CancellationToken.None);
                Assert.Equal("running", started.Output["state"]!.GetValue<string>());
            }

            var over = await service.ExecuteAuthenticatedV2Async(
                TerminalV2Request(sessionId, "req-bg-over-" + Guid.NewGuid().ToString("N"), new JsonObject
                {
                    ["mode"] = "run",
                    ["executable"] = "cmd.exe",
                    ["arguments"] = new JsonArray("/c", "ping -n 30 127.0.0.1 > nul"),
                    // Foreground launches reserve the same slot because they
                    // may outlive their wait window and become background.
                    ["timeout_ms"] = 1_000,
                }),
                CancellationToken.None);
            Assert.Equal(ToolResultStatuses.Failed, over.Status);
            Assert.Equal("background_task_limit_reached", over.ErrorCode);

            var unknown = await service.ExecuteAuthenticatedV2Async(
                TerminalV2Request(sessionId, "req-kill-unknown-" + Guid.NewGuid().ToString("N"), new JsonObject
                {
                    ["mode"] = "kill",
                    ["execution_id"] = "req-no-such-task",
                }),
                CancellationToken.None);
            Assert.Equal(ToolResultStatuses.Failed, unknown.Status);
            Assert.Equal("unknown_background_task", unknown.ErrorCode);

            foreach (var id in ids)
            {
                var kill = await service.ExecuteAuthenticatedV2Async(
                    TerminalV2Request(sessionId, "req-kill-" + Guid.NewGuid().ToString("N"), new JsonObject
                    {
                        ["mode"] = "kill",
                        ["execution_id"] = id,
                    }),
                    CancellationToken.None);
                Assert.Equal(ToolResultStatuses.Succeeded, kill.Status);
            }

            await WaitForBackgroundStateAsync(service, sessionId, ids[0], "killed");
            var notRunning = await service.ExecuteAuthenticatedV2Async(
                TerminalV2Request(sessionId, "req-kill-again-" + Guid.NewGuid().ToString("N"), new JsonObject
                {
                    ["mode"] = "kill",
                    ["execution_id"] = ids[0],
                }),
                CancellationToken.None);
            Assert.Equal(ToolResultStatuses.Failed, notRunning.Status);
            Assert.Equal("background_task_not_running", notRunning.ErrorCode);
        }
        finally
        {
            await DeleteBackgroundRootAsync(root);
        }
    }

    /// <summary>
    /// A foreground command that outlives its approved window is moved to the
    /// background instead of being killed; the receipt carries the execution id
    /// so the model can page or stop it.
    /// </summary>
    [Fact]
    public async Task Foreground_command_moves_to_background_after_its_window()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-terminal-move-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspaceRoot = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspaceRoot);
        var session = CreateSession(Path.Combine(root, "session"));
        const string sessionId = "session-terminal-move";
        session.Open(
            sessionId,
            new HashSet<string>(
                [RuAgent.Broker.Policy.ToolScope.TerminalExecute],
                StringComparer.Ordinal));
        var service = new ToolExecutionService(session, workspaceRoot);
        try
        {
            var runRequestId = "req-move-" + Guid.NewGuid().ToString("N");
            var started = await service.ExecuteAuthenticatedV2Async(
                TerminalV2Request(sessionId, runRequestId, new JsonObject
                {
                    ["mode"] = "run",
                    ["executable"] = "cmd.exe",
                    ["arguments"] = new JsonArray("/c", "ping -n 30 127.0.0.1 > nul"),
                    ["timeout_ms"] = 1_000,
                }),
                CancellationToken.None);

            Assert.Equal(ToolResultStatuses.Succeeded, started.Status);
            Assert.True(started.Output["moved_to_background"]!.GetValue<bool>());
            Assert.Equal(runRequestId, started.Output["execution_id"]!.GetValue<string>());
            Assert.Equal("running", started.Output["state"]!.GetValue<string>());

            var killed = await service.ExecuteAuthenticatedV2Async(
                TerminalV2Request(sessionId, "req-kill-move-" + Guid.NewGuid().ToString("N"), new JsonObject
                {
                    ["mode"] = "kill",
                    ["execution_id"] = runRequestId,
                }),
                CancellationToken.None);
            Assert.Equal(ToolResultStatuses.Succeeded, killed.Status);

            await WaitForBackgroundStateAsync(service, sessionId, runRequestId, "killed");
        }
        finally
        {
            await DeleteBackgroundRootAsync(root);
        }
    }

    /// <summary>
    /// The envelope deadline is a control-RPC budget, not a terminal bound: a
    /// command that outlives it still runs to its own timeout (previously it was
    /// cancelled at the envelope deadline, roughly ten seconds in production).
    /// </summary>
    [Fact]
    public async Task Envelope_deadline_does_not_cancel_a_longer_terminal_command()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-terminal-deadline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspaceRoot = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspaceRoot);
        var session = CreateSession(Path.Combine(root, "session"));
        const string sessionId = "session-terminal-deadline";
        session.Open(
            sessionId,
            new HashSet<string>(
                [RuAgent.Broker.Policy.ToolScope.TerminalExecute],
                StringComparer.Ordinal));
        var service = new ToolExecutionService(session, workspaceRoot);
        try
        {
            var envelope = new AgentEnvelope(
                AgentEnvelopeCodec.ProtocolVersionV2,
                "req-deadline-" + Guid.NewGuid().ToString("N"),
                AgentMessageTypes.ToolExecute,
                sessionId,
                null,
                2_000,
                new JsonObject
                {
                    ["tool_id"] = "bash",
                    ["tool_version"] = "1.0.0",
                    ["capability_class"] = "internal",
                    ["run_id"] = "run_" + Guid.NewGuid().ToString("N"),
                    ["arguments"] = new JsonObject
                    {
                        ["mode"] = "run",
                        ["executable"] = "cmd.exe",
                        ["arguments"] = new JsonArray("/c", "ping -n 4 127.0.0.1 > nul"),
                        ["timeout_ms"] = 120_000,
                    },
                });

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = await service.ExecuteAuthenticatedV2Async(envelope, CancellationToken.None);
            stopwatch.Stop();

            Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
            Assert.Equal(0, result.Output["exit_code"]!.GetValue<int>());
            Assert.True(
                stopwatch.ElapsedMilliseconds > 2_000,
                $"command was cancelled at the envelope deadline after {stopwatch.ElapsedMilliseconds}ms");
        }
        finally
        {
            await DeleteBackgroundRootAsync(root);
        }
    }

    private static async Task DeleteBackgroundRootAsync(string root)
    {
        // The killed process tree releases its working-directory handle a
        // moment after the parent exits; retry instead of racing the OS. The
        // window is generous because the suite runs many real processes in
        // parallel and termination is asynchronous.
        for (var attempt = 0; attempt < 300; attempt++)
        {
            try
            {
                Directory.Delete(root, recursive: true);
                return;
            }
            catch (IOException)
            {
                await Task.Delay(100);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(100);
            }
        }

        Directory.Delete(root, recursive: true);
    }

    private static async Task<ToolResult> WaitForBackgroundStateAsync(
        ToolExecutionService service,
        string sessionId,
        string executionId,
        string expectedState)
    {
        ToolResult? last = null;
        for (var attempt = 0; attempt < 300; attempt++)
        {
            var page = await service.ExecuteAuthenticatedV2Async(
                TerminalV2Request(sessionId, "req-state-" + Guid.NewGuid().ToString("N"), new JsonObject
                {
                    ["mode"] = "read_output",
                    ["execution_id"] = executionId,
                }),
                CancellationToken.None);
            last = page;
            if (page.Status == ToolResultStatuses.Succeeded
                && page.Output["state"] is System.Text.Json.Nodes.JsonNode state
                && string.Equals(state.GetValue<string>(), expectedState, StringComparison.Ordinal))
            {
                return page;
            }

            await Task.Delay(50);
        }

        throw new Xunit.Sdk.XunitException(
            $"Background task never reached state '{expectedState}' (last={last?.Status}/{last?.ErrorCode}/{last?.Output["state"]?.GetValue<string>()}).");
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

    /// <summary>
    /// Each execution service cleans only its own configured spool root: an
    /// embedded runtime must be able to start without deleting another
    /// runtime's spilled terminal output.
    /// </summary>
    [Fact]
    public void CleanStaleSpoolFiles_only_cleans_the_configured_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-spool-isolation-" + Guid.NewGuid().ToString("N"));
        var spoolA = Path.Combine(root, "spool-a");
        var spoolB = Path.Combine(root, "spool-b");
        var workspaceA = Path.Combine(root, "workspace-a");
        var workspaceB = Path.Combine(root, "workspace-b");
        Directory.CreateDirectory(spoolA);
        Directory.CreateDirectory(spoolB);
        Directory.CreateDirectory(workspaceA);
        Directory.CreateDirectory(workspaceB);
        File.WriteAllText(Path.Combine(spoolA, "stale-a.log"), "a");
        File.WriteAllText(Path.Combine(spoolB, "stale-b.log"), "b");
        try
        {
            var session = BrokerSession.CreateForHost(
                Path.Combine(root, "session"),
                DateTimeOffset.UtcNow.AddMinutes(30));
            var serviceA = new ToolExecutionService(session, workspaceA, terminalSpoolRoot: spoolA);
            var serviceB = new ToolExecutionService(session, workspaceB, terminalSpoolRoot: spoolB);

            serviceA.CleanStaleSpoolFiles();

            Assert.False(Directory.Exists(spoolA));
            Assert.True(File.Exists(Path.Combine(spoolB, "stale-b.log")));
            serviceB.CleanStaleSpoolFiles();
            Assert.False(Directory.Exists(spoolB));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }


    /// <summary>
    /// The descriptor-free session the in-process execution authority runs on:
    /// a temp root and a bounded lifetime, no broker.json and no bearer.
    /// </summary>
    private static BrokerSession CreateSession(string sessionRoot, DateTimeOffset? now = null)
    {
        Directory.CreateDirectory(sessionRoot);
        return BrokerSession.CreateForHost(
            sessionRoot,
            (now ?? DateTimeOffset.UtcNow).AddMinutes(5),
            now: now);
    }

    private static (string Root, string SessionId, string RunId, BrokerSession Session, ToolExecutionService Service) CreateFullAccessService(string prefix) =>
        CreateLeaseService(prefix, EnvelopeTestSnapshots.FullAccessEmpty());

    private static (string Root, string SessionId, string RunId, BrokerSession Session, ToolExecutionService Service) CreateLeaseService(
        string prefix,
        BrokerPolicySnapshotWire snapshot)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var session = CreateSession(Path.Combine(root, "session"));
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

    /// <summary>
    /// A failed file call names the repair, not just the failure: the delete
    /// turn that met a directory died with a bare code and took the run with
    /// it, because the model could not tell a directory from a missing file.
    /// </summary>
    [Fact]
    public async Task Failed_file_calls_carry_repair_hints()
    {
        var fixture = CreateFullAccessService("ru-io-hint-");
        var workspace = Path.Combine(fixture.Root, "workspace");
        try
        {
            Directory.CreateDirectory(Path.Combine(workspace, "adir"));
            File.WriteAllText(Path.Combine(workspace, "taken.txt"), "x");

            var deleted = await fixture.Service.ExecuteAuthenticatedV2Async(
                WriteEnvelope(fixture, "ru-io-1", new JsonObject
                {
                    ["op"] = "delete",
                    ["path"] = "adir",
                }),
                CancellationToken.None);

            Assert.Equal(ToolResultStatuses.Failed, deleted.Status);
            Assert.Equal("is_directory", deleted.ErrorCode);
            Assert.Contains("directory", deleted.SafeSummary, StringComparison.OrdinalIgnoreCase);

            var missing = await fixture.Service.ExecuteAuthenticatedV2Async(
                WriteEnvelope(fixture, "ru-io-2", new JsonObject
                {
                    ["op"] = "delete",
                    ["path"] = "nope.txt",
                }),
                CancellationToken.None);

            Assert.Equal(ToolResultStatuses.Failed, missing.Status);
            Assert.Equal("file_not_found", missing.ErrorCode);
            Assert.Contains("does not exist", missing.SafeSummary, StringComparison.OrdinalIgnoreCase);

            var exists = await fixture.Service.ExecuteAuthenticatedV2Async(
                WriteEnvelope(fixture, "ru-io-3", new JsonObject
                {
                    ["op"] = "write",
                    ["path"] = "taken.txt",
                    ["content"] = "new",
                }),
                CancellationToken.None);

            Assert.Equal(ToolResultStatuses.Failed, exists.Status);
            Assert.Equal("file_exists", exists.ErrorCode);
            Assert.Contains("op=write", exists.SafeSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("overwrite", exists.SafeSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("same path and content", exists.SafeSummary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    /// <summary>
    /// Output past the in-memory window must stay retrievable: it spills to a
    /// session spool file and pages back through the same read_output call,
    /// whose receipt names the file.
    /// </summary>
    [Fact]
    public async Task Spilled_terminal_output_pages_past_the_in_memory_window()
    {
        var fixture = CreateFullAccessService("ru-spool-spill-");
        try
        {
            var run = await fixture.Service.ExecuteAuthenticatedV2Async(
                new AgentEnvelope(
                    AgentEnvelopeCodec.ProtocolVersionV2,
                    "spill-run",
                    AgentMessageTypes.ToolExecute,
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
                            ["executable"] = "python",
                            ["arguments"] = new JsonArray(
                                "-c",
                                "import sys;sys.stdout.buffer.write(b'0123456789012345678901234567890123456789\\n'*20000)"),
                            ["working_directory"] = ".",
                        },
                    }),
                CancellationToken.None);
            Assert.Equal(ToolResultStatuses.Succeeded, run.Status);
            Assert.True(run.Output.ContainsKey("spool_path"), run.Output.ToJsonString());

            var page = await fixture.Service.ExecuteAuthenticatedV2Async(
                new AgentEnvelope(
                    AgentEnvelopeCodec.ProtocolVersionV2,
                    "spill-page",
                    AgentMessageTypes.ToolExecute,
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
                            ["mode"] = "read_output",
                            ["execution_id"] = "spill-run",
                            ["offset"] = 400_000,
                            ["limit"] = 1000,
                        },
                    }),
                CancellationToken.None);
            Assert.Equal(ToolResultStatuses.Succeeded, page.Status);
            Assert.Equal(820_000, page.Output["total_chars"]!.GetValue<int>());
            Assert.StartsWith("456789", page.Output["page"]!.GetValue<string>());
            Assert.True(page.Output.ContainsKey("spool_path"));
        }
        finally
        {
            await fixture.Service.RemoveSessionAsync(fixture.SessionId);
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    private static AgentEnvelope WriteEnvelope(
        (string Root, string SessionId, string RunId, BrokerSession Session, ToolExecutionService Service) fixture,
        string requestId,
        JsonObject arguments) => new(
            AgentEnvelopeCodec.ProtocolVersionV2,
            requestId,
            AgentMessageTypes.ToolExecute,
            fixture.SessionId,
            null,
            30_000,
            new JsonObject
            {
                ["tool_id"] = "write",
                ["tool_version"] = "1.0.0",
                ["capability_class"] = "internal",
                ["run_id"] = fixture.RunId,
                ["arguments"] = arguments,
            });

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
