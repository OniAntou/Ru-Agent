using System.Globalization;
using RuAgent.Cli.Tui;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace RuAgent.Cli.TerminalGui;

/// <summary>One-line header: <c># title</c> flush left, usage cluster flush right.</summary>
public sealed class TgHeaderView : View
{
    private readonly Label _title;
    private readonly Label _usage;

    public TgHeaderView()
    {
        Height = 1;
        SetScheme(TgPalette.Scheme(TuiToken.Meta));
        _title = new Label { X = 0, Y = 0, Text = "#" };
        _title.SetScheme(TgPalette.Scheme(TuiToken.Bright));
        _usage = new Label { X = 0, Y = 0 };
        Add(_title, _usage);
    }

    public void SetTitle(string title) => _title.Text = "# " + title;

    public void UpdateUsage(long totalTokens, int? percent, decimal? cost)
    {
        var usage = ComposeUsage(totalTokens, percent, cost);
        _usage.Text = usage;
        // Same right edge as TuiHeader's TwoColumn: the cluster ends on the last column.
        _usage.X = Pos.AnchorEnd(TuiText.VisibleWidth(usage));
        SetNeedsDraw();
    }

    /// <summary>
    /// The classic <see cref="TuiHeader.Compose"/> tally: grouped tokens only when positive,
    /// the context percent only when known, the cost only when priced, joined two spaces apart.
    /// </summary>
    internal static string ComposeUsage(long totalTokens, int? percent, decimal? cost)
    {
        var parts = new List<string>(3);
        if (totalTokens > 0)
        {
            parts.Add(totalTokens.ToString("N0", CultureInfo.InvariantCulture));
        }

        if (percent is { } knownPercent)
        {
            parts.Add($"{knownPercent}%");
        }

        if (cost is { } value)
        {
            parts.Add($"(${value.ToString("0.00", CultureInfo.InvariantCulture)})");
        }

        return string.Join("  ", parts);
    }
}
