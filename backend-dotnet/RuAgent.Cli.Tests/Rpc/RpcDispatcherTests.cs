using System.Text.Json;
using RuAgent.Cli.Rpc;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Cli.Tests.Rpc;

public sealed class RpcDispatcherTests
{
    private static (RpcDispatcher Dispatcher, List<string> Requests) CreateDispatcher()
    {
        var requests = new List<string>();
        var http = RpcTestHost.CreateClient(request =>
        {
            var uri = request.RequestUri!.ToString();
            requests.Add(uri);
            if (uri.Contains("/settings"))
            {
                return Task.FromResult(RpcTestHost.Json("{\"status\":\"ok\"}"));
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
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
        return (dispatcher, requests);
    }

    private static RpcRequest Request(string method, string paramsJson = "{}")
    {
        var line = $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"{method}\",\"params\":{paramsJson}}}";
        var request = RpcMessage.TryParseRequest(line, out var error);
        Assert.Null(error);
        return request!;
    }

    [Fact]
    public async Task Initialize_returns_version_methods_and_notifications()
    {
        var (dispatcher, _) = CreateDispatcher();
        var result = await dispatcher.DispatchAsync(
            Request("initialize", "{\"protocolVersion\":1,\"clientInfo\":{\"name\":\"t\",\"version\":\"0\"}}"),
            CancellationToken.None);
        Assert.Equal(1, result!["protocolVersion"]!.GetValue<int>());
        var methods = result["methods"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Contains("run.start", methods);
        Assert.Contains("initialize", methods);
        var notifications = result["notifications"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Contains("run.event", notifications);
    }

    [Fact]
    public async Task Initialize_rejects_a_second_call()
    {
        var (dispatcher, _) = CreateDispatcher();
        await dispatcher.DispatchAsync(Request("initialize", "{\"protocolVersion\":1}"), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<RpcDispatchException>(
            () => dispatcher.DispatchAsync(Request("initialize", "{\"protocolVersion\":1}"), CancellationToken.None));
        Assert.Equal(RpcProtocol.AlreadyInitialized, ex.RpcErrorCode);
    }

    [Fact]
    public async Task Unsupported_protocol_version_is_rejected()
    {
        var (dispatcher, _) = CreateDispatcher();
        var ex = await Assert.ThrowsAsync<RpcDispatchException>(
            () => dispatcher.DispatchAsync(Request("initialize", "{\"protocolVersion\":99}"), CancellationToken.None));
        Assert.Equal("unsupported_protocol", ex.RuCode);
    }

    [Fact]
    public async Task Methods_before_initialize_are_rejected()
    {
        var (dispatcher, _) = CreateDispatcher();
        var ex = await Assert.ThrowsAsync<RpcDispatchException>(
            () => dispatcher.DispatchAsync(Request("host.status"), CancellationToken.None));
        Assert.Equal(RpcProtocol.NotInitialized, ex.RpcErrorCode);
    }

    [Fact]
    public async Task Unknown_method_returns_method_not_found()
    {
        var (dispatcher, _) = CreateDispatcher();
        await dispatcher.DispatchAsync(Request("initialize", "{\"protocolVersion\":1}"), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<RpcDispatchException>(
            () => dispatcher.DispatchAsync(Request("nope.method"), CancellationToken.None));
        Assert.Equal(RpcProtocol.MethodNotFound, ex.RpcErrorCode);
    }

    [Fact]
    public async Task Host_status_hits_settings_and_never_returns_the_bearer()
    {
        var (dispatcher, requests) = CreateDispatcher();
        await dispatcher.DispatchAsync(Request("initialize", "{\"protocolVersion\":1}"), CancellationToken.None);
        var result = await dispatcher.DispatchAsync(Request("host.status"), CancellationToken.None);
        Assert.True(result!["ready"]!.GetValue<bool>());
        Assert.Contains(requests, r => r.Contains("/settings"));
        Assert.DoesNotContain("test-bearer", result.ToJsonString());
    }
}
