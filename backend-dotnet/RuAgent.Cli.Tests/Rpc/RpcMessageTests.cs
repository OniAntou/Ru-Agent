using RuAgent.Cli.Rpc;
using Xunit;

namespace RuAgent.Cli.Tests.Rpc;

public sealed class RpcMessageTests
{
    [Fact]
    public void TryParseRequest_accepts_a_request_with_object_params()
    {
        var request = RpcMessage.TryParseRequest(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"host.status\",\"params\":{}}", out var error);
        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal("host.status", request!.Method);
        Assert.Equal(1, request.Id!.GetValue<int>());
        Assert.False(request.IsNotification);
    }

    [Fact]
    public void TryParseRequest_accepts_a_notification_without_id()
    {
        var request = RpcMessage.TryParseRequest(
            "{\"jsonrpc\":\"2.0\",\"method\":\"$/cancelRequest\",\"params\":{\"id\":\"3\"}}", out var error);
        Assert.Null(error);
        Assert.True(request!.IsNotification);
    }

    [Fact]
    public void TryParseRequest_returns_parse_error_for_broken_json()
    {
        var request = RpcMessage.TryParseRequest("{not json", out var error);
        Assert.Null(request);
        Assert.Equal(RpcProtocol.ParseError, error!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void TryParseRequest_rejects_a_wrong_jsonrpc_version_but_keeps_the_id()
    {
        var request = RpcMessage.TryParseRequest(
            "{\"jsonrpc\":\"1.0\",\"id\":\"a\",\"method\":\"x\"}", out var error);
        Assert.Null(request);
        Assert.Equal(RpcProtocol.InvalidRequest, error!["error"]!["code"]!.GetValue<int>());
        Assert.Equal("a", error["id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":true,\"method\":\"x\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"x\",\"params\":[1,2]}")]
    [InlineData("[1,2,3]")]
    public void TryParseRequest_rejects_invalid_requests(string line)
    {
        var request = RpcMessage.TryParseRequest(line, out var error);
        Assert.Null(request);
        Assert.NotNull(error);
    }

    [Fact]
    public void ErrorResponse_carries_ruCode_and_httpStatus_in_data()
    {
        var error = RpcMessage.ErrorResponse(null, RpcProtocol.ApplicationError, "approval_stale", "approval_stale", 409);
        Assert.Equal(-32000, error["error"]!["code"]!.GetValue<int>());
        Assert.Equal("approval_stale", error["error"]!["data"]!["ruCode"]!.GetValue<string>());
        Assert.Equal(409, error["error"]!["data"]!["httpStatus"]!.GetValue<int>());
    }
}
