using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RuAgent.Broker.Tools.Desktop;
using RuAgent.Broker.Runtime;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class BrokerHostTests
{
    [Fact]
    public async Task Health_requires_the_descriptor_bearer()
    {
        await using var fixture = await HostFixture.StartAsync();
        using var client = new HttpClient();

        using var unauthorized = await client.GetAsync(fixture.Host.BaseUrl + "/health");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, fixture.Host.BaseUrl + "/health");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            fixture.Token);
        using var authorized = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        Assert.Contains("ready", await authorized.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authenticated_websocket_answers_health_with_the_private_protocol()
    {
        await using var fixture = await HostFixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await socket.ConnectAsync(
            new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"),
            cancellation.Token);
        var request = new AgentEnvelope(
            1,
            "health-1",
            AgentMessageTypes.Health,
            "session-1",
            "corr-1",
            1_000,
            new System.Text.Json.Nodes.JsonObject());
        var bytes = Encoding.UTF8.GetBytes(AgentEnvelopeCodec.Serialize(request));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellation.Token);

        var buffer = new byte[64 * 1024];
        var result = await socket.ReceiveAsync(buffer, cancellation.Token);
        var reply = AgentEnvelopeCodec.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));

        Assert.Equal(AgentMessageTypes.HealthOk, reply.MessageType);
        Assert.Equal(request.RequestId, reply.RequestId);
        Assert.Equal(request.CorrelationId, reply.CorrelationId);
    }

    [Fact]
    public async Task Hello_defaults_to_chat_and_advertises_only_workspace_read_tools()
    {
        await using var fixture = await HostFixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(
            new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"),
            cancellation.Token);

        await SendAsync(
            socket,
            new AgentEnvelope(1, "hello-chat", AgentMessageTypes.Hello, "session-chat", "corr-chat", 10_000, new System.Text.Json.Nodes.JsonObject()),
            cancellation.Token);
        var hello = await ReceiveAsync(socket, cancellation.Token);

        var ids = hello.Payload["tools"]!.AsArray()
            .Select(node => node!["tool_id"]!.GetValue<string>())
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "files.glob", "files.list", "files.read", "files.search" }, ids);
        Assert.Empty(hello.Payload["skills"]!.AsArray());
    }

    [Fact]
    public async Task Hello_with_agent_profile_advertises_mutating_capabilities()
    {
        await using var fixture = await HostFixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(
            new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"),
            cancellation.Token);

        await SendAsync(
            socket,
            new AgentEnvelope(
                1,
                "hello-agent",
                AgentMessageTypes.Hello,
                "session-agent",
                "corr-agent",
                10_000,
                new System.Text.Json.Nodes.JsonObject { ["profile"] = "agent" }),
            cancellation.Token);
        var hello = await ReceiveAsync(socket, cancellation.Token);

        var tools = hello.Payload["tools"]!.AsArray();
        var volume = Assert.Single(tools, node =>
            node!["tool_id"]!.GetValue<string>() == "system.volume");
        Assert.Equal("1.0.0", volume!["tool_version"]!.GetValue<string>());
        Assert.Equal("native_pending", volume["availability"]!.GetValue<string>());
        Assert.True(volume["model_enabled"]!.GetValue<bool>());
        Assert.Equal("system.write", Assert.Single(volume["scopes"]!.AsArray())!.GetValue<string>());

        var ids = tools.Select(node => node!["tool_id"]!.GetValue<string>()).ToArray();
        Assert.Contains("files.write", ids);
        Assert.DoesNotContain("terminal.run", ids);
        Assert.DoesNotContain("terminal.shell", ids);
        Assert.DoesNotContain("app.list", ids);
        Assert.DoesNotContain("app.open", ids);
        Assert.DoesNotContain("web.search", ids);
        Assert.DoesNotContain("calendar.create", ids);
        Assert.DoesNotContain("mail.send", ids);
    }

    [Fact]
    public async Task Hello_with_full_profile_adds_terminal_capabilities()
    {
        await using var fixture = await HostFixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(
            new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"),
            cancellation.Token);

        await SendAsync(
            socket,
            new AgentEnvelope(
                1,
                "hello-full",
                AgentMessageTypes.Hello,
                "session-full",
                "corr-full",
                10_000,
                new System.Text.Json.Nodes.JsonObject { ["profile"] = "full" }),
            cancellation.Token);
        var hello = await ReceiveAsync(socket, cancellation.Token);

        var ids = hello.Payload["tools"]!.AsArray()
            .Select(node => node!["tool_id"]!.GetValue<string>())
            .ToArray();
        Assert.Contains("terminal.run", ids);
        Assert.Contains("terminal.shell", ids);
    }

    [Fact]
    public async Task Hello_with_an_unknown_profile_is_rejected()
    {
        await using var fixture = await HostFixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(
            new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"),
            cancellation.Token);

        await SendAsync(
            socket,
            new AgentEnvelope(
                1,
                "hello-bad-profile",
                AgentMessageTypes.Hello,
                "session-bad-profile",
                "corr-bad-profile",
                10_000,
                new System.Text.Json.Nodes.JsonObject { ["profile"] = "hyperdrive" }),
            cancellation.Token);
        var reply = await ReceiveAsync(socket, cancellation.Token);

        Assert.Equal(AgentMessageTypes.ToolResult, reply.MessageType);
        Assert.Equal("hello-bad-profile", reply.RequestId);
        Assert.Equal(ToolResultStatuses.Failed, reply.Payload["status"]!.GetValue<string>());
        Assert.Equal("invalid_profile", reply.Payload["error_code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Session_open_rejects_skill_ids_without_opening_them()
    {
        await using var fixture = await HostFixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(
            new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"),
            cancellation.Token);
        await SendAsync(socket, new AgentEnvelope(1, "hello-skills", AgentMessageTypes.Hello, "session-skills", "corr-skills", 10_000, new System.Text.Json.Nodes.JsonObject()), cancellation.Token);
        _ = await ReceiveAsync(socket, cancellation.Token);

        await SendAsync(
            socket,
            new AgentEnvelope(
                1,
                "open-skills",
                AgentMessageTypes.SessionOpen,
                "session-skills",
                "corr-skills",
                10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["scopes"] = new System.Text.Json.Nodes.JsonArray("system.write"),
                    ["skill_ids"] = new System.Text.Json.Nodes.JsonArray("research-note"),
                }),
            cancellation.Token);
        var reply = await ReceiveAsync(socket, cancellation.Token);

        Assert.Equal(ToolResultStatuses.Denied, reply.Payload["status"]!.GetValue<string>());
        Assert.Equal("skill_not_granted", reply.Payload["error_code"]!.GetValue<string>());
        Assert.Empty(fixture.Host.Session.SkillIds);
        Assert.False(fixture.Host.Session.IsReady);
    }

    [Fact]
    public async Task Session_opened_returns_the_actual_validated_grant()
    {
        await using var fixture = await HostFixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(
            new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"),
            cancellation.Token);
        await SendAsync(socket, new AgentEnvelope(1, "hello-grant", AgentMessageTypes.Hello, "session-grant", "corr-grant", 10_000, new System.Text.Json.Nodes.JsonObject()), cancellation.Token);
        _ = await ReceiveAsync(socket, cancellation.Token);

        await SendAsync(
            socket,
            new AgentEnvelope(
                1,
                "open-grant",
                AgentMessageTypes.SessionOpen,
                "session-grant",
                "corr-grant",
                10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["scopes"] = new System.Text.Json.Nodes.JsonArray("system.write"),
                    ["skill_ids"] = new System.Text.Json.Nodes.JsonArray(),
                }),
            cancellation.Token);
        var reply = await ReceiveAsync(socket, cancellation.Token);

        Assert.Equal(AgentMessageTypes.SessionOpened, reply.MessageType);
        Assert.Equal(new[] { "system.write" }, reply.Payload["scopes"]!.AsArray()
            .Select(item => item!.GetValue<string>()));
        Assert.Empty(reply.Payload["skill_ids"]!.AsArray());

        await SendAsync(
            socket,
            new AgentEnvelope(
                1,
                "skill-disabled",
                AgentMessageTypes.SkillStart,
                "session-grant",
                "corr-grant",
                10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["skill_id"] = "research-note",
                    ["input"] = new System.Text.Json.Nodes.JsonObject(),
                }),
            cancellation.Token);
        var skillReply = await ReceiveAsync(socket, cancellation.Token);
        Assert.Equal(ToolResultStatuses.Denied, skillReply.Payload["status"]!.GetValue<string>());
        Assert.Equal("skill_not_granted", skillReply.Payload["error_code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Authenticated_session_can_use_a_registered_read_tool()
    {
        await using var fixture = await HostFixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(
            new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"),
            cancellation.Token);

        await SendAsync(socket, new AgentEnvelope(1, "hello-1", AgentMessageTypes.Hello, "session-1", "corr-1", 10_000, new System.Text.Json.Nodes.JsonObject()), cancellation.Token);
        _ = await ReceiveAsync(socket, cancellation.Token);
        await SendAsync(
            socket,
            new AgentEnvelope(
                1,
                "session-1",
                AgentMessageTypes.SessionOpen,
                "session-1",
                "corr-1",
                10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["scopes"] = new System.Text.Json.Nodes.JsonArray("workspace.read"),
                }), cancellation.Token);
        var opened = await ReceiveAsync(socket, cancellation.Token);
        Assert.Equal("succeeded", opened.Payload["status"]!.GetValue<string>());

        await SendAsync(
            socket,
            new AgentEnvelope(
                1,
                "tool-1",
                AgentMessageTypes.ToolRequest,
                "session-1",
                "corr-1",
                10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["tool_id"] = "files.list",
                    ["tool_version"] = "1.0.0",
                    ["arguments"] = new System.Text.Json.Nodes.JsonObject { ["path"] = "src" },
                }), cancellation.Token);
        var result = await ReceiveAsync(socket, cancellation.Token);

        Assert.Equal(AgentMessageTypes.ToolResult, result.MessageType);
        Assert.Equal("succeeded", result.Payload["status"]!.GetValue<string>());
        Assert.NotNull(result.Payload["output"]!["entries"]);
    }

    [Fact]
    public async Task Authenticated_session_can_execute_volume_with_a_fake_controller()
    {
        var controller = new FakeVolumeController(level: 40);
        await using var fixture = await HostFixture.StartAsync(controller);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(
            new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"),
            cancellation.Token);

        await SendAsync(socket, new AgentEnvelope(1, "hello-volume", AgentMessageTypes.Hello, "session-volume", "corr-volume", 10_000, new System.Text.Json.Nodes.JsonObject()), cancellation.Token);
        _ = await ReceiveAsync(socket, cancellation.Token);
        await SendAsync(
            socket,
            new AgentEnvelope(
                1,
                "session-volume",
                AgentMessageTypes.SessionOpen,
                "session-volume",
                "corr-volume",
                10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["scopes"] = new System.Text.Json.Nodes.JsonArray("system.write"),
                }),
            cancellation.Token);
        var opened = await ReceiveAsync(socket, cancellation.Token);
        Assert.Equal("succeeded", opened.Payload["status"]!.GetValue<string>());

        await SendAsync(
            socket,
            new AgentEnvelope(
                1,
                "volume-1",
                AgentMessageTypes.ToolRequest,
                "session-volume",
                "corr-volume",
                10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["tool_id"] = "system.volume",
                    ["tool_version"] = "1.0.0",
                    ["arguments"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["operation"] = "set",
                        ["level"] = 2,
                    },
                }),
            cancellation.Token);
        var result = await ReceiveAsync(socket, cancellation.Token);

        Assert.Equal("succeeded", result.Payload["status"]!.GetValue<string>());
        Assert.Equal(2, result.Payload["output"]!["level"]!.GetValue<int>());
        Assert.False(result.Payload["output"]!.AsObject().ContainsKey("verified"));
        Assert.Equal("verified", result.Payload["verification_state"]!.GetValue<string>());
        Assert.Equal("default_render_multimedia", result.Payload["endpoint_role"]!.GetValue<string>());
        Assert.Matches("^[0-9a-f]{16}$", result.Payload["endpoint_hash"]!.GetValue<string>());
        Assert.True(result.Payload["adapter_invoked"]!.GetValue<bool>());
        Assert.Equal(1, controller.OpenCount);
        Assert.Equal(1, controller.DisposeCount);
        Assert.Equal([("level", 2)], controller.Writes);
    }

    [Fact]
    public async Task Authenticated_terminal_run_requires_approval_then_executes()
    {
        await using var fixture = await HostFixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(
            new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"),
            cancellation.Token);

        await SendAsync(socket, new AgentEnvelope(1, "hello-terminal", AgentMessageTypes.Hello, "session-terminal", "corr-terminal", 10_000, new System.Text.Json.Nodes.JsonObject()), cancellation.Token);
        _ = await ReceiveAsync(socket, cancellation.Token);
        await SendAsync(
            socket,
            new AgentEnvelope(
                1,
                "session-terminal",
                AgentMessageTypes.SessionOpen,
                "session-terminal",
                "corr-terminal",
                10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["scopes"] = new System.Text.Json.Nodes.JsonArray("terminal.execute"),
                }),
            cancellation.Token);
        _ = await ReceiveAsync(socket, cancellation.Token);

        await SendAsync(
            socket,
            new AgentEnvelope(
                1,
                "terminal-1",
                AgentMessageTypes.ToolRequest,
                "session-terminal",
                "corr-terminal",
                10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["tool_id"] = "terminal.run",
                    ["tool_version"] = "1.0.0",
                    ["arguments"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["executable"] = "dotnet",
                        ["arguments"] = new System.Text.Json.Nodes.JsonArray("--version"),
                        ["working_directory"] = ".",
                    },
                }),
            cancellation.Token);
        var pending = await ReceiveAsync(socket, cancellation.Token);

        Assert.Equal(ToolResultStatuses.ApprovalRequired, pending.Payload["status"]!.GetValue<string>());
        Assert.False(pending.Payload["adapter_invoked"]!.GetValue<bool>());
        var approvalId = pending.Payload["output"]!["approval_id"]!.GetValue<string>();

        await SendAsync(
            socket,
            new AgentEnvelope(
                1,
                "terminal-approval-1",
                AgentMessageTypes.ApprovalResponse,
                "session-terminal",
                "corr-terminal",
                10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["approval_id"] = approvalId,
                    ["approved"] = true,
                }),
            cancellation.Token);
        var result = await ReceiveAsync(socket, cancellation.Token);

        Assert.Equal(ToolResultStatuses.Succeeded, result.Payload["status"]!.GetValue<string>());
        Assert.True(result.Payload["adapter_invoked"]!.GetValue<bool>());
        Assert.Contains("10.", result.Payload["output"]!["stdout_head"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nonzero_terminal_exit_is_an_outer_failed_receipt()
    {
        await using var fixture = await HostFixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(
            new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"),
            cancellation.Token);

        await SendAsync(socket, new AgentEnvelope(1, "hello-terminal-failed", AgentMessageTypes.Hello, "session-terminal-failed", "corr-terminal-failed", 10_000, new System.Text.Json.Nodes.JsonObject()), cancellation.Token);
        _ = await ReceiveAsync(socket, cancellation.Token);
        await SendAsync(
            socket,
            new AgentEnvelope(
                1,
                "session-terminal-failed",
                AgentMessageTypes.SessionOpen,
                "session-terminal-failed",
                "corr-terminal-failed",
                10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["scopes"] = new System.Text.Json.Nodes.JsonArray("terminal.execute"),
                }),
            cancellation.Token);
        _ = await ReceiveAsync(socket, cancellation.Token);

        await SendAsync(
            socket,
            new AgentEnvelope(
                1,
                "terminal-failed-1",
                AgentMessageTypes.ToolRequest,
                "session-terminal-failed",
                "corr-terminal-failed",
                10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["tool_id"] = "terminal.run",
                    ["tool_version"] = "1.0.0",
                    ["arguments"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["executable"] = "python",
                        ["arguments"] = new System.Text.Json.Nodes.JsonArray("-c", "raise SystemExit(7)"),
                        ["working_directory"] = ".",
                    },
                }),
            cancellation.Token);
        var pending = await ReceiveAsync(socket, cancellation.Token);

        Assert.Equal(ToolResultStatuses.ApprovalRequired, pending.Payload["status"]!.GetValue<string>());
        Assert.False(pending.Payload["adapter_invoked"]!.GetValue<bool>());
        var approvalId = pending.Payload["output"]!["approval_id"]!.GetValue<string>();

        await SendAsync(
            socket,
            new AgentEnvelope(
                1,
                "terminal-failed-approval-1",
                AgentMessageTypes.ApprovalResponse,
                "session-terminal-failed",
                "corr-terminal-failed",
                10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["approval_id"] = approvalId,
                    ["approved"] = true,
                }),
            cancellation.Token);
        var result = await ReceiveAsync(socket, cancellation.Token);

        Assert.Equal(ToolResultStatuses.Failed, result.Payload["status"]!.GetValue<string>());
        Assert.Equal("process_exit_nonzero", result.Payload["error_code"]!.GetValue<string>());
        Assert.False(result.Payload["output"]!.AsObject().ContainsKey("status"));
    }

    [Fact]
    public async Task Approval_response_runs_the_original_shell_request_once()
    {
        await using var fixture = await HostFixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(
            new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"),
            cancellation.Token);

        await SendAsync(socket, new AgentEnvelope(1, "hello-shell", AgentMessageTypes.Hello, "session-shell", "corr-shell", 10_000, new System.Text.Json.Nodes.JsonObject()), cancellation.Token);
        _ = await ReceiveAsync(socket, cancellation.Token);
        await SendAsync(
            socket,
            new AgentEnvelope(
                1,
                "session-shell",
                AgentMessageTypes.SessionOpen,
                "session-shell",
                "corr-shell",
                10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["scopes"] = new System.Text.Json.Nodes.JsonArray("terminal.execute"),
                }),
            cancellation.Token);
        _ = await ReceiveAsync(socket, cancellation.Token);

        await SendAsync(
            socket,
            new AgentEnvelope(
                1,
                "shell-request-1",
                AgentMessageTypes.ToolRequest,
                "session-shell",
                "corr-shell",
                10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["tool_id"] = "terminal.shell",
                    ["tool_version"] = "1.0.0",
                    ["arguments"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["working_directory"] = ".",
                        ["script"] = "Get-Date",
                    },
                }),
            cancellation.Token);
        var pending = await ReceiveAsync(socket, cancellation.Token);

        Assert.Equal("approval_required", pending.Payload["status"]!.GetValue<string>());
        var approvalId = pending.Payload["output"]!["approval_id"]!.GetValue<string>();
        Assert.Equal("Get-Date", pending.Payload["output"]![ApprovalPreviewFields.Field]!.GetValue<string>());

        await SendAsync(
            socket,
            new AgentEnvelope(
                1,
                "approval-response-1",
                AgentMessageTypes.ApprovalResponse,
                "session-shell",
                "corr-shell",
                10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["approval_id"] = approvalId,
                    ["approved"] = true,
                }),
            cancellation.Token);
        var approved = await ReceiveAsync(socket, cancellation.Token);

        Assert.Equal("succeeded", approved.Payload["status"]!.GetValue<string>());
        Assert.NotNull(approved.Payload["output"]!["stdout_head"]!.GetValue<string>());

        await SendAsync(
            socket,
            new AgentEnvelope(
                1,
                "approval-response-2",
                AgentMessageTypes.ApprovalResponse,
                "session-shell",
                "corr-shell",
                10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["approval_id"] = approvalId,
                    ["approved"] = true,
                }),
            cancellation.Token);
        var replay = await ReceiveAsync(socket, cancellation.Token);
        Assert.Equal("approval_stale", replay.Payload["error_code"]!.GetValue<string>());
    }

    [Fact]
    public async Task V1_rejects_v2_only_message()
    {
        await using var fixture = await HostFixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"), cts.Token);
        var v2Envelope = new AgentEnvelope(
            1,
            "v2-reject-1",
            AgentMessageTypes.RunOpen,
            "session-v2-reject",
            null,
            10_000,
            EnvelopeTestSnapshots.RunOpenPayload(
                "run_11111111111111111111111111111111",
                "session-v2-reject",
                Path.GetTempPath(),
                EnvelopeTestSnapshots.StandardEmpty()));
        await SendAsync(socket, v2Envelope, cts.Token);
        var reply = await ReceiveAsync(socket, cts.Token);
        Assert.Equal("unsupported_message", reply.Payload["error_code"]!.GetValue<string>());
    }

    [Fact]
    public async Task V1_rejects_new_v2_only_policy_and_approval_messages()
    {
        await using var fixture = await HostFixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"), cts.Token);

        foreach (var messageType in new[]
                 {
                     AgentMessageTypes.RunPolicyRebind,
                     AgentMessageTypes.ToolDecision,
                     AgentMessageTypes.ToolPrepared,
                     AgentMessageTypes.ApprovalRespond,
                     AgentMessageTypes.ApprovalResult,
                 })
        {
            await SendAsync(
                socket,
                new AgentEnvelope(1, "v1-reject-" + messageType, messageType, "session-v1-new", null, 10_000, new System.Text.Json.Nodes.JsonObject()),
                cts.Token);
            var reply = await ReceiveAsync(socket, cts.Token);
            Assert.Equal("unsupported_message", reply.Payload["error_code"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task V2_run_open_advertises_only_read_capabilities()
    {
        await using var fixture = await HostFixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"), cts.Token);
        // Hello v2
        var hello = new AgentEnvelope(2, "hello-v2", AgentMessageTypes.Hello, "session-v2", null, 10_000, new System.Text.Json.Nodes.JsonObject { ["profile"] = "chat" });
        await SendAsync(socket, hello, cts.Token);
        var helloReply = await ReceiveAsync(socket, cts.Token);
        Assert.Equal(AgentMessageTypes.HelloOk, helloReply.MessageType);
        // Run open
        var runOpen = new AgentEnvelope(
            2,
            "run-open-1",
            AgentMessageTypes.RunOpen,
            "session-v2",
            null,
            10_000,
            EnvelopeTestSnapshots.RunOpenPayload(
                "run_22222222222222222222222222222222",
                "session-v2",
                Path.GetTempPath(),
                EnvelopeTestSnapshots.StandardEmpty()));
        await SendAsync(socket, runOpen, cts.Token);
        var opened = await ReceiveAsync(socket, cts.Token);
        Assert.Equal(AgentMessageTypes.RunOpened, opened.MessageType);
        Assert.Equal("succeeded", opened.Payload["status"]!.GetValue<string>());
        var caps = opened.Payload["advertised_capabilities"]!.AsArray().Select(n => n!.GetValue<string>()).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "app.list", "files.delete", "files.edit", "files.glob", "files.list", "files.read", "files.restore", "files.search", "files.write", "help.docs", "notes.create", "system.status", "system.time", "system.volume", "terminal.run", "terminal.shell" }, caps);
    }

    [Fact]
    public async Task V2_run_open_advertises_web_capabilities_when_web_is_enabled()
    {
        await using var fixture = await HostFixture.StartAsync(
            web: new Configuration.WebOptions(
                enabled: true,
                searchEndpoint: new Uri("https://html.duckduckgo.com/html/")));
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"), cts.Token);
        await SendAsync(
            socket,
            new AgentEnvelope(2, "hello-web", AgentMessageTypes.Hello, "session-web", null, 10_000, new System.Text.Json.Nodes.JsonObject { ["profile"] = "chat" }),
            cts.Token);
        await ReceiveAsync(socket, cts.Token);
        await SendAsync(
            socket,
            new AgentEnvelope(
                2,
                "run-open-web",
                AgentMessageTypes.RunOpen,
                "session-web",
                null,
                10_000,
                EnvelopeTestSnapshots.RunOpenPayload(
                    "run_web11111111111111111111111111111",
                    "session-web",
                    Path.GetTempPath(),
                    EnvelopeTestSnapshots.StandardEmpty())),
            cts.Token);
        var opened = await ReceiveAsync(socket, cts.Token);
        Assert.Equal(AgentMessageTypes.RunOpened, opened.MessageType);
        var caps = opened.Payload["advertised_capabilities"]!.AsArray().Select(n => n!.GetValue<string>()).OrderBy(x => x).ToArray();
        Assert.Equal(
            new[]
            {
                "app.list", "files.delete", "files.edit", "files.glob", "files.list",
                "files.read", "files.restore", "files.search", "files.write",
                "help.docs", "notes.create", "system.status", "system.time",
                "system.volume", "terminal.run", "terminal.shell", "web.fetch", "web.search",
            },
            caps);
    }

    [Fact]
    public async Task V2_run_open_with_tampered_snapshot_hash_fails_closed()
    {
        await using var fixture = await HostFixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"), cts.Token);

        var payload = EnvelopeTestSnapshots.RunOpenPayload(
            "run_tamper111111111111111111111111",
            "session-tamper",
            Path.GetTempPath(),
            EnvelopeTestSnapshots.StandardWithRules(
                new BrokerPolicyRuleWire(
                    "rule_allow_reads",
                    "project",
                    "allow",
                    new BrokerPolicyMatcherWire("files.read", null, null, null, null, null, null, null),
                    1,
                    null)));
        payload["snapshot_hash"] = new string('e', 64);
        await SendAsync(socket, new AgentEnvelope(2, "run-tamper", AgentMessageTypes.RunOpen, "session-tamper", null, 10_000, payload), cts.Token);
        var reply = await ReceiveAsync(socket, cts.Token);
        Assert.Equal(ToolResultStatuses.Denied, reply.Payload["status"]!.GetValue<string>());
        Assert.Equal("snapshot_hash_mismatch", reply.Payload["error_code"]!.GetValue<string>());

        await SendAsync(
            socket,
            new AgentEnvelope(2, "hello-after-tamper", AgentMessageTypes.Hello, "session-tamper", null, 10_000, new System.Text.Json.Nodes.JsonObject { ["profile"] = "full" }),
            cts.Token);
        var helloReply = await ReceiveAsync(socket, cts.Token);
        var ids = helloReply.Payload["tools"]!.AsArray()
            .Select(node => node!["tool_id"]!.GetValue<string>())
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[]
            {
                "files.delete", "files.edit", "files.glob", "files.list",
                "files.read", "files.restore", "files.search", "files.write",
                "help.docs", "notes.create", "system.volume", "terminal.run",
                "terminal.shell",
            },
            ids);
    }

    [Fact]
    public async Task V2_evaluate_prepare_execute_flow_for_workspace_reads()
    {
        await using var fixture = await HostFixture.StartAsync();
        var probePath = Path.Combine(fixture.Root, "probe.txt");
        File.WriteAllText(probePath, "workspace-probe");
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"), cts.Token);

        await SendAsync(
            socket,
            new AgentEnvelope(2, "run-open-flow", AgentMessageTypes.RunOpen, "session-flow", null, 10_000,
                EnvelopeTestSnapshots.RunOpenPayload("run_flow111111111111111111111111111", "session-flow", fixture.Root, EnvelopeTestSnapshots.StandardEmpty())),
            cts.Token);
        var opened = await ReceiveAsync(socket, cts.Token);
        Assert.Equal(AgentMessageTypes.RunOpened, opened.MessageType);

        await SendAsync(
            socket,
            new AgentEnvelope(2, "evaluate-1", AgentMessageTypes.ToolEvaluate, "session-flow", null, 10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["run_id"] = "run_flow111111111111111111111111111",
                    ["tool_id"] = "files.read",
                    ["tool_version"] = "1.0.0",
                    ["arguments"] = new System.Text.Json.Nodes.JsonObject { ["path"] = "probe.txt" },
                }),
            cts.Token);
        var decision = await ReceiveAsync(socket, cts.Token);
        Assert.Equal(AgentMessageTypes.ToolDecision, decision.MessageType);
        Assert.Equal("allow", decision.Payload["decision"]!.GetValue<string>());
        Assert.Equal("standard_workspace_allow", decision.Payload["reason_code"]!.GetValue<string>());
        var decisionToken = decision.Payload["decision_token"]!.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(decisionToken));

        await SendAsync(
            socket,
            new AgentEnvelope(2, "prepare-1", AgentMessageTypes.ToolPrepare, "session-flow", null, 10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["run_id"] = "run_flow111111111111111111111111111",
                    ["tool_id"] = "files.read",
                    ["decision_token"] = decisionToken,
                }),
            cts.Token);
        var prepared = await ReceiveAsync(socket, cts.Token);
        Assert.Equal(AgentMessageTypes.ToolPrepared, prepared.MessageType);
        Assert.Equal("succeeded", prepared.Payload["status"]!.GetValue<string>());
        var preparationToken = prepared.Payload["preparation_token"]!.GetValue<string>();

        await SendAsync(
            socket,
            new AgentEnvelope(2, "execute-1", AgentMessageTypes.ToolExecute, "session-flow", null, 10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["run_id"] = "run_flow111111111111111111111111111",
                    ["tool_id"] = "files.read",
                    ["tool_version"] = "1.0.0",
                    ["preparation_token"] = preparationToken,
                    ["arguments"] = new System.Text.Json.Nodes.JsonObject { ["path"] = "probe.txt" },
                }),
            cts.Token);
        var result = await ReceiveAsync(socket, cts.Token);
        Assert.Equal(AgentMessageTypes.ToolExecuteResult, result.MessageType);
        Assert.Equal(ToolResultStatuses.Succeeded, result.Payload["status"]!.GetValue<string>());
        Assert.True(result.Payload["adapter_invoked"]!.GetValue<bool>());
    }

    [Fact]
    public async Task V2_evaluate_keeps_unadvertised_tools_unavailable()
    {
        await using var fixture = await HostFixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"), cts.Token);

        await SendAsync(
            socket,
            new AgentEnvelope(2, "run-open-unadv", AgentMessageTypes.RunOpen, "session-unadv", null, 10_000,
                EnvelopeTestSnapshots.RunOpenPayload("run_unad111111111111111111111111111", "session-unadv", Path.GetTempPath(), EnvelopeTestSnapshots.StandardEmpty())),
            cts.Token);
        _ = await ReceiveAsync(socket, cts.Token);

        foreach (var toolId in new[] { "web.search", "totally.unknown" })
        {
            await SendAsync(
                socket,
                new AgentEnvelope(2, "evaluate-" + toolId, AgentMessageTypes.ToolEvaluate, "session-unadv", null, 10_000,
                    new System.Text.Json.Nodes.JsonObject
                    {
                        ["run_id"] = "run_unad111111111111111111111111111",
                        ["tool_id"] = toolId,
                        ["arguments"] = new System.Text.Json.Nodes.JsonObject { ["path"] = "." },
                    }),
                cts.Token);
            var reply = await ReceiveAsync(socket, cts.Token);
            Assert.Equal(ToolResultStatuses.Denied, reply.Payload["status"]!.GetValue<string>());
            Assert.Equal("phase_not_available", reply.Payload["error_code"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task V2_policy_rebind_invalidates_pending_decisions_over_the_wire()
    {
        await using var fixture = await HostFixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"), cts.Token);

        const string sessionId = "session-rebind-wire";
        const string runId = "run_rwire111111111111111111111111111";
        await SendAsync(
            socket,
            new AgentEnvelope(2, "rebind-open", AgentMessageTypes.RunOpen, sessionId, null, 10_000,
                EnvelopeTestSnapshots.RunOpenPayload(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardEmpty(version: 1))),
            cts.Token);
        _ = await ReceiveAsync(socket, cts.Token);

        await SendAsync(
            socket,
            new AgentEnvelope(2, "rebind-eval", AgentMessageTypes.ToolEvaluate, sessionId, null, 10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["run_id"] = runId,
                    ["tool_id"] = "files.list",
                    ["arguments"] = new System.Text.Json.Nodes.JsonObject { ["path"] = "." },
                }),
            cts.Token);
        var decision = await ReceiveAsync(socket, cts.Token);
        var staleToken = decision.Payload["decision_token"]!.GetValue<string>();

        var rebind = new AgentEnvelope(
            2,
            "rebind-1",
            AgentMessageTypes.RunPolicyRebind,
            sessionId,
            null,
            10_000,
            EnvelopeTestSnapshots.RunOpenPayload(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardEmpty(version: 2)));
        await SendAsync(socket, rebind, cts.Token);
        var rebound = await ReceiveAsync(socket, cts.Token);
        Assert.Equal(AgentMessageTypes.RunRebound, rebound.MessageType);
        Assert.Equal("succeeded", rebound.Payload["status"]!.GetValue<string>());
        Assert.Equal(2, rebound.Payload["binding_version"]!.GetValue<long>());

        await SendAsync(
            socket,
            new AgentEnvelope(2, "rebind-prepare-stale", AgentMessageTypes.ToolPrepare, sessionId, null, 10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["run_id"] = runId,
                    ["tool_id"] = "files.list",
                    ["decision_token"] = staleToken,
                }),
            cts.Token);
        var stale = await ReceiveAsync(socket, cts.Token);
        Assert.Equal(ToolResultStatuses.Denied, stale.Payload["status"]!.GetValue<string>());
        Assert.Equal("decision_stale", stale.Payload["error_code"]!.GetValue<string>());
    }

    [Fact]
    public async Task V2_run_close_stops_further_evaluation()
    {
        await using var fixture = await HostFixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"), cts.Token);

        const string sessionId = "session-close-wire";
        const string runId = "run_clos1111111111111111111111111111";
        await SendAsync(
            socket,
            new AgentEnvelope(2, "close-open", AgentMessageTypes.RunOpen, sessionId, null, 10_000,
                EnvelopeTestSnapshots.RunOpenPayload(runId, sessionId, Path.GetTempPath(), EnvelopeTestSnapshots.StandardEmpty())),
            cts.Token);
        _ = await ReceiveAsync(socket, cts.Token);

        await SendAsync(
            socket,
            new AgentEnvelope(2, "close-1", AgentMessageTypes.RunClose, sessionId, null, 10_000,
                new System.Text.Json.Nodes.JsonObject { ["run_id"] = runId }),
            cts.Token);
        var closed = await ReceiveAsync(socket, cts.Token);
        Assert.Equal(AgentMessageTypes.RunClosed, closed.MessageType);
        Assert.Equal("succeeded", closed.Payload["status"]!.GetValue<string>());

        await SendAsync(
            socket,
            new AgentEnvelope(2, "after-close-eval", AgentMessageTypes.ToolEvaluate, sessionId, null, 10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["run_id"] = runId,
                    ["tool_id"] = "files.list",
                    ["arguments"] = new System.Text.Json.Nodes.JsonObject { ["path"] = "." },
                }),
            cts.Token);
        var rejected = await ReceiveAsync(socket, cts.Token);
        Assert.Equal(ToolResultStatuses.Denied, rejected.Payload["status"]!.GetValue<string>());
        Assert.Equal("run_not_open", rejected.Payload["error_code"]!.GetValue<string>());
    }

    [Fact]
    public async Task V2_approval_respond_and_output_delta_are_reserved()
    {
        await using var fixture = await HostFixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"), cts.Token);

        await SendAsync(
            socket,
            new AgentEnvelope(1, "hello-reserved", AgentMessageTypes.Hello, "session-reserved", null, 10_000, new System.Text.Json.Nodes.JsonObject()),
            cts.Token);
        _ = await ReceiveAsync(socket, cts.Token);
        await SendAsync(
            socket,
            new AgentEnvelope(1, "open-reserved", AgentMessageTypes.SessionOpen, "session-reserved", null, 10_000, new System.Text.Json.Nodes.JsonObject { ["scopes"] = new System.Text.Json.Nodes.JsonArray("workspace.read") }),
            cts.Token);
        _ = await ReceiveAsync(socket, cts.Token);

        await SendAsync(
            socket,
            new AgentEnvelope(2, "approval-respond-1", AgentMessageTypes.ApprovalRespond, "session-reserved", null, 10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["approval_id"] = "approval-1",
                    ["approved"] = true,
                }),
            cts.Token);
        var approval = await ReceiveAsync(socket, cts.Token);
        Assert.Equal(AgentMessageTypes.ApprovalResult, approval.MessageType);
        Assert.Equal("phase_not_available", approval.Payload["error_code"]!.GetValue<string>());

        await SendAsync(
            socket,
            new AgentEnvelope(2, "delta-1", AgentMessageTypes.ToolOutputDelta, "session-reserved", null, 10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["run_id"] = "run_77777777777777777777777777777777",
                    ["tool_call_id"] = "call_77777777777777777777777777777777",
                    ["execution_id"] = "exec-1",
                    ["frame_sequence"] = 1,
                    ["stream"] = "stdout",
                    ["text"] = "partial",
                }),
            cts.Token);
        var delta = await ReceiveAsync(socket, cts.Token);
        Assert.Equal(AgentMessageTypes.ToolExecuteResult, delta.MessageType);
        Assert.Equal("phase_not_available", delta.Payload["error_code"]!.GetValue<string>());
    }

    private sealed class HostFixture : IAsyncDisposable
    {
        private HostFixture(string root, string token, BrokerHost host)
        {
            Root = root;
            Token = token;
            Host = host;
        }

        public string Root { get; }
        public string Token { get; }
        public BrokerHost Host { get; }

        public static async Task<HostFixture> StartAsync(
            IVolumeController? volumeController = null,
            Configuration.WebOptions? web = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "ru-agent-host-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var sessionRoot = Path.Combine(root, "session");
            Directory.CreateDirectory(sessionRoot);
            var token = "broker-token-" + Guid.NewGuid().ToString("N");
            var descriptorPath = Path.Combine(root, "broker.json");
            var descriptor = new BrokerSessionDescriptor(
                "http://127.0.0.1:" + GetFreePort(),
                token,
                1,
                sessionRoot,
                DateTimeOffset.UtcNow.AddMinutes(5));
            File.WriteAllText(
                descriptorPath,
                JsonSerializer.Serialize(descriptor, BrokerSessionDescriptor.JsonOptions));

            // Phase-1 file adapters still resolve through the legacy workspace root; pin it to
            // this fixture so delegated execution matches the leased workspace. The v2 policy
            // evaluator itself only ever uses the lease root.
            Directory.CreateDirectory(Path.Combine(root, "src"));
            PreviousWorkspaceRoot = Environment.GetEnvironmentVariable("RU_WORKSPACE_ROOT");
            Environment.SetEnvironmentVariable("RU_WORKSPACE_ROOT", root);

            var host = web is not null
                ? await BrokerHost.StartAsync(new Configuration.BrokerOptions(descriptorPath, Web: web))
                : volumeController is null
                    ? await BrokerHost.StartAsync(descriptorPath)
                    : await BrokerHost.StartAsync(descriptorPath, volumeController);
            return new HostFixture(root, token, host);
        }

        private static string? PreviousWorkspaceRoot { get; set; }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Environment.SetEnvironmentVariable("RU_WORKSPACE_ROOT", PreviousWorkspaceRoot);
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static int GetFreePort()
        {
            using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    private sealed class FakeVolumeController(int level, bool muted = false) : IVolumeController
    {
        public VolumeState State { get; private set; } = new(level, muted);
        public List<(string Kind, int Value)> Writes { get; } = [];
        public int OpenCount { get; private set; }
        public int DisposeCount { get; private set; }

        public IVolumeEndpoint OpenDefaultRenderEndpoint()
        {
            OpenCount++;
            return new FakeEndpoint(this);
        }

        private sealed class FakeEndpoint(FakeVolumeController owner) : IVolumeEndpoint
        {
            private bool _disposed;

            public string EndpointRole => "default_render_multimedia";
            public string EndpointHash => "0123456789abcdef";

            public VolumeState GetState() => owner.State;

            public void SetLevel(int value)
            {
                owner.Writes.Add(("level", value));
                owner.State = owner.State with { Level = value };
            }

            public void SetMuted(bool value) => owner.State = owner.State with { Muted = value };

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                owner.DisposeCount++;
            }
        }
    }

    private static async Task SendAsync(ClientWebSocket socket, AgentEnvelope envelope, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(AgentEnvelopeCodec.Serialize(envelope));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<AgentEnvelope> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var result = await socket.ReceiveAsync(buffer, cancellationToken);
        return AgentEnvelopeCodec.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
    }
}


