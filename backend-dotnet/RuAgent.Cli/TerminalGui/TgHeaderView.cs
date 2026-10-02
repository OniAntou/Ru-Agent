using System.Globalization;
using RuAgent.Cli.Tui;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace RuAgent.Cli.TerminalGui;

/// <summary>One-line header: <c># title</c> left, usage cluster right.</summary>
public sealed class TgHeaderView : View
{
    private readonly Label _title;
    private readonly Label _usage;

    public TgHeaderView()
    {
        Height = 1;
        SetScheme(TgPalette.Scheme(TuiToken.Meta));
        _title = new Label { X = 0, Y = 0, Text = " #" };
        _title.SetScheme(TgPalette.Scheme(TuiToken.Bright));
        _usage = new Label { X = 0, Y = 0 };
        Add(_title, _usage);
    }

    public void SetTitle(string title) => _title.Text = " # " + title;

    public void UpdateUsage(long totalTokens, int percent, decimal? cost)
    {
        // InvariantCulture matches TuiHeader.Compose so both engines pin the same copy.
        var usage = totalTokens.ToString("N0", CultureInfo.InvariantCulture) + $"  {percent}%";
        if (cost is { } value)
        {
            usage += $"  (${value.ToString("0.00", CultureInfo.InvariantCulture)})";
        }

        _usage.Text = usage;
        // Right-aligned: recompute the anchor against the live width.
        _usage.X = Math.Max(0, (App?.Screen.Width ?? 80) - usage.Length - 2);
        SetNeedsDraw();
    }
}
