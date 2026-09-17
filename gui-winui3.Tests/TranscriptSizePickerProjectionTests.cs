using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The conversation text size picker's rows and labels.
/// </summary>
/// <remarks>
/// The picker builds its rows from the store's bounds at runtime rather than
/// declaring them in markup, so the two cannot disagree about the range. These
/// assert the projection that decision rests on, and the seam into the view.
/// </remarks>
public sealed class TranscriptSizePickerProjectionTests
{
    [Fact]
    public void The_picker_offers_every_whole_size_the_store_accepts()
    {
        var sizes = TranscriptSizePickerProjection.Sizes();

        Assert.Equal(new double[] { 14, 15, 16, 17 }, sizes);
    }

    [Fact]
    public void Every_offered_size_survives_the_store_unchanged()
    {
        // A picker that offered a size the store clamps would show one value and
        // persist another, and the reopen would move the selection.
        foreach (var size in TranscriptSizePickerProjection.Sizes())
        {
            Assert.Equal(size, TranscriptFontSizeStore.Clamp(size));
        }
    }

    [Fact]
    public void The_range_starts_at_the_store_default()
    {
        // A default outside the offered range would open the picker with nothing
        // selected.
        Assert.Equal(
            TranscriptFontSizeStore.DefaultSize,
            TranscriptSizePickerProjection.Sizes()[0]);
    }

    [Fact]
    public void Whole_sizes_are_labelled_without_a_decimal_point()
    {
        Assert.Equal("14", TranscriptSizePickerProjection.Label(14));
        Assert.Equal("17", TranscriptSizePickerProjection.Label(17));
    }

    [Fact]
    public void A_label_states_what_the_activity_line_will_be()
    {
        // The activity line is always one step smaller, and the picker is the only
        // place a user could learn that before seeing it.
        Assert.Equal("14 (activity 13)", TranscriptSizePickerProjection.LabelWithActivity(14));
        Assert.Equal("17 (activity 16)", TranscriptSizePickerProjection.LabelWithActivity(17));
    }

    [Fact]
    public void A_stored_size_outside_the_range_still_selects_a_row()
    {
        Assert.Equal(14, TranscriptSizePickerProjection.Normalize(3));
        Assert.Equal(17, TranscriptSizePickerProjection.Normalize(99));
        Assert.Equal(16, TranscriptSizePickerProjection.Normalize(16));
    }

    [Fact]
    public void Each_option_has_a_stable_automation_id()
    {
        Assert.Equal("TranscriptSizeOption14", TranscriptSizePickerProjection.OptionAutomationId(14));
        Assert.Equal("TranscriptSizeOption17", TranscriptSizePickerProjection.OptionAutomationId(17));
    }

    [Fact]
    public void Every_option_automation_id_is_distinct()
    {
        var ids = TranscriptSizePickerProjection.Sizes()
            .Select(TranscriptSizePickerProjection.OptionAutomationId)
            .ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }
}
