using System.Globalization;
using RuAgent.Cli.Tui;
using Terminal.Gui.ViewBase;

namespace RuAgent.Cli.TerminalGui;

/// <summary>Session heading, workspace and live activity above the conversation.</summary>
public sealed class TgHeaderView : View
{
    public string SessionTitle { get; private set; } = "session";
    public long TotalTokens { get; private set; }
    public int? ContextPercent { get; private set; }
    public decimal? SessionCost { get; private set; }
    private string _workspace = string.Empty;
    private string _activity = string.Empty;

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

    public void UpdateUsage(long totalTokens, int? percent, decimal? cost)
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

    public void SetActivity(string activity)
    {
        var safe = ChatDisplayText.Sanitize(TuiText.Strip(activity).Trim(), 512);
        if (_activity == safe) return;
        _activity = safe;
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        if (width <= 0) return true;
        var usage = width >= 60 ? ComposeUsage(TotalTokens, ContextPercent, SessionCost)
            : width >= 32 && TotalTokens > 0 ? TuiStatusLine.FormatTokens(TotalTokens) : string.Empty;
        var heading = TuiText.Truncate("# " + SessionTitle, Math.Max(0, width - TuiText.VisibleWidth(usage) - (usage.Length > 0 ? 2 : 0)));
        SetAttribute(TgPalette.Attr(TuiToken.Bright));
        Move(0, 0);
        AddStr(heading);
        SetAttribute(TgPalette.Attr(TuiToken.Brand));
        Move(0, 0);
        AddStr("#");
        if (usage.Length > 0)
        {
            SetAttribute(TgPalette.Attr(TuiToken.Meta));
            Move(Math.Max(0, width - TuiText.VisibleWidth(usage)), 0);
            AddStr(usage);
        }
        if (Viewport.Height >= 3)
        {
            SetAttribute(TgPalette.Attr(TuiToken.Meta));
            Move(0, 1);
            var activity = TuiText.Truncate(_activity, Math.Max(0, width / 2));
            var activityWidth = TuiText.VisibleWidth(activity);
            var workspaceWidth = Math.Max(0, width - activityWidth - (activityWidth > 0 ? 3 : 0));
            AddStr(TuiText.Truncate(_workspace, workspaceWidth));
            if (activityWidth > 0)
            {
                var idle = _activity.Contains("idle", StringComparison.OrdinalIgnoreCase);
                SetAttribute(TgPalette.Attr(idle ? TuiToken.Meta : TuiToken.Brand));
                Move(width - activityWidth, 1);
                AddStr(activity);
            }
            SetAttribute(TgPalette.Attr(TuiToken.Line));
            Move(0, 2);
            AddStr(new string('─', width));
        }
        return true;
    }

    internal static string ComposeUsage(long totalTokens, int? percent, decimal? cost)
    {
        var parts = new List<string>(3);
        if (totalTokens > 0) parts.Add(totalTokens.ToString("N0", CultureInfo.InvariantCulture));
        if (percent is { } knownPercent) parts.Add($"{knownPercent}%");
        if (cost is { } value) parts.Add($"(${value.ToString("0.00", CultureInfo.InvariantCulture)})");
        return string.Join("  ", parts);
    }
}
