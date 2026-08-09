using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class VtsWindowTrackerTests
{
    private sealed class FakeNative : IVtsWindowNative
    {
        public IntPtr TitleHandle { get; set; }
        public IntPtr ProcessHandle { get; set; }
        public VtsWindowInfo? Info { get; set; }
        public int TitleLookups { get; private set; }
        public int ProcessLookups { get; private set; }
        public int InfoLookups { get; private set; }

        public IntPtr FindWindowByTitle(string title)
        {
            TitleLookups++;
            return TitleHandle;
        }

        public IntPtr FindWindowByProcessNames(params string[] processNames)
        {
            ProcessLookups++;
            return ProcessHandle;
        }

        public bool TryGetWindowInfo(IntPtr handle, out VtsWindowInfo info)
        {
            InfoLookups++;
            if (Info is null || handle == IntPtr.Zero)
            {
                info = default;
                return false;
            }
            info = Info.Value;
            return true;
        }
    }

    private sealed class FakeLocator : IVtsWindowLocator
    {
        public RectPx? Rect { get; set; }
        public VtsWindowInfo? GetCurrent() =>
            Rect is null ? null : new VtsWindowInfo(Rect.Value, Rect.Value, 96);
    }

    [Fact]
    public void ReportsHidden_WhenNoWindow()
    {
        var locator = new FakeLocator { Rect = null };
        var tracker = new VtsWindowTracker(locator);
        Assert.False(tracker.GetCurrent().Visible);
    }

    [Fact]
    public void ReportsRect_WhenVisible()
    {
        var locator = new FakeLocator { Rect = new RectPx(10, 20, 300, 400) };
        var tracker = new VtsWindowTracker(locator);
        var current = tracker.GetCurrent();
        Assert.True(current.Visible);
        Assert.Equal(10, current.Rect.X);
    }

    [Fact]
    public void LocatorFallsBackToProcessAndCachesTheWorkingWindow()
    {
        var rect = new RectPx(100, 200, 640, 720);
        var native = new FakeNative
        {
            TitleHandle = IntPtr.Zero,
            ProcessHandle = new IntPtr(42),
            Info = new VtsWindowInfo(rect, new RectPx(0, 0, 1920, 1040), 144),
        };
        var locator = new VtsWindowLocator(native);

        VtsWindowInfo? first = locator.GetCurrent();
        VtsWindowInfo? second = locator.GetCurrent();

        Assert.Equal(rect, first?.Rect);
        Assert.Equal(rect, second?.Rect);
        Assert.Equal(1, native.TitleLookups);
        Assert.Equal(1, native.ProcessLookups);
        Assert.Equal(2, native.InfoLookups);
    }

    [Fact]
    public void LocatorThrottlesProcessFallbackWhileVtsIsMissing()
    {
        DateTimeOffset now = new(2026, 8, 9, 10, 0, 0, TimeSpan.Zero);
        var native = new FakeNative();
        var locator = new VtsWindowLocator(
            native,
            utcNow: () => now,
            processRetryDelay: TimeSpan.FromSeconds(2));

        Assert.Null(locator.GetCurrent());
        Assert.Null(locator.GetCurrent());
        Assert.Equal(1, native.ProcessLookups);

        now += TimeSpan.FromSeconds(2);
        Assert.Null(locator.GetCurrent());

        Assert.Equal(2, native.ProcessLookups);
        Assert.Equal(3, native.TitleLookups);
    }
}
