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

    /// <summary>"#rrggbb" for an opaque text color; pictures take the reader's ink.</summary>
    public static string Ink(byte red, byte green, byte blue) => $"#{red:x2}{green:x2}{blue:x2}";

    private static string FirstWord(string? info)
    {
        string trimmed = (info ?? string.Empty).Trim();
        int space = trimmed.IndexOfAny([' ', '\t', '{']);
        return space < 0 ? trimmed : trimmed[..space];
    }
}
