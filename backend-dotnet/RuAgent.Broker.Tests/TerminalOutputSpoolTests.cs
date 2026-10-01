using RuAgent.Broker.Tools.Terminal;
using Xunit;

namespace RuAgent.Broker.Tests;

/// <summary>
/// Session-scoped retention of full terminal output. A spool id must resolve
/// only inside the session that produced it, and a chatty session must evict
/// instead of growing memory.
/// </summary>
public sealed class TerminalOutputSpoolTests
{
    [Fact]
    public void Store_and_read_roundtrip()
    {
        var spool = new TerminalOutputSpool();
        spool.Store("session-a", "req-1", "hello output");

        Assert.True(spool.TryRead("session-a", "req-1", out var text));
        Assert.Equal("hello output", text);
    }

    [Fact]
    public void Unknown_session_or_id_fails_closed()
    {
        var spool = new TerminalOutputSpool();
        spool.Store("session-a", "req-1", "hello output");

        Assert.False(spool.TryRead("session-a", "req-2", out _));
        Assert.False(spool.TryRead("session-b", "req-1", out _));
        Assert.False(spool.TryRead(string.Empty, "req-1", out _));
        Assert.False(spool.TryRead("session-a", "  ", out _));
    }

    [Fact]
    public void Same_id_is_replaced_not_duplicated()
    {
        var spool = new TerminalOutputSpool();
        spool.Store("session-a", "req-1", "first");
        spool.Store("session-a", "req-1", "second");

        Assert.True(spool.TryRead("session-a", "req-1", out var text));
        Assert.Equal("second", text);
    }

    [Fact]
    public void Oldest_spool_is_evicted_past_the_cap()
    {
        var spool = new TerminalOutputSpool();
        for (int index = 0; index <= TerminalOutputSpool.MaxSpoolsPerSession; index++)
        {
            spool.Store("session-a", $"req-{index}", $"output-{index}");
        }

        Assert.False(spool.TryRead("session-a", "req-0", out _));
        Assert.True(spool.TryRead("session-a", $"req-{TerminalOutputSpool.MaxSpoolsPerSession}", out var text));
        Assert.Equal($"output-{TerminalOutputSpool.MaxSpoolsPerSession}", text);
    }

    [Fact]
    public void Oversized_text_is_truncated_to_the_cap()
    {
        var spool = new TerminalOutputSpool();
        spool.Store("session-a", "req-1", new string('x', Protocol.TerminalLimits.MaxSpoolChars + 100));

        Assert.True(spool.TryRead("session-a", "req-1", out var text));
        Assert.Equal(Protocol.TerminalLimits.MaxSpoolChars, text.Length);
    }

    [Fact]
    public void RemoveSession_clears_only_that_session()
    {
        var spool = new TerminalOutputSpool();
        spool.Store("session-a", "req-1", "a");
        spool.Store("session-b", "req-1", "b");
        spool.RemoveSession("session-a");

        Assert.False(spool.TryRead("session-a", "req-1", out _));
        Assert.True(spool.TryRead("session-b", "req-1", out var text));
        Assert.Equal("b", text);
    }
}
