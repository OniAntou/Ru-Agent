using System.Net;
using RuAgent.Cli.Rpc;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Cli.Tests.Rpc;

public sealed class RpcDispatchSurfaceTests
{
    private static async Task<RpcDispatcher> CreateInitializedAsync(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler,
        List<string>? seen = null)
    {
        seen ??= [];
        var http = RpcTestHost.CreateClient(request =>
        {
            seen.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return handler(request);
        });
        var session = RpcTestHost.CreateSession();
        var dispatcher = new RpcDispatcher(
            session,
            new SessionApiClient(session, http),
            new AgentApiClient(session, http),
            new SettingsApiClient(session, http),
            runStarter: null!,
            runBridge: null!,
            log: TextWriter.Null,
            httpClient: http);
        await dispatcher.DispatchAsync(Req("initialize", "{\"protocolVersion\":1}"), CancellationToken.None);
        return dispatcher;
    }

    private static RpcRequest Req(string method, string paramsJson = "{}")
    {
        var line = $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"{method}\",\"params\":{paramsJson}}}";
        var parsed = RpcMessage.TryParseRequest(line, out var error);
        Assert.Null(error);
        return parsed!;
    }

    [Fact]
    public async Task Session_create_returns_the_session_id()
    {
        var dispatcher = await CreateInitializedAsync(_ =>
            Task.FromResult(RpcTestHost.Json("{\"status\":\"ok\",\"session_id\":\"sess_1\"}")));
        var result = await dispatcher.DispatchAsync(Req("session.create"), CancellationToken.None);
        Assert.Equal("sess_1", result!["session_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Session_list_passes_through_the_envelope()
    {
        var dispatcher = await CreateInitializedAsync(_ =>
            Task.FromResult(RpcTestHost.Json("{\"status\":\"ok\",\"sessions\":[]}")));
        var result = await dispatcher.DispatchAsync(Req("session.list"), CancellationToken.None);
        Assert.Equal("ok", result!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Session_fork_posts_the_index_and_title()
    {
        string? body = null;
        var dispatcher = await CreateInitializedAsync(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return RpcTestHost.Json("{\"status\":\"ok\"}");
        });
        await dispatcher.DispatchAsync(
            Req("session.fork", "{\"sessionId\":\"sess_1\",\"index\":2,\"title\":\"branch\"}"),
            CancellationToken.None);
        Assert.Contains("\"up_to_message_index\":2", body);
        Assert.Contains("\"new_title\":\"branch\"", body);
    }

    [Fact]
    public async Task Session_fork_without_index_is_rejected()
    {
        var dispatcher = await CreateInitializedAsync(_ => Task.FromResult(RpcTestHost.Json("{}")));
        var ex = await Assert.ThrowsAsync<RpcDispatchException>(
            () => dispatcher.DispatchAsync(Req("session.fork", "{\"sessionId\":\"sess_1\"}"), CancellationToken.None));
        Assert.Equal(RpcProtocol.InvalidParams, ex.RpcErrorCode);
    }

    [Fact]
    public async Task Policy_remove_requires_explicit_confirmation()
    {
        var dispatcher = await CreateInitializedAsync(_ => Task.FromResult(RpcTestHost.Json("{}")));
        var ex = await Assert.ThrowsAsync<RpcDispatchException>(
            () => dispatcher.DispatchAsync(
                Req("policy.remove", "{\"ruleId\":\"rule_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}"),
                CancellationToken.None));
        Assert.Equal(RpcProtocol.InvalidParams, ex.RpcErrorCode);
    }

    [Fact]
    public async Task AgentApiException_from_a_route_surfaces_as_ruCode()
    {
        List<string> seen = [];
        var dispatcher = await CreateInitializedAsync(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent("{\"error_code\":\"approval_stale\",\"message\":\"stale\"}"),
            }), seen);
        var ex = await Assert.ThrowsAsync<RpcDispatchException>(
            () => dispatcher.DispatchAsync(
                Req("policy.remove", "{\"ruleId\":\"rule_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"confirm\":true}"),
                CancellationToken.None));
        Assert.Equal(RpcProtocol.ApplicationError, ex.RpcErrorCode);
        Assert.Equal("approval_stale", ex.RuCode);
        Assert.Equal(409, ex.HttpStatus);
    }
}
