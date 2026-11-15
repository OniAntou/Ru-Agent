using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ConversationColumnBoundsTests
{
    [Fact]
    public void Handles_sit_on_the_column_edges_beside_the_side_panel_reserve()
    {
        // The live layout that exposed the bug: a 1164px list, 20px padding, a 212px
        // side-panel reserve and the default 720px column. The column is centered in the
        // 912px slot left of the reserve, so its left edge is 20 + (912 - 720) / 2.
        var bounds = ConversationColumnBounds.For(
            hostWidth: 1164, hostLeft: 0, paddingLeft: 20, paddingRight: 20, sidePanelReserve: 212, contentWidth: 720);

        Assert.Equal(116, bounds.Left);
        Assert.Equal(720, bounds.Width);
    }

    [Fact]
    public void A_narrow_list_caps_the_column_to_its_slot()
    {
        var bounds = ConversationColumnBounds.For(
            hostWidth: 700, hostLeft: 10, paddingLeft: 20, paddingRight: 20, sidePanelReserve: 0, contentWidth: 920);

        Assert.Equal(660, bounds.Width);
        Assert.Equal(30, bounds.Left);
    }

    [Fact]
    public void The_slot_never_goes_negative()
    {
        Assert.Equal(0, ConversationColumnBounds.SlotWidth(hostWidth: 100, paddingLeft: 20, paddingRight: 20, sidePanelReserve: 212));
    }
}
