using System;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SessionTimeFormatterTests
{
    private static readonly TimeSpan LocalOffset = DateTimeOffset.Now.Offset;
    private static readonly DateTimeOffset Noon = new(2026, 9, 17, 12, 0, 0, LocalOffset);

    [Theory]
    [InlineData(2026, 9, 17, 11, 59, "11:59")]
    [InlineData(2026, 9, 17, 0, 3, "00:03")]
    public void Today_shows_hour_and_minute(int year, int month, int day, int hour, int minute, string expected)
    {
        var updatedAt = new DateTimeOffset(year, month, day, hour, minute, 0, LocalOffset).ToString("O");
        Assert.Equal(expected, SessionTimeFormatter.Format(updatedAt, Noon));
    }

    [Fact]
    public void This_year_shows_day_month_and_time()
    {
        var updatedAt = new DateTimeOffset(2026, 3, 5, 8, 4, 0, LocalOffset).ToString("O");
        Assert.Equal("05/03 08:04", SessionTimeFormatter.Format(updatedAt, Noon));
    }

    [Fact]
    public void Older_year_includes_year()
    {
        var updatedAt = new DateTimeOffset(2025, 12, 31, 23, 59, 0, LocalOffset).ToString("O");
        Assert.Equal("31/12/2025 23:59", SessionTimeFormatter.Format(updatedAt, Noon));
    }

    [Theory]
    [InlineData("", "fallback")]
    [InlineData("not-a-date", "fallback")]
    public void Unparseable_input_returns_fallback(string updatedAt, string fallback)
    {
        Assert.Equal(fallback, SessionTimeFormatter.Format(updatedAt, Noon, fallback));
    }
}
