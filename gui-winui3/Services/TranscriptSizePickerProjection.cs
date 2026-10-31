namespace RuApp.Services;

/// <summary>
/// The choices the conversation text size picker offers, and how each is labelled.
/// </summary>
/// <remarks>
/// Kept out of the view so the range, the default, and the wording are asserted
/// rather than read. The picker's steps come from the store's bounds, so widening
/// the range there cannot leave the picker offering sizes the store would clamp.
/// </remarks>
internal static class TranscriptSizePickerProjection
{
    /// <summary>
    /// Every whole size from the store's minimum to its maximum, ascending.
    /// </summary>
    internal static IReadOnlyList<double> Sizes()
    {
        var sizes = new List<double>();
        for (double size = TranscriptFontSizeStore.MinimumSize;
             size <= TranscriptFontSizeStore.MaximumSize;
             size += 1)
        {
            sizes.Add(size);
        }

        return sizes;
    }

    /// <summary>
    /// The label for one size. Whole sizes print without a decimal point so the
    /// picker reads "14" rather than "14.0".
    /// </summary>
    internal static string Label(double size) =>
        Math.Abs(size - Math.Round(size)) < 0.001
            ? ((int)Math.Round(size)).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : size.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The size to show as selected for a stored value, clamped into the range the
    /// picker can represent so a hand-edited settings file still selects a row.
    /// </summary>
    internal static double Normalize(double stored) => TranscriptFontSizeStore.Clamp(stored);

    /// <summary>
    /// Automation id for one size's row, stable across restarts so UI automation can
    /// address a specific choice.
    /// </summary>
    internal static string OptionAutomationId(double size) =>
        $"TranscriptSizeOption{Label(size)}";
}
