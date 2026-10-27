using RuAgent.Cli.Subagents;
using RuAgent.Cli.Tui;
using Terminal.Gui.ViewBase;

namespace RuAgent.Cli.TerminalGui;

/// <summary>
/// Session heading and workspace above the conversation. The live activity moved to the
/// transcript's live row. Workspace is centered above the chat; short layouts
/// use the second row when the permission/usage cluster needs the first.
/// </summary>
public sealed class TgHeaderView : View
{
    public string SessionTitle { get; private set; } = "session";
    public long TotalTokens { get; private set; }
    public double? ContextPercent { get; private set; }
    public decimal? SessionCost { get; private set; }
    public string Preset { get; private set; } = string.Empty;
    internal string WorkspaceLabel => _workspace;
    internal bool ShowSessionDetails { get; set; } = true;
    private string _workspace = string.Empty;
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

    /// <summary>Live children of the current root, painted right-aligned on the workspace row.</summary>
    public void SetSubagents(IReadOnlyList<SubagentChip> chips)
    {
        ArgumentNullException.ThrowIfNull(chips);
        if (_subagents.SequenceEqual(chips)) return;
        _subagents = chips;
        SetNeedsDraw();
    }

    public void SetWorkspace(string workspace)
    {
        var safe = TuiWorkspaceHeading.DisplayPath(workspace);
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
        var workspace = _workspace.Length == 0 && !ShowSessionDetails ? TuiWorkspaceHeading.NoProject : _workspace;
        // Preserve a useful centered path before spending space on the expanded usage meter.
        if (workspace.Length > 0 && width >= 60 && width - 2 * (rightWidth + 2) < Math.Min(24, width / 3))
        {
            parts = null;
            tokensOnly = ShowSessionDetails && TotalTokens > 0 ? TuiStatusLine.FormatTokens(TotalTokens) : string.Empty;
            usageWidth = TuiText.VisibleWidth(tokensOnly);
            presetGap = presetWidth > 0 && usageWidth > 0 ? 2 : 0;
            rightWidth = presetWidth + presetGap + usageWidth;
        }
        var centerBudget = Math.Max(0, width - 2 * (rightWidth > 0 ? rightWidth + 2 : 0));
        if (Viewport.Height >= 2 && centerBudget < Math.Min(16, TuiText.VisibleWidth(workspace))) centerBudget = 0;
        var centered = FitWorkspace(workspace, centerBudget);
        var centerX = (width - TuiText.VisibleWidth(centered)) / 2;
        var titleBudget = centered.Length > 0 ? Math.Max(0, centerX - 2)
            : Math.Max(0, width - rightWidth - (rightWidth > 0 ? 2 : 0));
        var heading = ShowSessionDetails ? TuiText.Truncate(SessionTitle, titleBudget) : string.Empty;
        SetAttribute(TgPalette.Attr(TuiToken.Bright));
        Move(0, 0);
        AddStr(heading);
        if (centered.Length > 0)
        {
            SetAttribute(TgPalette.Attr(TuiToken.Meta));
            Move(centerX, 0);
            AddStr(centered);
        }
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
            var spans = SubagentLiveTracker.Fit(_subagents, Math.Max(0, width / (centered.Length == 0 ? 3 : 2)));
            var chipWidth = spans.Sum(span => TuiText.VisibleWidth(span.Text));
            SetAttribute(TgPalette.Attr(TuiToken.Meta));
            if (centered.Length == 0 && workspace.Length > 0)
            {
                var fallback = FitWorkspace(workspace, Math.Max(0, width - 2 * (chipWidth > 0 ? chipWidth + 2 : 0)));
                Move((width - TuiText.VisibleWidth(fallback)) / 2, 1);
                AddStr(fallback);
            }
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

    internal static string FitWorkspace(string text, int width) => width <= 0 ? string.Empty
        : width == 1 ? TuiText.Truncate(text, 1) : TuiText.MiddleTruncate(text, width);

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
