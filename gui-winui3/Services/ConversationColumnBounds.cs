namespace RuApp.Services;

/// <summary>
/// Where the reading column sits inside the transcript list: the one formula both the
/// message rows and the width handles use, so the handles always land on the edges the
/// rows are about to draw.
/// </summary>
/// <param name="Left">The column's left edge, in the coordinates <c>hostLeft</c> was given in.</param>
/// <param name="Width">The column's width.</param>
public readonly record struct ConversationColumnBounds(double Left, double Width)
{
    /// <summary>
    /// A row's slot is the list's inner width minus the side-panel reserve, which every
    /// message column keeps as a right margin; the column is the content width capped to
    /// that slot and centered in it.
    /// </summary>
    public static ConversationColumnBounds For(
        double hostWidth,
        double hostLeft,
        double paddingLeft,
        double paddingRight,
        double sidePanelReserve,
        double contentWidth)
    {
        var slot = SlotWidth(hostWidth, paddingLeft, paddingRight, sidePanelReserve);
        var width = Math.Min(contentWidth, slot);
        return new(hostLeft + paddingLeft + (slot - width) / 2, width);
    }

    /// <summary>The width a message column may take before the side-panel reserve.</summary>
    public static double SlotWidth(double hostWidth, double paddingLeft, double paddingRight, double sidePanelReserve) =>
        Math.Max(0, hostWidth - paddingLeft - paddingRight - sidePanelReserve);
}
