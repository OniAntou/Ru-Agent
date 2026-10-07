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

    [Fact]
    public void Spilled_entry_pages_the_file_half_and_cleans_up_with_the_session()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-spool-" + Guid.NewGuid().ToString("N"));
        try
        {
            var spool = new TerminalOutputSpool(root);
            var prefix = new string('a', Protocol.TerminalLimits.MaxSpoolChars);
            var filePath = spool.CreateFilePath("session-a", "req-1");
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, "tail");
            spool.Store("session-a", "req-1", prefix, new SpoolSpill(filePath, prefix.Length + 4, Truncated: false));

            Assert.True(spool.TryReadPage(
                "session-a",
                "req-1",
                Protocol.TerminalLimits.MaxSpoolChars - 2,
                6,
                out var page));
            Assert.Equal("aatail", page.Text);
            Assert.Equal(prefix.Length + 4, page.TotalChars);
            Assert.Equal(filePath, page.FilePath);
            Assert.False(page.Truncated);

            Assert.True(spool.TryGetSpill("session-a", "req-1", out var named));
            Assert.Equal(filePath, named);

            spool.RemoveSession("session-a");
            Assert.False(File.Exists(filePath));
            Assert.False(spool.TryReadPage("session-a", "req-1", 0, 10, out _));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Eviction_deletes_the_evicted_spill_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-spool-" + Guid.NewGuid().ToString("N"));
        try
        {
            var spool = new TerminalOutputSpool(root);
            var first = spool.CreateFilePath("session-a", "req-0");
            Directory.CreateDirectory(Path.GetDirectoryName(first)!);
            File.WriteAllText(first, "x");
            spool.Store("session-a", "req-0", "window", new SpoolSpill(first, 6, Truncated: false));
            for (var index = 1; index <= TerminalOutputSpool.MaxSpoolsPerSession; index++)
            {
                spool.Store("session-a", $"req-{index}", $"output-{index}");
            }

            Assert.False(File.Exists(first));
            Assert.False(spool.TryRead("session-a", "req-0", out _));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
