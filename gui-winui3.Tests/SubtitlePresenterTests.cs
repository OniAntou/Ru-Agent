// gui-winui3.Tests/SubtitlePresenterTests.cs
using System;
using System.Collections.Generic;
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
    public void SegmentsFlowFromFirstToLastToken()
    {
        var sink = new Sink();
        using var presenter = new SubtitlePresenter();
        presenter.SnapshotChanged += sink.OnSnapshot;
        presenter.BeginSegment("em là Ru", 900);

        presenter.Tick(0);
        presenter.Tick(300);
        presenter.Tick(700);
        presenter.Tick(900);

        Assert.True(sink.Snapshots[0].HighlightToken == "em");
        Assert.True(sink.Snapshots[1].HighlightToken == "là");
        Assert.True(sink.Snapshots[^1].HighlightToken == "Ru");
        Assert.True(presenter.IsSpeaking);
    }

    [Fact]
    public void CancelAll_StopsAndClears()
    {
        var sink = new Sink();
        using var presenter = new SubtitlePresenter();
        presenter.SnapshotChanged += sink.OnSnapshot;
        presenter.BeginSegment("a b c", 900);
        presenter.CancelAll();
        Assert.Equal(SubtitleSnapshot.Empty, presenter.Current);
        Assert.Single(sink.Snapshots);
        Assert.Equal(SubtitleSnapshot.Empty, sink.Snapshots[^1]);
    }
}