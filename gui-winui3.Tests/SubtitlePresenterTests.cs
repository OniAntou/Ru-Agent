// gui-winui3.Tests/SubtitlePresenterTests.cs
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SubtitlePresenterTests
{
    private sealed class Sink
    {
        public List<SubtitleSnapshot> Snapshots { get; } = new();
        public void OnSnapshot(SubtitleSnapshot s) => Snapshots.Add(s);
    }

    [Fact]
    public void BeginSegment_EmitsFullTextImmediately()
    {
        var sink = new Sink();
        using var presenter = new SubtitlePresenter();
        presenter.SnapshotChanged += sink.OnSnapshot;
        presenter.BeginSegment("em là Ru", 900);

        Assert.Single(sink.Snapshots);
        Assert.Equal("em là Ru", sink.Snapshots[0].Text);
        Assert.Equal("em là Ru", presenter.Current.Text);
    }

    [Fact]
    public void CancelAll_ClearsTextAndEmitsEmpty()
    {
        var sink = new Sink();
        using var presenter = new SubtitlePresenter();
        presenter.SnapshotChanged += sink.OnSnapshot;
        presenter.BeginSegment("a b c", 900);
        presenter.CancelAll();

        Assert.Equal(SubtitleSnapshot.Empty, presenter.Current);
        Assert.Equal(2, sink.Snapshots.Count);
        Assert.Equal(SubtitleSnapshot.Empty, sink.Snapshots[^1]);
    }
}
