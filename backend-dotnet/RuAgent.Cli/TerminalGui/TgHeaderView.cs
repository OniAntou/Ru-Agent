using RuAgent.Cli.Tui;
using Terminal.Gui.ViewBase;

namespace RuAgent.Cli.TerminalGui;

/// <summary>
/// Session heading and workspace above the conversation. The live activity moved to the
/// transcript's live row, so the header stays two rows: title with the
/// usage cluster, then the workspace.
/// </summary>
public sealed class TgHeaderView : View
{
    public string SessionTitle { get; private set; } = "session";
    public long TotalTokens { get; private set; }
    public double? ContextPercent { get; private set; }
    public decimal? SessionCost { get; private set; }
    private string _workspace = string.Empty;

    public TgHeaderView()
    {
        Height = 1;
        SetScheme(TgPalette.Scheme(TuiToken.Meta));
    }

    public void SetTitle(string title)
    {
        SessionTitle = string.IsNullOrWhiteSpace(title) ? "session" : ChatDisplayText.Sanitize(title, 256);
        SetNeedsDraw();
    }

    public void UpdateUsage(long totalTokens, double? percent, decimal? cost)
    {
        TotalTokens = totalTokens;
        ContextPercent = percent;
        SessionCost = cost;
        SetNeedsDraw();
    }

    public void SetWorkspace(string workspace)
    {
        var safe = ChatDisplayText.Sanitize(workspace, 512);
        if (_workspace == safe) return;
        _workspace = safe;
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        if (width <= 0) return true;
        IReadOnlyList<TuiUsagePart>? parts = null;
        var tokensOnly = string.Empty;
        if (width >= 60)
        {
            parts = TuiUsageMeter.Compose(TotalTokens, ContextPercent, SessionCost);
            if (parts.Count == 0) parts = null;
        }
        else if (width >= 32 && TotalTokens > 0)
        {
            tokensOnly = TuiStatusLine.FormatTokens(TotalTokens);
        }

        var usageWidth = parts is not null ? TuiUsageMeter.VisibleWidth(parts) : TuiText.VisibleWidth(tokensOnly);
        var heading = TuiText.Truncate(SessionTitle, Math.Max(0, width - usageWidth - (usageWidth > 0 ? 2 : 0)));
        SetAttribute(TgPalette.Attr(TuiToken.Bright));
        Move(0, 0);
        AddStr(heading);
        if (parts is not null)
        {
            DrawUsageParts(parts, Math.Max(0, width - usageWidth));
        }
        else if (usageWidth > 0)
        {
            SetAttribute(TgPalette.Attr(TuiToken.Meta));
            Move(Math.Max(0, width - usageWidth), 0);
            AddStr(tokensOnly);
        }
        if (Viewport.Height >= 2)
        {
            SetAttribute(TgPalette.Attr(TuiToken.Meta));
            Move(0, 1);
            AddStr(TuiText.Truncate(_workspace, width));
        }
        return true;
    }

    /// <summary>
    /// Paints the usage cluster at <paramref name="x"/>. The meter's fill and
    /// track are cells with their own background: cream fill on the dark track
    /// surface, the same shape the classic rows keep as blank cells.
    /// </summary>
    private void DrawUsageParts(IReadOnlyList<TuiUsagePart> parts, int x)
    {
        var cursor = x;
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0)
            {
                cursor += TuiUsageMeter.SeparatorAfter(parts[i - 1]);
            }

            var part = parts[i];
            if (part.IsMeter)
            {
                Move(cursor, 0);
                SetAttribute(TgPalette.Attr(part.FillTone, TuiPalette.Rgb(part.FillTone)));
                AddStr(new string(' ', part.FillCells));
                if (part.FillCells < TuiUsageMeter.BarWidth)
                {
                    SetAttribute(TgPalette.Attr(TuiToken.Meta, TuiPalette.ElementRgb()));
                    AddStr(new string(' ', TuiUsageMeter.BarWidth - part.FillCells));
                }

                cursor += TuiUsageMeter.BarWidth;
            }
            else
            {
                SetAttribute(TgPalette.Attr(part.Tone));
                Move(cursor, 0);
                AddStr(part.Text);
                cursor += TuiText.VisibleWidth(part.Text);
            }
        }
    }
}
