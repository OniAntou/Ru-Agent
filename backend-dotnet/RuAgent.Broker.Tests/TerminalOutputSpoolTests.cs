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
            spool.Store("session-a", "req-1", prefix, new SpoolSpill(filePath, prefix.Length + 4, 4, Truncated: false));

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
            spool.Store("session-a", "req-0", "window", new SpoolSpill(first, 6, 1, Truncated: false));
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

    [Fact]
    public void Session_byte_quota_evicts_the_oldest_spill()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-spool-" + Guid.NewGuid().ToString("N"));
        try
        {
            var spool = new TerminalOutputSpool(root);
            var first = spool.CreateFilePath("session-a", "req-0");
            Directory.CreateDirectory(Path.GetDirectoryName(first)!);
            File.WriteAllText(first, "x");
            spool.Store(
                "session-a",
                "req-0",
                "old",
                new SpoolSpill(first, 3, Protocol.TerminalLimits.MaxSpoolSessionBytes, Truncated: false));
            var second = spool.CreateFilePath("session-a", "req-1");
            File.WriteAllText(second, "y");
            spool.Store("session-a", "req-1", "new", new SpoolSpill(second, 3, 1, Truncated: false));

            Assert.False(File.Exists(first));
            Assert.False(spool.TryRead("session-a", "req-0", out _));
            Assert.True(spool.TryRead("session-a", "req-1", out var text));
            Assert.Equal("new", text);
            Assert.True(File.Exists(second));
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
    public void Concurrent_writers_cannot_exceed_the_session_byte_budget()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-spool-" + Guid.NewGuid().ToString("N"));
        try
        {
            var spool = new TerminalOutputSpool(root, maxSessionBytes: 1000);
            var first = spool.CreateWriter("session-a", "req-1");
            var second = spool.CreateWriter("session-a", "req-2");

            first.Append(new string('a', Protocol.TerminalLimits.MaxSpoolChars));
            first.Append(new string('b', 800));
            second.Append(new string('a', Protocol.TerminalLimits.MaxSpoolChars));
            second.Append(new string('b', 800));
            first.Complete();
            second.Complete();

            Assert.False(first.Truncated);
            Assert.True(second.Truncated);
            Assert.Equal(800, new FileInfo(first.FilePath!).Length);
            Assert.Equal(200, new FileInfo(second.FilePath!).Length);
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
    public void Store_eviction_reclaims_spill_files_after_live_writer_claims()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-spool-" + Guid.NewGuid().ToString("N"));
        try
        {
            var budget = 2L * Protocol.TerminalLimits.MaxSpoolChars + 800;
            var spool = new TerminalOutputSpool(root, maxSessionBytes: budget);
            var first = spool.CreateWriter("session-a", "req-1");
            var second = spool.CreateWriter("session-a", "req-2");

            first.Append(new string('a', Protocol.TerminalLimits.MaxSpoolChars));
            first.Append(new string('b', 800));
            second.Append(new string('a', Protocol.TerminalLimits.MaxSpoolChars));
            second.Append(new string('c', 800));
            first.Complete();
            second.Complete();

            Assert.False(first.Truncated);
            Assert.False(second.Truncated);
            spool.Store("session-a", "req-1", first);
            spool.Store("session-a", "req-2", second);

            Assert.False(File.Exists(first.FilePath!));
            Assert.False(spool.TryRead("session-a", "req-1", out _));
            Assert.True(spool.TryRead("session-a", "req-2", out _));
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
    public void Old_writer_release_cannot_reduce_reopened_session_claims()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-spool-" + Guid.NewGuid().ToString("N"));
        try
        {
            var spool = new TerminalOutputSpool(root, maxSessionBytes: 1000);
            var old = spool.CreateWriter("session-a", "old");
            old.Append(new string('a', Protocol.TerminalLimits.MaxSpoolChars));
            old.Append(new string('b', 1000));
            old.Complete();
            spool.RemoveSession("session-a");

            var current = spool.CreateWriter("session-a", "current");
            current.Append(new string('c', Protocol.TerminalLimits.MaxSpoolChars));
            current.Append(new string('d', 800));
            old.ReleaseBudget();

            var competing = spool.CreateWriter("session-a", "competing");
            competing.Append(new string('e', Protocol.TerminalLimits.MaxSpoolChars));
            competing.Append(new string('f', 900));
            current.Complete();
            competing.Complete();

            Assert.False(current.Truncated);
            Assert.True(competing.Truncated);
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
    public void Removed_generation_writer_cannot_append_or_store_into_reopened_session()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-spool-" + Guid.NewGuid().ToString("N"));
        try
        {
            var spool = new TerminalOutputSpool(root, maxSessionBytes: 1000);
            var old = spool.CreateWriter("session-a", "same-id");
            old.Append(new string('a', Protocol.TerminalLimits.MaxSpoolChars));
            old.Append(new string('b', 100));
            old.Complete();
            var oldPath = old.FilePath!;
            spool.RemoveSession("session-a");

            var current = spool.CreateWriter("session-a", "same-id");
            old.Append(new string('x', 100));
            spool.Store("session-a", "same-id", old);

            Assert.False(spool.TryRead("session-a", "same-id", out _));
            Assert.NotEqual(oldPath, spool.CreateFilePath("session-a", "same-id"));
            Assert.False(File.Exists(oldPath));
            Assert.Equal(0, current.TotalChars);
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
    public void Removing_active_writer_invalidates_it_and_cleans_only_its_generation()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-spool-" + Guid.NewGuid().ToString("N"));
        try
        {
            var spool = new TerminalOutputSpool(
                root,
                maxSessionBytes: Protocol.TerminalLimits.MaxSpoolChars + 1000L);
            var old = spool.CreateWriter("session-a", "same-id");
            old.Append(new string('a', Protocol.TerminalLimits.MaxSpoolChars));
            old.Append(new string('b', 100));
            var oldPath = old.FilePath!;
            spool.RemoveSession("session-a");

            var current = spool.CreateWriter("session-a", "same-id");
            current.Append(new string('c', Protocol.TerminalLimits.MaxSpoolChars));
            current.Append(new string('d', 100));
            old.Append(new string('x', 100));
            old.Complete();
            current.Complete();
            spool.Store("session-a", "same-id", old);
            spool.Store("session-a", "same-id", current);

            Assert.False(File.Exists(oldPath));
            Assert.NotEqual(oldPath, current.FilePath);
            Assert.True(File.Exists(current.FilePath));
            Assert.True(spool.TryRead("session-a", "same-id", out var text));
            Assert.Equal(new string('c', Protocol.TerminalLimits.MaxSpoolChars), text);
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
    public async Task Blocked_generation_action_does_not_block_another_session_writer()
    {
        var spool = new TerminalOutputSpool();
        var blockedWriter = spool.CreateWriter("session-a", "blocked");
        var independentWriter = spool.CreateWriter("session-b", "independent");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocker = Task.Run(() => blockedWriter.Budget!.RunIfCurrent(() =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
        }));

        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var independent = Task.Run(() => independentWriter.Append("independent output"));

            Assert.True(await Task.WhenAny(independent, Task.Delay(TimeSpan.FromSeconds(1))) == independent);
            await independent;
        }
        finally
        {
            release.Set();
            await blocker;
        }
    }

    [Fact]
    public async Task Budget_release_waits_for_generation_append_gate()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-spool-" + Guid.NewGuid().ToString("N"));
        SpoolFileWriter? writer = null;
        try
        {
            var spool = new TerminalOutputSpool(root, maxSessionBytes: 1000);
            writer = spool.CreateWriter("session-a", "release-gate");
            writer.Append(new string('a', Protocol.TerminalLimits.MaxSpoolChars));
            writer.Append(new string('b', 100));
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var gateOwner = Task.Run(() => writer.Budget!.RunIfCurrent(() =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            }));

            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                var budgetRelease = Task.Run(writer.ReleaseBudget);

                Assert.NotEqual(budgetRelease, await Task.WhenAny(budgetRelease, Task.Delay(250)));
                release.Set();
                await budgetRelease;
            }
            finally
            {
                release.Set();
                await gateOwner;
            }

            writer.Complete();
        }
        finally
        {
            writer?.Complete();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Unopenable_spill_file_reports_truncation_through_the_stored_page()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-spool-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var spool = new TerminalOutputSpool(root);
            var blockedSession = "blocked-session";
            File.WriteAllText(Path.Combine(root, blockedSession), "x");
            var writer = spool.CreateWriter(blockedSession, "req-1");

            writer.Append(new string('a', Protocol.TerminalLimits.MaxSpoolChars));
            writer.Append(new string('b', 64));
            writer.Complete();

            Assert.True(writer.Truncated);
            Assert.Null(writer.FilePath);
            spool.Store(blockedSession, "req-1", writer);

            Assert.True(spool.TryReadPage(blockedSession, "req-1", 0, 8, out var page));
            Assert.True(page.Truncated);
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
