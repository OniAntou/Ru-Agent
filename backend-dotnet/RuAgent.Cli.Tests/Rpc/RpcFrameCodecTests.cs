using RuAgent.Cli.Rpc;
using Xunit;

namespace RuAgent.Cli.Tests.Rpc;

public sealed class RpcFrameCodecTests
{
    [Fact]
    public async Task ReadFrame_ignores_one_byte_order_mark_at_the_start_of_the_stream_only()
    {
        using var reader = new StringReader("﻿{\"a\":1}\n﻿{\"b\":2}\n");
        var first = await RpcFrameCodec.ReadFrameAsync(reader, 1024, CancellationToken.None);
        Assert.Equal(RpcFrameStatus.Line, first.Status);
        Assert.Equal("{\"a\":1}", first.Line);
        var second = await RpcFrameCodec.ReadFrameAsync(reader, 1024, CancellationToken.None);
        Assert.Equal("﻿{\"b\":2}", second.Line);
    }

    [Fact]
    public async Task ReadFrame_returns_the_line_without_the_newline()
    {
        using var reader = new StringReader("{\"a\":1}\n");
        var result = await RpcFrameCodec.ReadFrameAsync(reader, 1024, CancellationToken.None);
        Assert.Equal(RpcFrameStatus.Line, result.Status);
        Assert.Equal("{\"a\":1}", result.Line);
    }

    [Fact]
    public async Task ReadFrame_strips_carriage_returns_and_handles_eof_without_newline()
    {
        using var reader = new StringReader("{\"a\":1}\r\n{\"b\":2}");
        var first = await RpcFrameCodec.ReadFrameAsync(reader, 1024, CancellationToken.None);
        Assert.Equal("{\"a\":1}", first.Line);
        var second = await RpcFrameCodec.ReadFrameAsync(reader, 1024, CancellationToken.None);
        Assert.Equal(RpcFrameStatus.Line, second.Status);
        Assert.Equal("{\"b\":2}", second.Line);
        var third = await RpcFrameCodec.ReadFrameAsync(reader, 1024, CancellationToken.None);
        Assert.Equal(RpcFrameStatus.EndOfInput, third.Status);
    }

    [Fact]
    public async Task ReadFrame_discards_an_oversized_line_and_recovers_on_the_next_line()
    {
        var oversized = new string('x', 2000);
        using var reader = new StringReader(oversized + "\n{\"ok\":true}\n");
        var first = await RpcFrameCodec.ReadFrameAsync(reader, 1024, CancellationToken.None);
        Assert.Equal(RpcFrameStatus.TooLarge, first.Status);
        var second = await RpcFrameCodec.ReadFrameAsync(reader, 1024, CancellationToken.None);
        Assert.Equal(RpcFrameStatus.Line, second.Status);
        Assert.Equal("{\"ok\":true}", second.Line);
    }

    [Fact]
    public async Task ReadFrame_caps_the_line_in_utf8_bytes_not_characters()
    {
        // "é" is 2 UTF-8 bytes, so three of them are 6 bytes against a 4-byte cap.
        using var reader = new StringReader("ééé\n{é}\n");
        var first = await RpcFrameCodec.ReadFrameAsync(reader, 4, CancellationToken.None);
        Assert.Equal(RpcFrameStatus.TooLarge, first.Status);
        var second = await RpcFrameCodec.ReadFrameAsync(reader, 4, CancellationToken.None);
        Assert.Equal(RpcFrameStatus.Line, second.Status);
        Assert.Equal("{é}", second.Line);
    }

    [Fact]
    public async Task ReadFrame_accepts_a_line_at_the_exact_byte_boundary()
    {
        using var reader = new StringReader("éé\n");
        var result = await RpcFrameCodec.ReadFrameAsync(reader, 4, CancellationToken.None);
        Assert.Equal(RpcFrameStatus.Line, result.Status);
        Assert.Equal("éé", result.Line);
    }

    [Fact]
    public async Task ReadFrame_counts_a_surrogate_pair_as_four_bytes()
    {
        using var tooSmall = new StringReader("😀\n");
        var oversized = await RpcFrameCodec.ReadFrameAsync(tooSmall, 3, CancellationToken.None);
        Assert.Equal(RpcFrameStatus.TooLarge, oversized.Status);

        using var exact = new StringReader("😀\n");
        var result = await RpcFrameCodec.ReadFrameAsync(exact, 4, CancellationToken.None);
        Assert.Equal(RpcFrameStatus.Line, result.Status);
        Assert.Equal("😀", result.Line);
    }

    [Fact]
    public async Task ReadFrame_handles_a_line_larger_than_the_read_buffer()
    {
        var line = "{\"method\":\"" + new string('m', 5000) + "\"}";
        using var reader = new StringReader(line + "\n");
        var result = await RpcFrameCodec.ReadFrameAsync(reader, 64 * 1024, CancellationToken.None);
        Assert.Equal(RpcFrameStatus.Line, result.Status);
        Assert.Equal(line, result.Line);
    }

    [Fact]
    public async Task WriteFrame_writes_one_compact_json_line()
    {
        using var writer = new StringWriter();
        var message = RpcMessage.Notification(
            "run.event",
            new System.Text.Json.Nodes.JsonObject { ["sequence"] = 7 });
        await RpcFrameCodec.WriteFrameAsync(writer, message, CancellationToken.None);
        var text = writer.ToString();
        Assert.EndsWith("\n", text);
        Assert.Single(text.TrimEnd('\n').Split('\n'));
        Assert.Contains("\"method\":\"run.event\"", text);
    }
}
