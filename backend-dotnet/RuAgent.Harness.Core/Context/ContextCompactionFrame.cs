using System.Text.RegularExpressions;

namespace RuAgent.Harness.Core.Context;

/// <summary>
/// The frame that marks a compaction summary as untrusted evidence in model context.
/// </summary>
/// <remarks>
/// The body is derived from history the model and tools wrote, so it may contain the
/// frame's own markers. Left as they are, a body could close its frame early and make
/// the text after it read as if it were outside the untrusted block. Markers inside the
/// body are neutralized (the bracket becomes a parenthesis), so the only frame markers
/// in a summary are the ones written here.
/// </remarks>
public static partial class ContextCompactionFrame
{
    public const string Close = "[/context_compaction]";

    public static string Wrap(string attributes, string body) =>
        $"[context_compaction {attributes}]\n{Neutralize(body)}\n{Close}";

    public static string Neutralize(string body) =>
        FrameMarker().Replace(body, match => "(" + match.Value[1..]);

    [GeneratedRegex(@"\[\s*/?\s*context_compaction", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FrameMarker();
}
