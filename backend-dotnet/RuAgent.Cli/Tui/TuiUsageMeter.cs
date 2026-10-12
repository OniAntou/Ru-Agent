using System.Globalization;

namespace RuAgent.Cli.Tui;

/// <summary>
/// One piece of the session header's usage cluster: a painted text run, or the
/// context meter (a fill of <see cref="TuiUsageMeter.BarWidth"/> cells whose
/// track stays behind it). The classic string builder renders the meter as
/// blank cells; the Terminal.Gui view paints fill and track.
/// </summary>
public sealed record TuiUsagePart
{
    public TuiUsagePart(string text, TuiToken tone)
    {
        Text = text;
        Tone = tone;
        IsMeter = false;
        FillCells = 0;
        FillTone = tone;
    }

    private TuiUsagePart(int fillCells, TuiToken fillTone)
    {
        Text = string.Empty;
        Tone = TuiToken.Meta;
        IsMeter = true;
        FillCells = fillCells;
        FillTone = fillTone;
    }

    /// <summary>The meter piece: <paramref name="fillCells"/> lit cells on the track.</summary>
    public static TuiUsagePart Meter(int fillCells, TuiToken fillTone) => new(fillCells, fillTone);

    public string Text { get; }

    public TuiToken Tone { get; }

    public bool IsMeter { get; }

    /// <summary>Lit cells of the meter, 0 to <see cref="TuiUsageMeter.BarWidth"/>.</summary>
    public int FillCells { get; }

    public TuiToken FillTone { get; }
}

/// <summary>
/// The context meter and its cluster: grouped token count, the
/// <see cref="BarWidth"/>-cell meter with a two-decimal percentage, and the
/// session cost. One composition feeds both engines so the classic row and the
/// Terminal.Gui header cannot drift.
/// </summary>
public static class TuiUsageMeter
{
    /// <summary>Meter cells. The fill rounds up and keeps at least one lit cell.</summary>
    public const int BarWidth = 8;

    /// <summary>Lit cells at or above this percentage take the warning tone.</summary>
    public const double WarnPercent = 70;

    /// <summary>Lit cells at or above this percentage take the error tone.</summary>
    public const double CriticalPercent = 90;

    /// <summary>The cluster pieces in display order, omitting unpublished members.</summary>
    public static IReadOnlyList<TuiUsagePart> Compose(long? tokens, double? percent, decimal? cost)
    {
        var parts = new List<TuiUsagePart>(4);
        if (tokens is > 0)
        {
            parts.Add(new TuiUsagePart(tokens.Value.ToString("N0", CultureInfo.InvariantCulture), TuiToken.Meta));
        }

        if (percent is { } value)
        {
            parts.Add(TuiUsagePart.Meter(FillCells(value), FillTone(value)));
            parts.Add(new TuiUsagePart(FormatPercent(value), TuiToken.Meta));
        }

        if (cost is { } priced)
        {
            parts.Add(new TuiUsagePart($"(${priced.ToString("0.00", CultureInfo.InvariantCulture)})", TuiToken.Meta));
        }

        return parts;
    }

    /// <summary>
    /// Lit cells for a percentage: rounded up so any occupancy shows, and at
    /// least one cell for a positive reading (the sample look keeps a lit
    /// block at 4.32% rather than an empty track).
    /// </summary>
    public static int FillCells(double percent) =>
        percent <= 0 ? 0 : Math.Clamp((int)Math.Ceiling(percent / 100d * BarWidth), 1, BarWidth);

    /// <summary>Tone of the lit cells: cream, then warn, then error as the window fills.</summary>
    public static TuiToken FillTone(double percent) =>
        percent >= CriticalPercent ? TuiToken.Err
        : percent >= WarnPercent ? TuiToken.Warn
        : TuiToken.Bright;

    /// <summary>The percentage with the two-decimal precision the meter promises.</summary>
    public static string FormatPercent(double percent) =>
        percent.ToString("0.00", CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// Cells between two pieces. The percentage sits one cell from its own
    /// meter so the two read as one unit; the remaining clusters take two.
    /// </summary>
    public static int SeparatorAfter(TuiUsagePart part) => part.IsMeter ? 1 : 2;

    /// <summary>Visible width of the whole cluster, separators included.</summary>
    public static int VisibleWidth(IReadOnlyList<TuiUsagePart> parts)
    {
        if (parts is null || parts.Count == 0)
        {
            return 0;
        }

        var width = 0;
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0)
            {
                width += SeparatorAfter(parts[i - 1]);
            }

            width += parts[i].IsMeter ? BarWidth : TuiText.VisibleWidth(parts[i].Text);
        }

        return width;
    }
}
