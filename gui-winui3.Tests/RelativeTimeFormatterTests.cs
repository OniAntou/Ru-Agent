using System;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class RelativeTimeFormatterTests
{
    [Theory]
    [InlineData("2026-09-17T00:59:00Z", "2026-09-17T01:00:00Z", "just now")]
    [InlineData("2026-09-17T00:00:00Z", "2026-09-17T01:00:00Z", "1h")]
    [InlineData("2026-09-16T17:00:00Z", "2026-09-17T01:00:00Z", "8h")]
    [InlineData("2026-09-14T01:00:00Z", "2026-09-17T01:00:00Z", "3d")]
    [InlineData("2026-07-17T01:00:00Z", "2026-09-17T01:00:00Z", "2mo")]
    public void Formats_relative_to_now(string updatedAt, string now, string expected)
    {
        Assert.Equal(expected, RelativeTimeFormatter.Format(updatedAt, DateTimeOffset.Parse(now)));
    }

    [Theory]
    [InlineData("", "fallback")]
    [InlineData("not-a-date", "fallback")]
    public void Unparseable_input_returns_fallback(string updatedAt, string fallback)
    {
        Assert.Equal(fallback, RelativeTimeFormatter.Format(updatedAt, DateTimeOffset.UtcNow, fallback));
    }
}
