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
    private TuiActivityPresentation? _activityPresentation;

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
        if (_activity == safe && _activityPresentation is null) return;
        _activity = safe;
        _activityPresentation = null;
        SetNeedsDraw();
    }

    public void SetActivity(TuiActivityPresentation activity)
    {
        if (_activityPresentation == activity) return;
        _activityPresentation = activity;
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
            AddStr(TuiText.Truncate(_workspace, width));
            Move(0, 2);
            var spans = _activityPresentation?.Fit(width)
                ?? new[] { new TuiActivitySpan(TuiText.Truncate(_activity, width), TuiToken.Meta) };
            foreach (var span in spans)
            {
                SetAttribute(TgPalette.Attr(span.Tone));
                AddStr(span.Text);
            }
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
