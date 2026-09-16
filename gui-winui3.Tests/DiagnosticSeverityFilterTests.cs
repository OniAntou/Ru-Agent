using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The diagnostics severity picker: what it offers, and what a selection filters on.
/// The entries and the filter mapping share one source so a fourth level cannot be
/// added to the list but forgotten in the filter.
/// </summary>
public sealed class DiagnosticSeverityFilterTests
{
    [Fact]
    public void The_picker_offers_any_level_first_then_every_level()
    {
        Assert.Equal("All Levels", DiagnosticSeverityFilter.PickerLabels[0]);
        Assert.Equal(
            DiagnosticSeverityFilter.Levels,
            DiagnosticSeverityFilter.PickerLabels.Skip(1));
    }

    [Fact]
    public void Every_offered_label_round_trips_through_the_filter()
    {
        // The property that matters: picking a level filters on that level.
        for (var index = DiagnosticSeverityFilter.FirstLevelIndex;
             index < DiagnosticSeverityFilter.PickerLabels.Count;
             index++)
        {
            string? level = DiagnosticSeverityFilter.ForSelectedIndex(index);

            Assert.NotNull(level);
            Assert.Equal(index, DiagnosticSeverityFilter.IndexOf(level));
        }
    }

    [Fact]
    public void The_first_entry_filters_on_nothing()
    {
        Assert.Null(DiagnosticSeverityFilter.ForSelectedIndex(0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    public void An_out_of_range_index_filters_on_nothing_instead_of_throwing(int index)
    {
        // The picker can report -1 before it is populated, and that must not crash the
        // first refresh.
        Assert.Null(DiagnosticSeverityFilter.ForSelectedIndex(index));
    }

    [Theory]
    [InlineData("info", 1)]
    [InlineData("warning", 2)]
    [InlineData("error", 3)]
    [InlineData("ERROR", 3)]
    [InlineData("  error  ", 3)]
    public void A_level_name_selects_its_entry(string level, int expected)
    {
        Assert.Equal(expected, DiagnosticSeverityFilter.IndexOf(level));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("critical")]
    public void An_unrecognised_level_falls_back_to_any_level(string? level)
    {
        // Falling back to "no filter" is the safe direction: an unknown level shown as
        // "filtered" but returning nothing would look like data loss.
        Assert.Equal(0, DiagnosticSeverityFilter.IndexOf(level));
    }

    [Fact]
    public void The_levels_are_distinct_and_lowercase()
    {
        // The filter compares against what the API returns, which is lowercase.
        Assert.Equal(DiagnosticSeverityFilter.Levels.Count, DiagnosticSeverityFilter.Levels.Distinct().Count());
        Assert.All(
            DiagnosticSeverityFilter.Levels,
            level => Assert.Equal(level.ToLowerInvariant(), level));
    }
}
