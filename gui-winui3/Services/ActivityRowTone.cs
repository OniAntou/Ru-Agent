namespace RuApp.Services;

/// <summary>
/// The text tone of a settled activity row.
/// </summary>
/// <remarks>
/// Every settled row used to be painted in the ink colour, the same as the agent's own
/// reply, so a turn read as one flat column where "Thought briefly" weighed as much as
/// the answer. A finished step now recedes to the muted tone; a row that needs the
/// user (waiting) keeps ink, and a failure keeps the danger colour. Both muted and ink
/// clear AA on every theme surface (VisualPolishContractTests).
/// </remarks>
public static class ActivityRowTone
{
    public const string Ink = "RuInkBrush";
    public const string Muted = "RuMutedBrush";
    public const string Danger = "RuDangerBrush";

    public static string BrushKeyFor(bool isFailed, bool isWaiting) =>
        isFailed ? Danger : isWaiting ? Ink : Muted;
}
