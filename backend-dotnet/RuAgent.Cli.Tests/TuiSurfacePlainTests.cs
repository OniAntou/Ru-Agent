using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiSurfacePlainTests
{
    [Fact]
    public void RowsAndFragmentsKeepTheirOutputWithoutScreenControl()
    {
        using var writer = new StringWriter();
        var surface = new TuiSurface(writer);
        surface.Write("partial"); surface.Line();
        surface.WriteLine("a\nb\r\nc");
        surface.WriteBlock(["one", "two"]);
        surface.Flush();
        Assert.Equal(string.Join(Environment.NewLine, "partial", "a", "b", "c", "one", "two", ""), writer.ToString());
        Assert.DoesNotContain('\u001b', writer.ToString());
    }

    [Fact]
    public void PartialWritesAreNotDelayedInASecondHistoryBuffer()
    {
        using var writer = new StringWriter();
        var surface = new TuiSurface(writer);
        surface.Write("first");
        Assert.Equal("first", writer.ToString());
        surface.Write(" second"); surface.Line();
        Assert.Equal("first second" + Environment.NewLine, writer.ToString());
    }

    [Fact]
    public async Task IndependentSessionSinksDoNotShareAProcessWideWriteLock()
    {
        using var blocked = new BlockingWriter();
        using var normal = new StringWriter();
        var first = new TuiSurface(blocked); var second = new TuiSurface(normal);
        var pending = Task.Run(() => first.WriteLine("slow sink"));
        try
        {
            await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(() => second.WriteLine("other session")).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("other session" + Environment.NewLine, normal.ToString());
        }
        finally { blocked.Release.Set(); await pending; }
    }

    private sealed class BlockingWriter : StringWriter
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new(false);
        public override void WriteLine(string? value)
        {
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("test sink was not released");
            base.WriteLine(value);
        }
        protected override void Dispose(bool disposing) { Release.Dispose(); base.Dispose(disposing); }
    }
}
