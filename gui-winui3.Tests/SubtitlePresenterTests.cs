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
        presenter.BeginSegment("r1", "em là Ru");

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
        presenter.BeginSegment("r1", "a b c");
        presenter.CancelAll();

        Assert.Equal(SubtitleSnapshot.Empty, presenter.Current);
        Assert.Equal(2, sink.Snapshots.Count);
        Assert.Equal(SubtitleSnapshot.Empty, sink.Snapshots[^1]);
    }

    [Fact]
    public void EndingStaleSegmentDoesNotClearNewerSubtitle()
    {
        using var presenter = new SubtitlePresenter();
        presenter.BeginSegment("r1", "First sentence");
        presenter.BeginSegment("r2", "Second sentence");

        presenter.EndSegment("r1");

        Assert.Equal("Second sentence", presenter.Current.Text);

        presenter.EndSegment("r2");

        Assert.Equal(SubtitleSnapshot.Empty, presenter.Current);
    }
}
