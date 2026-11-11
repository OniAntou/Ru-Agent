using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SettingsSearchTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_query_matches_everything(string? query)
    {
        Assert.Empty(SettingsSearch.Words(query));
        Assert.True(SettingsSearch.Matches(query, "Notifications"));
        Assert.True(SettingsSearch.Matches(query, null));
    }

    [Theory]
    [InlineData("theme", "Appearance. Light, Dark, or Meteor Shower theme", true)]
    [InlineData("THEME", "Appearance. Light, Dark, or Meteor Shower theme", true)]
    [InlineData("dark meteor", "Light, Dark, or Meteor Shower", true)]
    [InlineData("meteor dark", "Light, Dark, or Meteor Shower", true)]
    [InlineData("  sound  ", "Play a sound", true)]
    [InlineData("sound taskbar", "Play a sound", false)]
    [InlineData("proxy", "Appearance and notifications", false)]
    public void Every_word_must_appear_in_any_order_ignoring_case(string query, string text, bool expected) =>
        Assert.Equal(expected, SettingsSearch.Matches(query, text));

    [Fact]
    public void A_query_never_matches_missing_text()
    {
        Assert.False(SettingsSearch.Matches("theme", null));
        Assert.False(SettingsSearch.Matches("theme", ""));
    }

    [Fact]
    public void Words_are_split_on_any_whitespace()
    {
        Assert.Equal(["full", "access"], SettingsSearch.Words(" full \t access\n"));
    }
}
