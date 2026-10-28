using RuAgent.Updates;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class UpdateCheckPolicyTests
{
    [Theory]
    [InlineData(1, UpdateChannel.Preview)]
    [InlineData(0, UpdateChannel.Stable)]
    [InlineData(-1, UpdateChannel.Stable)]
    [InlineData(2, UpdateChannel.Stable)]
    public void Selection_maps_only_preview_index_to_preview_channel(int selectedIndex, UpdateChannel expected)
    {
        Assert.Equal(expected, UpdateAvailabilityMonitor.ChannelFromSelection(selectedIndex));
    }
}
