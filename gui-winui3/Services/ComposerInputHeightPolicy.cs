using System;

namespace RuApp.Services;

/// <summary>
/// How tall the composer's text box may grow before it scrolls.
/// </summary>
/// <remarks>
/// A fixed cap showed about eight lines however tall the window was, so a long prompt
/// was read through a slot. The cap now follows the chat view's height: a share of it,
/// never below the old cap (a short window keeps today's composer) and never so tall
/// that the transcript above disappears behind a draft.
/// </remarks>
public static class ComposerInputHeightPolicy
{
    /// <summary>The cap for a short or unmeasured view; the composer's height before this policy.</summary>
    public const double MinimumMaxHeight = 180;

    /// <summary>The cap however tall the view is.</summary>
    public const double MaximumMaxHeight = 480;

    /// <summary>The share of the chat view a draft may take.</summary>
    public const double ViewShare = 0.35;

    public static double MaxHeightFor(double viewHeight)
    {
        if (double.IsNaN(viewHeight) || double.IsInfinity(viewHeight) || viewHeight <= 0)
        {
            return MinimumMaxHeight;
        }

        return Math.Clamp(Math.Floor(viewHeight * ViewShare), MinimumMaxHeight, MaximumMaxHeight);
    }
}
