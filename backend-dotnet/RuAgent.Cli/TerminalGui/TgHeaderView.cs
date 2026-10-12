using System.Globalization;
using RuAgent.Cli.Tui;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace RuAgent.Cli.TerminalGui;

/// <summary>One-line header: <c># title</c> flush left, usage cluster flush right.</summary>
public sealed class TgHeaderView : View
{
    /// <summary>
    /// Heading before the host has named the session. The classic header substitutes "session"
    /// for a blank title, so a bare "#" here would read as a stray character on the first row
    /// rather than as a heading.
    /// </summary>
    private const string DefaultHeading = "session";

    private readonly Label _title;
    private readonly Label _usage;

    public TgHeaderView()
    {
        Height = 1;
        SetScheme(TgPalette.Scheme(TuiToken.Meta));
        _title = new Label { X = 0, Y = 0, Text = Caption(string.Empty) };
        _title.SetScheme(TgPalette.Scheme(TuiToken.Bright));
        _usage = new Label { X = 0, Y = 0 };
        Add(_title, _usage);
    }

    /// <summary>
    /// Names the session. The title arrives asynchronously from a background probe, so this
    /// asks for the repaint itself: without it the header keeps the old caption on screen until
    /// something unrelated redraws the window.
    /// </summary>
    public void SetTitle(string title)
    {
        _title.Text = Caption(title);
        SetNeedsDraw();
    }

    /// <summary><c># session</c> until the session is named, then <c># &lt;title&gt;</c>.</summary>
    private static string Caption(string title) =>
        "# " + (string.IsNullOrWhiteSpace(title) ? DefaultHeading : title.Trim());

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
