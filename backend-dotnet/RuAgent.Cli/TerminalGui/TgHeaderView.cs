using RuAgent.Cli.Subagents;
using RuAgent.Cli.Tui;
using Terminal.Gui.ViewBase;

namespace RuAgent.Cli.TerminalGui;

/// <summary>
/// Session title, permission, usage and live subagents above the conversation.
/// </summary>
public sealed class TgHeaderView : View
{
    public string SessionTitle { get; private set; } = "session";
    public long TotalTokens { get; private set; }
    public double? ContextPercent { get; private set; }
    public decimal? SessionCost { get; private set; }
    public string Preset { get; private set; } = string.Empty;
    internal bool ShowSessionDetails { get; set; } = true;
    private IReadOnlyList<SubagentChip> _subagents = [];

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

    public void SetPreset(string preset)
    {
        var safe = ChatDisplayText.Sanitize(preset, 32);
        if (Preset == safe) return;
        Preset = safe;
        SetNeedsDraw();
    }

    public void UpdateUsage(long totalTokens, double? percent, decimal? cost)
    {
        TotalTokens = totalTokens;
        ContextPercent = percent;
        SessionCost = cost;
        SetNeedsDraw();
    }

    /// <summary>Live children of the current root, painted right-aligned on the second header row.</summary>
    public void SetSubagents(IReadOnlyList<SubagentChip> chips)
    {
        ArgumentNullException.ThrowIfNull(chips);
        if (_subagents.SequenceEqual(chips)) return;
        _subagents = chips;
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        if (width <= 0) return true;
        IReadOnlyList<TuiUsagePart>? parts = null;
        var tokensOnly = string.Empty;
        if (ShowSessionDetails && width >= 60)
        {
            parts = TuiUsageMeter.Compose(TotalTokens, ContextPercent, SessionCost);
            if (parts.Count == 0) parts = null;
        }
        else if (ShowSessionDetails && width >= 32 && TotalTokens > 0)
        {
            tokensOnly = TuiStatusLine.FormatTokens(TotalTokens);
        }

        var usageWidth = parts is not null ? TuiUsageMeter.VisibleWidth(parts) : TuiText.VisibleWidth(tokensOnly);
        var preset = ShowSessionDetails ? TuiText.Truncate(Preset, Math.Min(12, width)) : string.Empty;
        var presetWidth = TuiText.VisibleWidth(preset);
        var presetGap = presetWidth > 0 && usageWidth > 0 ? 2 : 0;
        if (usageWidth + presetWidth + presetGap > width)
        {
            parts = null;
            tokensOnly = width >= 32 && TotalTokens > 0
                ? TuiText.Truncate(TuiStatusLine.FormatTokens(TotalTokens), Math.Max(0, width - presetWidth - presetGap)) : string.Empty;
            usageWidth = TuiText.VisibleWidth(tokensOnly);
            presetGap = presetWidth > 0 && usageWidth > 0 ? 2 : 0;
        }
        var rightWidth = presetWidth + presetGap + usageWidth;
        var titleBudget = Math.Max(0, width - rightWidth - (rightWidth > 0 ? 2 : 0));
        var heading = ShowSessionDetails ? TuiText.Truncate(SessionTitle, titleBudget) : string.Empty;
        SetAttribute(TgPalette.Attr(TuiToken.Bright));
        Move(0, 0);
        AddStr(heading);
        if (presetWidth > 0)
        {
            SetAttribute(TgPalette.Attr(TuiToken.Ok));
            Move(Math.Max(0, width - rightWidth), 0);
            AddStr(preset);
        }
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
        if (Viewport.Height >= 2 && ShowSessionDetails)
        {
            var spans = SubagentLiveTracker.Fit(_subagents, Math.Max(0, width / 3));
            var chipWidth = spans.Sum(span => TuiText.VisibleWidth(span.Text));
            SetAttribute(TgPalette.Attr(TuiToken.Meta));
            var cursor = width - chipWidth;
            foreach (var span in spans)
            {
                SetAttribute(TgPalette.Attr(span.Tone));
                Move(cursor, 1);
                AddStr(span.Text);
                cursor += TuiText.VisibleWidth(span.Text);
            }
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
