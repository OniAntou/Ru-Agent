using System;
using System.IO;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class TranscriptFontSizeTests
{
    [Theory]
    [InlineData(13, 14)]
    [InlineData(14, 14)]
    [InlineData(15.5, 15.5)]
    [InlineData(17, 17)]
    [InlineData(99, 17)]
    public void Store_clamps_to_14_through_17(double requested, double expected)
    {
        var store = new TranscriptFontSizeStore(TempPath());

        Assert.True(store.SetSize(requested));
        Assert.Equal(expected, store.GetSize());
    }

    [Fact]
    public void Store_defaults_to_14_when_missing_or_corrupt()
    {
        Assert.Equal(14, new TranscriptFontSizeStore(TempPath()).GetSize());

        var corrupt = TempPath();
        File.WriteAllText(corrupt, "{not json");
        Assert.Equal(14, new TranscriptFontSizeStore(corrupt).GetSize());
    }

    [Fact]
    public void Typography_activity_line_runs_one_step_smaller()
    {
        var typography = new TranscriptTypography(new TranscriptFontSizeStore(TempPath()));

        Assert.Equal(14, typography.MessageFontSize);
        Assert.Equal(13, typography.ActivityFontSize);

        typography.MessageFontSize = 17;
        Assert.Equal(17, typography.MessageFontSize);
        Assert.Equal(16, typography.ActivityFontSize);
    }

    [Fact]
    public void Typography_notifies_both_sizes_and_persists()
    {
        var path = TempPath();
        var typography = new TranscriptTypography(new TranscriptFontSizeStore(path));
        var notified = new System.Collections.Generic.List<string?>();
        typography.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        typography.MessageFontSize = 16;

        Assert.Contains("MessageFontSize", notified);
        Assert.Contains("ActivityFontSize", notified);
        Assert.Equal(16, new TranscriptFontSizeStore(path).GetSize());
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "ru-transcript-tests", Guid.NewGuid().ToString("N") + ".json");
}
