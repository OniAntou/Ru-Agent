using RuApp.Services;

namespace RuApp.Controls;

/// <summary>
/// What a Markdown render may draw as a picture. <see cref="Settled"/> is false for the
/// block a reply is still streaming into: it keeps showing source until the reply ends,
/// so a half-written diagram or formula is never sent to the renderer.
/// </summary>
public sealed record MarkdownPictureContext(bool Settled, SandboxRenderStyle Style);

/// <summary>WinUI-free decisions about which Markdown becomes a picture.</summary>
public static class MarkdownPictures
{
    public static bool IsMermaid(string? info) =>
        string.Equals(FirstWord(info), "mermaid", StringComparison.OrdinalIgnoreCase);

    /// <summary>A block may hold a picture: re-render it once its reply settles.</summary>
    public static bool MayHold(string source) =>
        source.Contains('$', StringComparison.Ordinal)
        || source.Contains("mermaid", StringComparison.OrdinalIgnoreCase);

    /// <summary>Only a block before the streaming tail, or any block of a finished reply, is drawn.</summary>
    public static bool IsSettled(int blockIndex, int blockCount, bool streaming) =>
        !streaming || blockIndex < blockCount - 1;

    /// <summary>
    /// How many rendered blocks can be kept. The key prefix says which blocks are
    /// unchanged; a block that was built while it was the streaming tail and is settled
    /// now must still be rebuilt when it may hold a picture, or a diagram written in the
    /// middle of a reply would stay source after the next block arrived.
    /// </summary>
    public static int Retained(int keyPrefix, IReadOnlyList<(bool Settled, string Source)> built, Func<int, bool> settledNow)
    {
        for (int index = 0; index < keyPrefix && index < built.Count; index++)
        {
            if (!built[index].Settled && settledNow(index) && MayHold(built[index].Source)) return index;
        }

        return keyPrefix;
    }

    /// <summary>A diagram may shrink to fit its column, but never below this share of its own size.</summary>
    public const double MinDiagramScale = 0.75;

    /// <summary>
    /// The width a diagram is shown at: its own width when it fits, the column's when a
    /// little smaller, and no less than <see cref="MinDiagramScale"/> of its own, past
    /// which it scrolls sideways rather than shrinking its labels out of reading.
    /// </summary>
    public static double DiagramWidth(double natural, double available) =>
        available > 0 ? Math.Min(natural, Math.Max(available, natural * MinDiagramScale)) : natural;

    /// <summary>"#rrggbb" for an opaque text color; pictures take the reader's ink.</summary>
    public static string Ink(byte red, byte green, byte blue) => $"#{red:x2}{green:x2}{blue:x2}";

    private static string FirstWord(string? info)
    {
        string trimmed = (info ?? string.Empty).Trim();
        int space = trimmed.IndexOfAny([' ', '\t', '{']);
        return space < 0 ? trimmed : trimmed[..space];
    }
}
