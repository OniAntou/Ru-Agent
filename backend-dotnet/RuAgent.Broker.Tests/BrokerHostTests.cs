using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        Assert.Equal(new[] { "glob", "grep", "read" }, ids);
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
        var ids = tools.Select(node => node!["tool_id"]!.GetValue<string>()).ToArray();
        Assert.Contains("write", ids);
        Assert.Contains("edit", ids);
        Assert.Contains("bash", ids);
        Assert.Contains("read", ids);
        Assert.DoesNotContain("system.volume", ids);
        Assert.DoesNotContain("app.list", ids);
        Assert.DoesNotContain("app.open", ids);
        Assert.DoesNotContain("web.search", ids);
        Assert.DoesNotContain("calendar.create", ids);
        Assert.DoesNotContain("mail.send", ids);
    }

    [Fact]
    public async Task Hello_with_full_profile_maps_to_single_agent_policy()
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
        Assert.Contains("bash", ids);
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
                    ["scopes"] = new System.Text.Json.Nodes.JsonArray("workspace.write"),
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
                    ["scopes"] = new System.Text.Json.Nodes.JsonArray("workspace.write"),
                    ["skill_ids"] = new System.Text.Json.Nodes.JsonArray(),
                }),
            cancellation.Token);
        var reply = await ReceiveAsync(socket, cancellation.Token);

        Assert.Equal(AgentMessageTypes.SessionOpened, reply.MessageType);
        Assert.Equal(new[] { "workspace.write" }, reply.Payload["scopes"]!.AsArray()
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
                    ["scopes"] = new System.Text.Json.Nodes.JsonArray("workspace.read", "system.read"),
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
                    ["tool_id"] = "read",
                    ["tool_version"] = "1.0.0",
                    ["arguments"] = new System.Text.Json.Nodes.JsonObject { ["op"] = "list", ["path"] = "src" },
                }), cancellation.Token);
        var result = await ReceiveAsync(socket, cancellation.Token);

        Assert.Equal(AgentMessageTypes.ToolResult, result.MessageType);
        Assert.Equal("succeeded", result.Payload["status"]!.GetValue<string>());
        Assert.NotNull(result.Payload["output"]!["entries"]);
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
                    ["tool_id"] = "bash",
                    ["tool_version"] = "1.0.0",
                    ["arguments"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["mode"] = "run",
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
                    ["tool_id"] = "bash",
                    ["tool_version"] = "1.0.0",
                    ["arguments"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["mode"] = "run",
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
                    ["tool_id"] = "bash",
                    ["tool_version"] = "1.0.0",
                    ["arguments"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["mode"] = "shell",
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
    public async Task V2_run_open_advertises_all_eight_capabilities()
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
        Assert.Equal(new[] { "bash", "edit", "glob", "grep", "read", "web_fetch", "web_search", "write" }, caps);
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
        Assert.Equal(new[] { "bash", "edit", "glob", "grep", "read", "web_fetch", "web_search", "write" }, caps);
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
                    new BrokerPolicyMatcherWire("read", null, null, null, null, null, null, null),
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
        Assert.Equal(new[] { "bash", "edit", "glob", "grep", "read", "write" }, ids);
    }

    [Fact]
    public async Task V2_evaluate_execute_flow_for_workspace_reads()
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
                    ["tool_id"] = "read",
                    ["tool_version"] = "1.0.0",
                    ["arguments"] = new System.Text.Json.Nodes.JsonObject { ["op"] = "read", ["path"] = "probe.txt" },
                }),
            cts.Token);
        var decision = await ReceiveAsync(socket, cts.Token);
        Assert.Equal(AgentMessageTypes.ToolDecision, decision.MessageType);
        Assert.Equal("allow", decision.Payload["decision"]!.GetValue<string>());
        Assert.Equal("standard_workspace_allow", decision.Payload["reason_code"]!.GetValue<string>());
        var authorizationToken = decision.Payload["authorization_token"]!.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(authorizationToken));

        await SendAsync(
            socket,
            new AgentEnvelope(2, "execute-1", AgentMessageTypes.ToolExecute, "session-flow", null, 10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["run_id"] = "run_flow111111111111111111111111111",
                    ["tool_id"] = "read",
                    ["tool_version"] = "1.0.0",
                    ["authorization_token"] = authorizationToken,
                    ["arguments"] = new System.Text.Json.Nodes.JsonObject { ["op"] = "read", ["path"] = "probe.txt" },
                }),
            cts.Token);
        var result = await ReceiveAsync(socket, cts.Token);
        Assert.Equal(AgentMessageTypes.ToolExecuteResult, result.MessageType);
        Assert.Equal(ToolResultStatuses.Succeeded, result.Payload["status"]!.GetValue<string>());
        Assert.True(result.Payload["adapter_invoked"]!.GetValue<bool>());
    }

    [Fact]
    public async Task V2_external_directory_approval_is_reused_by_another_tool_in_the_same_session()
    {
        await using var fixture = await HostFixture.StartAsync();
        var external = Path.Combine(Path.GetTempPath(), "ru-broker-external-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "probe.txt"), "external");
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await socket.ConnectAsync(new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"), cts.Token);
            const string sessionId = "session-external";
            const string runId = "run_external111111111111111111111111";
            await SendAsync(
                socket,
                new AgentEnvelope(2, "open-external", AgentMessageTypes.RunOpen, sessionId, null, 10_000,
                    EnvelopeTestSnapshots.RunOpenPayload(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardEmpty())),
                cts.Token);
            Assert.Equal(AgentMessageTypes.RunOpened, (await ReceiveAsync(socket, cts.Token)).MessageType);

            await SendAsync(
                socket,
                Evaluate("evaluate-external-read", sessionId, runId, "files.read",
                    new JsonObject { ["path"] = Path.Combine(external, "probe.txt") }),
                cts.Token);
            var ask = await ReceiveAsync(socket, cts.Token);
            Assert.Equal("ask", ask.Payload["decision"]!.GetValue<string>());
            Assert.Equal("external_directory_required", ask.Payload["reason_code"]!.GetValue<string>());
            var approvalId = ask.Payload["approval_id"]!.GetValue<string>();

            await SendAsync(
                socket,
                new AgentEnvelope(2, "approve-external", AgentMessageTypes.ApprovalRespond, sessionId, null, 10_000,
                    new JsonObject
                    {
                        ["run_id"] = runId,
                        ["approval_id"] = approvalId,
                        ["command"] = "allow_session_directory",
                    }),
                cts.Token);
            var approved = await ReceiveAsync(socket, cts.Token);
            Assert.Equal("authorized", approved.Payload["status"]!.GetValue<string>());

            await SendAsync(
                socket,
                Evaluate("evaluate-external-write", sessionId, runId, "files.write",
                    new JsonObject
                    {
                        ["path"] = Path.Combine(external, "next.txt"),
                        ["content"] = "next",
                    }),
                cts.Token);
            var allowed = await ReceiveAsync(socket, cts.Token);
            Assert.Equal("allow", allowed.Payload["decision"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public async Task V2_external_directory_grant_executes_terminal_in_next_run_only_for_same_session()
    {
        await using var fixture = await HostFixture.StartAsync();
        var external = Path.Combine(Path.GetTempPath(), "ru-broker-terminal-external-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "probe.txt"), "external");
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await socket.ConnectAsync(new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"), cts.Token);
            const string sessionId = "session-external-terminal";
            const string runA = "run_external_a111111111111111111111111";
            const string runB = "run_external_b111111111111111111111111";
            const string runC = "run_external_c111111111111111111111111";

            await SendAsync(
                socket,
                new AgentEnvelope(2, "open-external-a", AgentMessageTypes.RunOpen, sessionId, null, 10_000,
                    EnvelopeTestSnapshots.RunOpenPayload(runA, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardEmpty())),
                cts.Token);
            Assert.Equal(AgentMessageTypes.RunOpened, (await ReceiveAsync(socket, cts.Token)).MessageType);

            await SendAsync(
                socket,
                Evaluate("evaluate-external-terminal-read", sessionId, runA, "files.read",
                    new JsonObject { ["path"] = Path.Combine(external, "probe.txt") }),
                cts.Token);
            var directoryAsk = await ReceiveAsync(socket, cts.Token);
            Assert.Equal("ask", directoryAsk.Payload["decision"]!.GetValue<string>());
            Assert.Equal("external_directory_required", directoryAsk.Payload["reason_code"]!.GetValue<string>());

            await SendAsync(
                socket,
                new AgentEnvelope(2, "approve-external-terminal-directory", AgentMessageTypes.ApprovalRespond, sessionId, null, 10_000,
                    new JsonObject
                    {
                        ["run_id"] = runA,
                        ["approval_id"] = directoryAsk.Payload["approval_id"]!.GetValue<string>(),
                        ["command"] = "allow_session_directory",
                    }),
                cts.Token);
            var authorized = await ReceiveAsync(socket, cts.Token);
            Assert.Equal("authorized", authorized.Payload["status"]!.GetValue<string>());
            Assert.False(string.IsNullOrWhiteSpace(authorized.Payload["authorization_token"]!.GetValue<string>()));

            await SendAsync(
                socket,
                new AgentEnvelope(2, "open-external-b", AgentMessageTypes.RunOpen, sessionId, null, 10_000,
                    EnvelopeTestSnapshots.RunOpenPayload(runB, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardEmpty())),
                cts.Token);
            Assert.Equal(AgentMessageTypes.RunOpened, (await ReceiveAsync(socket, cts.Token)).MessageType);

            var terminalArguments = new JsonObject
            {
                ["working_directory"] = external,
                ["executable"] = "dotnet",
                ["arguments"] = new JsonArray("--version"),
                ["read_only"] = true,
            };
            await SendAsync(socket, Evaluate("evaluate-external-terminal-run", sessionId, runB, "terminal.run", terminalArguments.DeepClone().AsObject()), cts.Token);
            var decision = await ReceiveAsync(socket, cts.Token);
            Assert.Equal("allow", decision.Payload["decision"]!.GetValue<string>());
            var authorizationToken = decision.Payload["authorization_token"]!.GetValue<string>();

            await SendAsync(
                socket,
                Execute("execute-external-terminal-run", sessionId, runB, "terminal.run", authorizationToken, terminalArguments.DeepClone().AsObject()),
                cts.Token);
            var execution = await ReceiveAsync(socket, cts.Token);
            Assert.Equal(AgentMessageTypes.ToolExecuteResult, execution.MessageType);
            Assert.Equal(ToolResultStatuses.Succeeded, execution.Payload["status"]!.GetValue<string>());
            Assert.True(execution.Payload["adapter_invoked"]!.GetValue<bool>());
            Assert.Equal(0, execution.Payload["output"]!["exit_code"]!.GetValue<int>());
            Assert.DoesNotContain("workspace_path_outside_root", execution.Payload.ToJsonString(), StringComparison.Ordinal);

            await SendAsync(
                socket,
                new AgentEnvelope(2, "open-external-c", AgentMessageTypes.RunOpen, "session-external-terminal-new", null, 10_000,
                    EnvelopeTestSnapshots.RunOpenPayload(runC, "session-external-terminal-new", fixture.Root, EnvelopeTestSnapshots.StandardEmpty())),
                cts.Token);
            Assert.Equal(AgentMessageTypes.RunOpened, (await ReceiveAsync(socket, cts.Token)).MessageType);

            await SendAsync(
                socket,
                Evaluate("evaluate-external-terminal-new-session", "session-external-terminal-new", runC, "terminal.run", terminalArguments.DeepClone().AsObject()),
                cts.Token);
            var newSessionDecision = await ReceiveAsync(socket, cts.Token);
            Assert.Equal("ask", newSessionDecision.Payload["decision"]!.GetValue<string>());
            Assert.Equal("external_directory_required", newSessionDecision.Payload["reason_code"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public async Task V2_terminal_run_authorized_risky_command_executes_successfully()
    {
        await using var fixture = await HostFixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await socket.ConnectAsync(new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"), cts.Token);
        const string sessionId = "session-terminal-risky";
        const string runId = "run_terminal_risky11111111111111";
        await SendAsync(
            socket,
            new AgentEnvelope(2, "open-terminal-risky", AgentMessageTypes.RunOpen, sessionId, null, 10_000,
                EnvelopeTestSnapshots.RunOpenPayload(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardEmpty())),
            cts.Token);
        Assert.Equal(AgentMessageTypes.RunOpened, (await ReceiveAsync(socket, cts.Token)).MessageType);

        var riskyArgs = new JsonObject
        {
            ["executable"] = "where.exe",
            ["arguments"] = new JsonArray("dotnet"),
            ["working_directory"] = ".",
        };

        await SendAsync(socket, Evaluate("evaluate-terminal-risky", sessionId, runId, "terminal.run", riskyArgs.DeepClone().AsObject()), cts.Token);
        var ask = await ReceiveAsync(socket, cts.Token);
        Assert.Equal("ask", ask.Payload["decision"]!.GetValue<string>());
        Assert.Equal("terminal_unknown_executable", ask.Payload["reason_code"]!.GetValue<string>());
        var approvalId = ask.Payload["approval_id"]!.GetValue<string>();

        await SendAsync(
            socket,
            new AgentEnvelope(2, "approve-terminal-risky", AgentMessageTypes.ApprovalRespond, sessionId, null, 10_000,
                new JsonObject
                {
                    ["run_id"] = runId,
                    ["approval_id"] = approvalId,
                    ["command"] = "allow_once",
                }),
            cts.Token);
        var authorized = await ReceiveAsync(socket, cts.Token);
        Assert.Equal("authorized", authorized.Payload["status"]!.GetValue<string>());
        var authToken = authorized.Payload["authorization_token"]!.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(authToken));

        // Execute using the authorization token
        await SendAsync(
            socket,
            Execute("execute-terminal-risky", sessionId, runId, "terminal.run", authToken, riskyArgs.DeepClone().AsObject()),
            cts.Token);

        var execution = await ReceiveAsync(socket, cts.Token);
        Assert.Equal(AgentMessageTypes.ToolExecuteResult, execution.MessageType);
        Assert.Equal(ToolResultStatuses.Succeeded, execution.Payload["status"]!.GetValue<string>());
        Assert.True(execution.Payload["adapter_invoked"]!.GetValue<bool>());
        Assert.Equal(0, execution.Payload["output"]!["exit_code"]!.GetValue<int>());
        Assert.Contains("dotnet", execution.Payload["output"]!["stdout_head"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);

        // Replay must be rejected because token was consumed
        await SendAsync(
            socket,
            Execute("execute-terminal-replay", sessionId, runId, "terminal.run", authToken, riskyArgs.DeepClone().AsObject()),
            cts.Token);

        var replay = await ReceiveAsync(socket, cts.Token);
        Assert.Equal("decision_stale", replay.Payload["error_code"]!.GetValue<string>());
    }

    [Fact]
    public async Task V2_external_directory_grant_executes_terminal_shell()
    {
        await using var fixture = await HostFixture.StartAsync();
        var external = Path.Combine(Path.GetTempPath(), "ru-broker-shell-external-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        var targetFile = Path.Combine(external, "shell_target.txt");
        File.WriteAllText(targetFile, "delete me via terminal.shell");
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await socket.ConnectAsync(new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"), cts.Token);
            const string sessionId = "session-external-shell";
            const string runA = "run_external_shell_a111111111111";
            const string runB = "run_external_shell_b111111111111";

            // Run A: grant external directory
            await SendAsync(
                socket,
                new AgentEnvelope(2, "open-shell-a", AgentMessageTypes.RunOpen, sessionId, null, 10_000,
                    EnvelopeTestSnapshots.RunOpenPayload(runA, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardEmpty())),
                cts.Token);
            Assert.Equal(AgentMessageTypes.RunOpened, (await ReceiveAsync(socket, cts.Token)).MessageType);

            await SendAsync(
                socket,
                Evaluate("evaluate-shell-external-read", sessionId, runA, "files.read",
                    new JsonObject { ["path"] = targetFile }),
                cts.Token);
            var dirAsk = await ReceiveAsync(socket, cts.Token);
            Assert.Equal("external_directory_required", dirAsk.Payload["reason_code"]!.GetValue<string>());

            await SendAsync(
                socket,
                new AgentEnvelope(2, "approve-shell-external-dir", AgentMessageTypes.ApprovalRespond, sessionId, null, 10_000,
                    new JsonObject
                    {
                        ["run_id"] = runA,
                        ["approval_id"] = dirAsk.Payload["approval_id"]!.GetValue<string>(),
                        ["command"] = "allow_session_directory",
                    }),
                cts.Token);
            var authorizedDir = await ReceiveAsync(socket, cts.Token);
            Assert.Equal("authorized", authorizedDir.Payload["status"]!.GetValue<string>());

            // Run B: run terminal.shell inside granted external directory
            await SendAsync(
                socket,
                new AgentEnvelope(2, "open-shell-b", AgentMessageTypes.RunOpen, sessionId, null, 10_000,
                    EnvelopeTestSnapshots.RunOpenPayload(runB, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardEmpty())),
                cts.Token);
            Assert.Equal(AgentMessageTypes.RunOpened, (await ReceiveAsync(socket, cts.Token)).MessageType);

            var shellArgs = new JsonObject
            {
                ["working_directory"] = external,
                ["script"] = "Remove-Item ./shell_target.txt",
            };

            await SendAsync(socket, Evaluate("evaluate-shell-delete", sessionId, runB, "terminal.shell", shellArgs.DeepClone().AsObject()), cts.Token);
            var shellAsk = await ReceiveAsync(socket, cts.Token);
            Assert.Equal("ask", shellAsk.Payload["decision"]!.GetValue<string>());
            Assert.Equal("terminal_delete_requires_approval", shellAsk.Payload["reason_code"]!.GetValue<string>());
            var shellApprovalId = shellAsk.Payload["approval_id"]!.GetValue<string>();

            await SendAsync(
                socket,
                new AgentEnvelope(2, "approve-shell-delete", AgentMessageTypes.ApprovalRespond, sessionId, null, 10_000,
                    new JsonObject
                    {
                        ["run_id"] = runB,
                        ["approval_id"] = shellApprovalId,
                        ["command"] = "allow_once",
                    }),
                cts.Token);
            var authorizedShell = await ReceiveAsync(socket, cts.Token);
            Assert.Equal("authorized", authorizedShell.Payload["status"]!.GetValue<string>());
            var shellAuthToken = authorizedShell.Payload["authorization_token"]!.GetValue<string>();

            await SendAsync(
                socket,
                Execute("execute-shell-delete", sessionId, runB, "terminal.shell", shellAuthToken, shellArgs.DeepClone().AsObject()),
                cts.Token);

            var shellExec = await ReceiveAsync(socket, cts.Token);
            Assert.Equal(AgentMessageTypes.ToolExecuteResult, shellExec.MessageType);
            Assert.Equal(ToolResultStatuses.Succeeded, shellExec.Payload["status"]!.GetValue<string>());
            Assert.True(shellExec.Payload["adapter_invoked"]!.GetValue<bool>());
            Assert.Equal(0, shellExec.Payload["output"]!["exit_code"]!.GetValue<int>());
            Assert.False(File.Exists(targetFile), "targetFile should have been deleted by terminal.shell!");
            Assert.DoesNotContain("workspace_path_outside_root", shellExec.Payload.ToJsonString(), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(external))
            {
                Directory.Delete(external, recursive: true);
            }
        }
    }

    [Fact]
    public async Task V2_terminal_shell_safe_command_requires_approval_then_executes()
    {
        await using var fixture = await HostFixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await socket.ConnectAsync(new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"), cts.Token);
        const string sessionId = "session-shell-safe";
        const string runId = "run_shell_safe1111111111111111";
        await SendAsync(
            socket,
            new AgentEnvelope(2, "open-shell-safe", AgentMessageTypes.RunOpen, sessionId, null, 10_000,
                EnvelopeTestSnapshots.RunOpenPayload(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardEmpty())),
            cts.Token);
        Assert.Equal(AgentMessageTypes.RunOpened, (await ReceiveAsync(socket, cts.Token)).MessageType);

        var shellArgs = new JsonObject
        {
            ["script"] = "Get-Date",
            ["working_directory"] = ".",
        };

        await SendAsync(socket, Evaluate("evaluate-shell-safe", sessionId, runId, "terminal.shell", shellArgs.DeepClone().AsObject()), cts.Token);
        var decision = await ReceiveAsync(socket, cts.Token);
        Assert.Equal("ask", decision.Payload["decision"]!.GetValue<string>());
        Assert.Equal("terminal_unknown_executable", decision.Payload["reason_code"]!.GetValue<string>());
        var approvalId = decision.Payload["approval_id"]!.GetValue<string>();

        await SendAsync(
            socket,
            new AgentEnvelope(2, "respond-shell-safe", AgentMessageTypes.ApprovalRespond, sessionId, null, 10_000,
                new JsonObject
                {
                    ["run_id"] = runId,
                    ["approval_id"] = approvalId,
                    ["command"] = "allow_once",
                }),
            cts.Token);
        var authorized = await ReceiveAsync(socket, cts.Token);
        Assert.Equal("authorized", authorized.Payload["status"]!.GetValue<string>());
        var authorizationToken = authorized.Payload["authorization_token"]!.GetValue<string>();

        await SendAsync(
            socket,
            Execute("execute-shell-safe", sessionId, runId, "terminal.shell", authorizationToken, shellArgs.DeepClone().AsObject()),
            cts.Token);

        var execution = await ReceiveAsync(socket, cts.Token);
        Assert.Equal(AgentMessageTypes.ToolExecuteResult, execution.MessageType);
        Assert.Equal(ToolResultStatuses.Succeeded, execution.Payload["status"]!.GetValue<string>());
        Assert.True(execution.Payload["adapter_invoked"]!.GetValue<bool>());
        Assert.Equal(0, execution.Payload["output"]!["exit_code"]!.GetValue<int>());
    }

    [Fact]
    public async Task V2_terminal_approval_creates_no_reusable_grant()
    {
        await using var fixture = await HostFixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"), cts.Token);
        const string sessionId = "session-terminal-grant";
        const string runId = "run_terminal111111111111111111111111";
        await SendAsync(
            socket,
            new AgentEnvelope(2, "open-terminal-grant", AgentMessageTypes.RunOpen, sessionId, null, 10_000,
                EnvelopeTestSnapshots.RunOpenPayload(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardEmpty())),
            cts.Token);
        Assert.Equal(AgentMessageTypes.RunOpened, (await ReceiveAsync(socket, cts.Token)).MessageType);

        static JsonObject TerminalDeleteArgs() => new()
        {
            ["executable"] = "rm.exe",
            ["arguments"] = new JsonArray("target.txt"),
            ["working_directory"] = ".",
        };

        await SendAsync(socket, Evaluate("evaluate-terminal-delete", sessionId, runId, "terminal.run", TerminalDeleteArgs()), cts.Token);
        var ask = await ReceiveAsync(socket, cts.Token);
        Assert.Equal("ask", ask.Payload["decision"]!.GetValue<string>());
        Assert.Equal("terminal_delete_requires_approval", ask.Payload["reason_code"]!.GetValue<string>());
        var approvalId = ask.Payload["approval_id"]!.GetValue<string>();

        await SendAsync(
            socket,
            new AgentEnvelope(2, "approve-terminal-once", AgentMessageTypes.ApprovalRespond, sessionId, null, 10_000,
                new JsonObject
                {
                    ["run_id"] = runId,
                    ["approval_id"] = approvalId,
                    ["command"] = "allow_once",
                }),
            cts.Token);
        Assert.Equal("authorized", (await ReceiveAsync(socket, cts.Token)).Payload["status"]!.GetValue<string>());

        await SendAsync(socket, Evaluate("evaluate-terminal-again", sessionId, runId, "terminal.run", TerminalDeleteArgs()), cts.Token);
        var again = await ReceiveAsync(socket, cts.Token);
        Assert.Equal("ask", again.Payload["decision"]!.GetValue<string>());
        Assert.Equal("terminal_delete_requires_approval", again.Payload["reason_code"]!.GetValue<string>());
    }

    [Fact]
    public async Task V2_stale_approval_cannot_add_session_grant()
    {
        await using var fixture = await HostFixture.StartAsync();
        var external = Path.Combine(Path.GetTempPath(), "ru-broker-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "probe.txt"), "external");
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await socket.ConnectAsync(new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"), cts.Token);
            const string sessionId = "session-stale-grant";
            const string runId = "run_stale111111111111111111111111111";
            await SendAsync(
                socket,
                new AgentEnvelope(2, "open-stale", AgentMessageTypes.RunOpen, sessionId, null, 10_000,
                    EnvelopeTestSnapshots.RunOpenPayload(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardEmpty())),
                cts.Token);
            Assert.Equal(AgentMessageTypes.RunOpened, (await ReceiveAsync(socket, cts.Token)).MessageType);

            await SendAsync(
                socket,
                Evaluate("evaluate-stale-read", sessionId, runId, "files.read",
                    new JsonObject { ["path"] = Path.Combine(external, "probe.txt") }),
                cts.Token);
            var ask = await ReceiveAsync(socket, cts.Token);
            Assert.Equal("external_directory_required", ask.Payload["reason_code"]!.GetValue<string>());
            var approvalId = ask.Payload["approval_id"]!.GetValue<string>();

            await SendAsync(
                socket,
                new AgentEnvelope(2, "approve-stale", AgentMessageTypes.ApprovalRespond, sessionId, null, 10_000,
                    new JsonObject
                    {
                        ["run_id"] = runId,
                        ["approval_id"] = approvalId,
                        ["command"] = "allow_session_directory",
                        ["expected_intent_hash"] = new string('1', 64),
                    }),
                cts.Token);
            var stale = await ReceiveAsync(socket, cts.Token);
            Assert.Equal("approval_stale", stale.Payload["error_code"]!.GetValue<string>());

            await SendAsync(
                socket,
                Evaluate("evaluate-stale-again", sessionId, runId, "files.read",
                    new JsonObject { ["path"] = Path.Combine(external, "probe.txt") }),
                cts.Token);
            var again = await ReceiveAsync(socket, cts.Token);
            Assert.Equal("ask", again.Payload["decision"]!.GetValue<string>());
            Assert.Equal("external_directory_required", again.Payload["reason_code"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public async Task V2_revoke_removes_only_the_named_session_grants()
    {
        await using var fixture = await HostFixture.StartAsync();
        var external = Path.Combine(Path.GetTempPath(), "ru-broker-revoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "probe.txt"), "external");
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await socket.ConnectAsync(new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"), cts.Token);
            const string sessionId = "session-revoke";
            const string runId = "run_revoke111111111111111111111111";
            await SendAsync(
                socket,
                new AgentEnvelope(2, "open-revoke", AgentMessageTypes.RunOpen, sessionId, null, 10_000,
                    EnvelopeTestSnapshots.RunOpenPayload(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardEmpty())),
                cts.Token);
            Assert.Equal(AgentMessageTypes.RunOpened, (await ReceiveAsync(socket, cts.Token)).MessageType);

            await SendAsync(
                socket,
                Evaluate("evaluate-revoke-read", sessionId, runId, "files.read",
                    new JsonObject { ["path"] = Path.Combine(external, "probe.txt") }),
                cts.Token);
            var ask = await ReceiveAsync(socket, cts.Token);
            Assert.Equal("external_directory_required", ask.Payload["reason_code"]!.GetValue<string>());

            await SendAsync(
                socket,
                new AgentEnvelope(2, "approve-revoke", AgentMessageTypes.ApprovalRespond, sessionId, null, 10_000,
                    new JsonObject
                    {
                        ["run_id"] = runId,
                        ["approval_id"] = ask.Payload["approval_id"]!.GetValue<string>(),
                        ["command"] = "allow_session_directory",
                    }),
                cts.Token);
            Assert.Equal("authorized", (await ReceiveAsync(socket, cts.Token)).Payload["status"]!.GetValue<string>());

            await SendAsync(
                socket,
                new AgentEnvelope(2, "revoke-other", AgentMessageTypes.SessionGrantsRevoke, sessionId, null, 10_000,
                    new JsonObject { ["agent_session_id"] = "session-other" }),
                cts.Token);
            var revokedOther = await ReceiveAsync(socket, cts.Token);
            Assert.Equal(AgentMessageTypes.SessionGrantsRevoked, revokedOther.MessageType);

            await SendAsync(
                socket,
                Evaluate("evaluate-revoke-still-allowed", sessionId, runId, "files.read",
                    new JsonObject { ["path"] = Path.Combine(external, "probe.txt") }),
                cts.Token);
            Assert.Equal("allow", (await ReceiveAsync(socket, cts.Token)).Payload["decision"]!.GetValue<string>());

            await SendAsync(
                socket,
                new AgentEnvelope(2, "revoke-self", AgentMessageTypes.SessionGrantsRevoke, sessionId, null, 10_000,
                    new JsonObject { ["agent_session_id"] = sessionId }),
                cts.Token);
            Assert.Equal(AgentMessageTypes.SessionGrantsRevoked, (await ReceiveAsync(socket, cts.Token)).MessageType);

            await SendAsync(
                socket,
                Evaluate("evaluate-revoke-asks-again", sessionId, runId, "files.read",
                    new JsonObject { ["path"] = Path.Combine(external, "probe.txt") }),
                cts.Token);
            var again = await ReceiveAsync(socket, cts.Token);
            Assert.Equal("ask", again.Payload["decision"]!.GetValue<string>());
            Assert.Equal("external_directory_required", again.Payload["reason_code"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(external, recursive: true);
        }
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

        foreach (var toolId in new[] { "system.volume", "totally.unknown" })
        {
            var (piId, piArgs) = (toolId, new JsonObject { ["path"] = "." });
            await SendAsync(
                socket,
                new AgentEnvelope(2, "evaluate-" + toolId, AgentMessageTypes.ToolEvaluate, "session-unadv", null, 10_000,
                    new System.Text.Json.Nodes.JsonObject
                    {
                        ["run_id"] = "run_unad111111111111111111111111111",
                        ["tool_id"] = piId,
                        ["arguments"] = piArgs,
                    }),
                cts.Token);
            var reply = await ReceiveAsync(socket, cts.Token);
            Assert.Equal(ToolResultStatuses.Denied, reply.Payload["status"]!.GetValue<string>());
            Assert.Equal("phase_not_available", reply.Payload["error_code"]!.GetValue<string>());
        }

        await SendAsync(
            socket,
            new AgentEnvelope(2, "evaluate-web_search", AgentMessageTypes.ToolEvaluate, "session-unadv", null, 10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["run_id"] = "run_unad111111111111111111111111111",
                    ["tool_id"] = "web_search",
                    ["arguments"] = new JsonObject { ["query"] = "ru" },
                }),
            cts.Token);
        var webReply = await ReceiveAsync(socket, cts.Token);
        Assert.Equal("ask", webReply.Payload["decision"]!.GetValue<string>());
        Assert.Equal("web_approval_required", webReply.Payload["reason_code"]!.GetValue<string>());
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
                    ["tool_id"] = "read",
                    ["arguments"] = new System.Text.Json.Nodes.JsonObject { ["op"] = "list", ["path"] = "." },
                }),
            cts.Token);
        var decision = await ReceiveAsync(socket, cts.Token);
        var authorizationToken = decision.Payload["authorization_token"]!.GetValue<string>();

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
            new AgentEnvelope(2, "rebind-execute-stale", AgentMessageTypes.ToolExecute, sessionId, null, 10_000,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["run_id"] = runId,
                    ["tool_id"] = "read",
                    ["tool_version"] = "1.0.0",
                    ["authorization_token"] = authorizationToken,
                    ["arguments"] = new System.Text.Json.Nodes.JsonObject { ["op"] = "list", ["path"] = "." },
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
                    ["tool_id"] = "read",
                    ["arguments"] = new System.Text.Json.Nodes.JsonObject { ["op"] = "list", ["path"] = "." },
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
                : await BrokerHost.StartAsync(descriptorPath);
            return new HostFixture(root, token, host);
        }

        private static string? PreviousWorkspaceRoot { get; set; }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Environment.SetEnvironmentVariable("RU_WORKSPACE_ROOT", PreviousWorkspaceRoot);
            DeleteDirectoryResilient(Root);
        }

        private static int GetFreePort()
        {
            using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    /// <summary>
    /// The trusted preset's promised relaxation must be reachable through the real
    /// connection path. It depends on the evaluator receiving a workspace cleanliness
    /// provider: when <c>AgentConnection</c> omitted it, the provider defaulted to
    /// <c>null</c>, the guard <c>_cleanliness?.IsWorkspaceClean(...) != true</c> was
    /// always true, and every trusted session silently kept asking. This asserts the
    /// wired end-to-end decision so that regression cannot come back unnoticed.
    /// </summary>
    [Fact]
    public async Task V2_trusted_preset_auto_allows_workspace_risk_when_the_repo_is_clean()
    {
        await using var fixture = await HostFixture.StartAsync();
        InitializeCleanGitWorkspace(fixture.Root);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await socket.ConnectAsync(new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"), cts.Token);
        const string sessionId = "session-trusted-clean";
        const string runId = "run_trusted_clean111111111111111";
        await SendAsync(
            socket,
            new AgentEnvelope(2, "open-trusted-clean", AgentMessageTypes.RunOpen, sessionId, null, 10_000,
                EnvelopeTestSnapshots.RunOpenPayload(
                    runId,
                    sessionId,
                    fixture.Root,
                    EnvelopeTestSnapshots.Snapshot(1, "trusted", []))),
            cts.Token);
        Assert.Equal(AgentMessageTypes.RunOpened, (await ReceiveAsync(socket, cts.Token)).MessageType);

        // An unknown executable inside the workspace with no network intent is the
        // documented trusted_workspace_allow case; standard keeps asking for it.
        var riskyArgs = new JsonObject
        {
            ["executable"] = "where.exe",
            ["arguments"] = new JsonArray("dotnet"),
            ["working_directory"] = ".",
        };

        await SendAsync(socket, Evaluate("evaluate-trusted-clean", sessionId, runId, "terminal.run", riskyArgs.DeepClone().AsObject()), cts.Token);
        var decision = await ReceiveAsync(socket, cts.Token);
        Assert.Equal("allow", decision.Payload["decision"]!.GetValue<string>());
        Assert.Equal("trusted_workspace_allow", decision.Payload["reason_code"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(decision.Payload["authorization_token"]!.GetValue<string>()));
    }

    [Fact]
    public async Task V2_trusted_preset_still_asks_when_the_workspace_is_dirty()
    {
        await using var fixture = await HostFixture.StartAsync();
        InitializeCleanGitWorkspace(fixture.Root);
        File.WriteAllText(Path.Combine(fixture.Root, "uncommitted.txt"), "dirty");
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Token);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await socket.ConnectAsync(new Uri(fixture.Host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"), cts.Token);
        const string sessionId = "session-trusted-dirty";
        const string runId = "run_trusted_dirty111111111111111";
        await SendAsync(
            socket,
            new AgentEnvelope(2, "open-trusted-dirty", AgentMessageTypes.RunOpen, sessionId, null, 10_000,
                EnvelopeTestSnapshots.RunOpenPayload(
                    runId,
                    sessionId,
                    fixture.Root,
                    EnvelopeTestSnapshots.Snapshot(1, "trusted", []))),
            cts.Token);
        Assert.Equal(AgentMessageTypes.RunOpened, (await ReceiveAsync(socket, cts.Token)).MessageType);

        await SendAsync(
            socket,
            Evaluate(
                "evaluate-trusted-dirty",
                sessionId,
                runId,
                "terminal.run",
                new JsonObject
                {
                    ["executable"] = "where.exe",
                    ["arguments"] = new JsonArray("dotnet"),
                    ["working_directory"] = ".",
                }),
            cts.Token);
        var decision = await ReceiveAsync(socket, cts.Token);
        Assert.Equal("ask", decision.Payload["decision"]!.GetValue<string>());
        Assert.Equal("terminal_unknown_executable", decision.Payload["reason_code"]!.GetValue<string>());
    }

    /// <summary>
    /// Removes a temporary root whose git object files are read-only, which is the
    /// normal state of a fresh <c>git init</c> on Windows and makes a plain recursive
    /// delete fail.
    /// </summary>
    private static void DeleteDirectoryResilient(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch (IOException)
            {
                // A locked file still gets removed with its directory below.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        Directory.Delete(path, recursive: true);
    }

    /// <summary>
    /// Leaves <paramref name="root"/> as a commit-clean git work tree so the
    /// cleanliness probe reports it clean. Config is injected per invocation instead
    /// of written to the user's global git config, and the fixture deletes the root.
    /// </summary>
    private static void InitializeCleanGitWorkspace(string root)
    {
        File.WriteAllText(Path.Combine(root, "probe.txt"), "workspace-probe");
        RunGit(root, "init", "--quiet");
        RunGit(root, "add", "--all");
        RunGit(
            root,
            "-c", "user.email=broker-tests@example.invalid",
            "-c", "user.name=Broker Tests",
            "-c", "commit.gpgsign=false",
            "commit", "--quiet", "--no-gpg-sign", "-m", "seed");
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        Assert.True(process.Start(), "git is required by the Broker test suite");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "git did not exit within the test budget");
        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(' ', arguments)} failed with {process.ExitCode}: {stdout}{stderr}");
    }

    private static AgentEnvelope Evaluate(
        string requestId,
        string sessionId,
        string runId,
        string toolId,
        JsonObject arguments)
    {
        var (piId, piArgs) = MapToPiArgs(toolId, arguments);
        return new AgentEnvelope(
            2,
            requestId,
            AgentMessageTypes.ToolEvaluate,
            sessionId,
            null,
            10_000,
            new JsonObject
            {
                ["run_id"] = runId,
                ["tool_id"] = piId,
                ["tool_version"] = "1.0.0",
                ["arguments"] = piArgs,
            });
    }

    private static AgentEnvelope Execute(
        string requestId,
        string sessionId,
        string runId,
        string toolId,
        string authorizationToken,
        JsonObject arguments)
    {
        var (piId, piArgs) = MapToPiArgs(toolId, arguments);
        return new AgentEnvelope(
            2,
            requestId,
            AgentMessageTypes.ToolExecute,
            sessionId,
            null,
            10_000,
            new JsonObject
            {
                ["run_id"] = runId,
                ["tool_id"] = piId,
                ["tool_version"] = "1.0.0",
                ["authorization_token"] = authorizationToken,
                ["arguments"] = piArgs,
            });
    }

    private static (string PiId, JsonObject PiArgs) MapToPiArgs(string toolId, JsonObject arguments)
    {
        var mapped = (JsonObject)arguments.DeepClone();
        string? op = null;
        string? mode = null;
        string piId = toolId;
        switch (toolId)
        {
            case "files.list": piId = "read"; op = "list"; break;
            case "files.glob": piId = "glob"; mapped.Remove("op"); break;
            case "files.search": piId = "grep"; mapped.Remove("op"); break;
            case "files.read": piId = "read"; op = "read"; break;
            case "files.write": piId = "write"; op = "write"; break;
            case "files.edit": piId = "edit"; break;
            case "files.delete": piId = "write"; op = "delete"; break;
            case "files.restore": piId = "write"; op = "restore"; break;
            case "terminal.run": piId = "bash"; mode = "run"; break;
            case "terminal.shell": piId = "bash"; mode = "shell"; break;
            case "web.search": piId = "web_search"; mapped.Remove("op"); break;
            case "web.fetch": piId = "web_fetch"; mapped.Remove("op"); break;
            case "help.docs": piId = "read"; op = "help"; break;
        }

        if (op is not null) mapped["op"] = op;
        if (mode is not null) mapped["mode"] = mode;
        return (piId, mapped);
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


