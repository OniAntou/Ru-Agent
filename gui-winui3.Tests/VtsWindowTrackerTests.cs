using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class VtsWindowTrackerTests
{
    private sealed class FakeLocator : IVtsWindowLocator
    {
        public RectPx? Rect { get; set; }
        public VtsWindowInfo? GetCurrent() =>
            Rect is null ? null : new VtsWindowInfo(Rect.Value, 96);
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
}