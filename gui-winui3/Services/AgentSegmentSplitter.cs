namespace RuApp.Services;

/// <summary>
/// Snaps a live transcript split to a line boundary. Activity frames can land
/// mid-sentence while prose still streams, and freezing there slices one
/// sentence across two bubbles with a hard visual joint. Snapping back to the
/// previous newline keeps the joint on a line edge; without one, the previous
/// word edge still reads like a normal wrap. The remainder keeps streaming in
/// the tail below the new row.
/// </summary>
internal static class AgentSegmentSplitter
{
    public static int SnapToLineStart(string content, int frozen)
    {
        if (string.IsNullOrEmpty(content))
        {
            return 0;
        }

        int start = Math.Clamp(frozen, 0, content.Length);
        if (start >= content.Length)
        {
            return content.Length;
        }

        int lineBreak = content.LastIndexOf('\n', content.Length - 1, content.Length - start);
        if (lineBreak >= start)
        {
            return lineBreak + 1;
        }

        int wordEdge = content.LastIndexOfAny([' ', '\t'], content.Length - 1, content.Length - start);
        return wordEdge >= start ? wordEdge + 1 : content.Length;
    }
}
