using System.Text.Json;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ChatWebSocketClientContractTests
{
    [Fact]
    public void SerializeRequest_preserves_caller_correlation_id()
    {
        var json = ChatWebSocketClient.SerializeRequest(
            "Xin chào",
            "session-1",
            "c_12345678");
        using var document = JsonDocument.Parse(json);

        Assert.Equal("Xin chào", document.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("session-1", document.RootElement.GetProperty("session_id").GetString());
        Assert.Equal("c_12345678", document.RootElement.GetProperty("correlation_id").GetString());
    }

    [Fact]
    public void TryParseFrame_reads_typed_public_fields_and_rejects_missing_type()
    {
        Assert.True(ChatWebSocketClient.TryParseFrame(
            "{\"type\":\"error\",\"content\":\"Lỗi\",\"error_code\":\"busy\",\"correlation_id\":\"c_12345678\"}",
            out var frame));
        Assert.Equal("error", frame!.Type);
        Assert.Equal("busy", frame.ErrorCode);
        Assert.Equal("c_12345678", frame.CorrelationId);

        Assert.False(ChatWebSocketClient.TryParseFrame("{\"content\":\"không có type\"}", out _));
    }

    [Fact]
    public void TryParseFrame_rejects_an_unsafe_correlation_id()
    {
        Assert.False(ChatWebSocketClient.TryParseFrame(
            "{\"type\":\"chunk\",\"content\":\"x\",\"correlation_id\":\"raw path\\\\secret\"}",
            out _));
    }

    [Fact]
    public void TryParseFrame_rejects_unknown_or_duplicate_fields()
    {
        Assert.False(ChatWebSocketClient.TryParseFrame(
            "{\"type\":\"chunk\",\"content\":\"x\",\"correlation_id\":\"c_12345678\",\"prompt\":\"secret\"}",
            out _));
        Assert.False(ChatWebSocketClient.TryParseFrame(
            "{\"type\":\"chunk\",\"content\":\"x\",\"content\":\"leak\",\"correlation_id\":\"c_12345678\"}",
            out _));
    }
}
