namespace RuAgent.Cli.Tui;

/// <summary>
/// Vertical geometry of the three-region full-screen layout: a fixed one-row header, the
/// transcript in the middle and a fixed three-row footer. Pure arithmetic; painting is the
/// caller's job.
/// </summary>
/// <remarks>
/// A terminal shorter than the header plus footer cannot fit all three regions. Rather than
/// returning a negative transcript height, <see cref="Compute"/> clamps it at zero and keeps
/// the footer directly below the transcript.
/// </remarks>
public readonly record struct TuiScreenLayout(
    int Columns,
    int HeaderTop, int HeaderHeight,
    int TranscriptTop, int TranscriptHeight,
    int FooterTop, int FooterHeight)
{
    /// <summary>
    /// Geometry for a terminal of <paramref name="columns"/> x <paramref name="rows"/>.
    /// <paramref name="columns"/> is carried through unchanged; this layout only splits rows.
    /// </summary>
    public static TuiScreenLayout Compute(int columns, int rows)
    {
        const int headerHeight = 1;
        const int footerHeight = 3;
        var transcriptTop = headerHeight;
        var transcriptHeight = Math.Max(0, rows - headerHeight - footerHeight);
        var footerTop = transcriptTop + transcriptHeight;
        return new TuiScreenLayout(
            Columns: columns,
            HeaderTop: 0, HeaderHeight: headerHeight,
            TranscriptTop: transcriptTop, TranscriptHeight: transcriptHeight,
            FooterTop: footerTop, FooterHeight: footerHeight);
    }
}
