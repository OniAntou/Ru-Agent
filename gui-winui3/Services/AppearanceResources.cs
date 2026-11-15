using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace RuApp.Services;

/// <summary>
/// Puts the user's fonts and corner style into the application resources before the
/// first window is built. XAML reads <c>RuUiFontFamily</c> and the corner tokens with
/// <c>StaticResource</c>, which resolves once, so this runs at launch: views built
/// afterwards pick up the replaced resources, and each App.xaml style that already
/// resolved a stock value is swapped for a derived style that sets the user's value.
/// </summary>
public static class AppearanceResources
{
    private const string UiFallback = "Segoe UI Variable, Segoe UI, Segoe UI Emoji";
    private const string CodeFallback = "Cascadia Code, Consolas";

    private static readonly string[] UiFontKeys = ["RuUiFontFamily", "RuDisplayFontFamily"];
    private static readonly string[] CodeFontKeys = ["RuMonoFontFamily"];

    private static readonly string[] CornerKeys =
    [
        "AppMicroPillCornerRadius",
        "AppControlCornerRadius",
        "AppInsetCornerRadius",
        "AppCardCornerRadius",
        "AppModalCornerRadius",
    ];

    /// <summary>The font family string Ru uses for a chosen name, with a fallback chain for missing glyphs.</summary>
    public static string UiFontSource(string name) => $"{name}, {UiFallback}";

    public static string CodeFontSource(string name) => $"{name}, {CodeFallback}";

    /// <summary>The radius for a stock token under a corner style; a micro pill stays a pill.</summary>
    public static double ScaleRadius(double stock, AppearanceCornerStyle style, bool isMicroPill) => style switch
    {
        AppearanceCornerStyle.Square => isMicroPill ? stock : Math.Min(stock, 2),
        AppearanceCornerStyle.Round => isMicroPill ? stock : Math.Round(stock * 1.5),
        _ => stock,
    };

    public static void ApplyAtStartup(ResourceDictionary resources, AppearancePreferences preferences)
    {
        var replacements = new Dictionary<object, object>(ReferenceEqualityComparer.Instance);
        if (preferences.UiFont is { } ui)
        {
            ReplaceFonts(resources, UiFontKeys, new FontFamily(UiFontSource(ui)), replacements);
        }

        if (preferences.CodeFont is { } code)
        {
            ReplaceFonts(resources, CodeFontKeys, new FontFamily(CodeFontSource(code)), replacements);
        }

        var corners = new Dictionary<CornerRadius, CornerRadius>();
        if (preferences.CornerStyle != AppearanceCornerStyle.Default)
        {
            foreach (var key in CornerKeys)
            {
                if (!resources.TryGetValue(key, out var value) || value is not CornerRadius stock) continue;
                var radius = ScaleRadius(stock.TopLeft, preferences.CornerStyle, key == "AppMicroPillCornerRadius");
                var scaled = new CornerRadius(radius);
                resources[key] = scaled;
                corners.TryAdd(stock, scaled);
            }
        }

        if (replacements.Count == 0 && corners.Count == 0) return;
        RewriteStyles(resources, replacements, corners);
        foreach (var theme in resources.ThemeDictionaries.Values.OfType<ResourceDictionary>())
        {
            RewriteStyles(theme, replacements, corners);
        }
    }

    /// <summary>
    /// Replaces each style that set a stock font or radius with a style based on it
    /// that sets the user's value. The original is never edited: App.xaml styles can
    /// already be sealed at this point, and writing a sealed setter fails with
    /// E_ACCESSDENIED inside the XAML runtime.
    /// </summary>
    private static void RewriteStyles(
        ResourceDictionary dictionary,
        Dictionary<object, object> replacements,
        Dictionary<CornerRadius, CornerRadius> corners)
    {
        var derived = new List<(object Key, Style Style)>();
        foreach (var (key, value) in dictionary)
        {
            if (value is not Style style) continue;
            Style? replacement = null;
            foreach (var setter in style.Setters.OfType<Setter>())
            {
                if (setter.Property is null || setter.Value is null) continue;
                object? next = null;
                if (replacements.TryGetValue(setter.Value, out var font)) next = font;
                else if (setter.Value is CornerRadius radius && corners.TryGetValue(radius, out var scaled)) next = scaled;
                if (next is null) continue;
                replacement ??= new Style(style.TargetType) { BasedOn = style };
                replacement.Setters.Add(new Setter(setter.Property, next));
            }

            if (replacement is not null) derived.Add((key, replacement));
        }

        foreach (var (key, style) in derived) dictionary[key] = style;
    }

    private static void ReplaceFonts(
        ResourceDictionary resources,
        IEnumerable<string> keys,
        FontFamily replacement,
        Dictionary<object, object> replacements)
    {
        foreach (var key in keys)
        {
            if (resources.TryGetValue(key, out var stock) && stock is not null) replacements.TryAdd(stock, replacement);
            resources[key] = replacement;
        }
    }
}
