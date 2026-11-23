using RuApp.Controls;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The preview header's second line is built from pure rules, so its wording and its
/// rounding stay testable without a window.
/// </summary>
public sealed class PreviewDetailTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(812, "812 B")]
    [InlineData(2048, "2.0 KB")]
    [InlineData(20480, "20 KB")]
    [InlineData(2_097_152, "2.0 MB")]
    [InlineData(3_221_225_472, "3.0 GB")]
    public void Sizes_read_at_the_scale_of_the_file(long bytes, string expected) =>
        Assert.Equal(expected, PreviewDetail.Size(bytes));

    [Fact]
    public void A_recent_change_reads_as_distance_and_an_old_one_as_a_date()
    {
        var now = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Local);
        Assert.Equal("modified just now", PreviewDetail.Modified(now.AddSeconds(-20), now));
        Assert.Equal("modified 7 min ago", PreviewDetail.Modified(now.AddMinutes(-7), now));
        Assert.Equal("modified 3 h ago", PreviewDetail.Modified(now.AddHours(-3), now));
        Assert.Equal("modified yesterday", PreviewDetail.Modified(now.AddDays(-1.5), now));
        Assert.Equal("modified 5 d ago", PreviewDetail.Modified(now.AddDays(-5), now));
        Assert.Equal("modified 2026-04-01", PreviewDetail.Modified(new DateTime(2026, 4, 1, 9, 0, 0), now));
    }

    [Fact]
    public void A_file_ahead_of_this_machines_clock_still_reads_as_a_time()
    {
        // A restored backup can carry a stamp in the future; it must not print "-3 h ago".
        var now = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Local);
        Assert.Equal("modified just now", PreviewDetail.Modified(now.AddHours(3), now));
    }

    [Fact]
    public void Compose_keeps_the_parts_it_has_and_drops_the_rest()
    {
        Assert.Equal("Python · 343 lines · 8.0 KB", PreviewDetail.Compose("Python · 343 lines", PreviewDetail.Size(8192)));
        Assert.Equal("PDF", PreviewDetail.Compose("PDF", null, "  "));
        Assert.Equal(string.Empty, PreviewDetail.Compose());
    }

    [Theory]
    [InlineData(1, "1 line")]
    [InlineData(1204, "1,204 lines")]
    public void Line_counts_are_words_for_one_and_grouped_beyond(int count, string expected) =>
        Assert.Equal(expected, PreviewDetail.Lines(count));

    [Fact]
    public void Pages_and_dimensions_read_plainly()
    {
        Assert.Equal("1 page", PreviewDetail.Pages(1));
        Assert.Equal("12 pages", PreviewDetail.Pages(12));
        Assert.Equal("1920 × 1080", PreviewDetail.Dimensions(1920, 1080));
        Assert.Equal(string.Empty, PreviewDetail.Dimensions(0, 0));
    }
}
