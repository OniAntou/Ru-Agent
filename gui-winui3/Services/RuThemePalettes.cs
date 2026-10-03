using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace RuApp.Services;

/// <summary>
/// Full-palette theme packs. WinUI only resolves Light/Dark theme dictionaries,
/// so a pack is applied by writing its colors into the active base dictionary:
/// every Ru brush stays ThemeResource-bound and updates live. Studio packs carry
/// the exact App.xaml values, so switching back restores pixel-identical colors.
/// </summary>
public static class RuThemePalettes
{
    public sealed record ThemePalette(
        Windows.UI.Color Canvas,
        Windows.UI.Color Sidebar,
        Windows.UI.Color Surface,
        Windows.UI.Color Ink,
        Windows.UI.Color Muted,
        Windows.UI.Color Line,
        Windows.UI.Color Accent,
        Windows.UI.Color AccentSoft,
        Windows.UI.Color AccentHover,
        Windows.UI.Color AccentPressed,
        Windows.UI.Color Success,
        Windows.UI.Color Warning,
        Windows.UI.Color Danger,
        Windows.UI.Color Info,
        Windows.UI.Color Violet,
        Windows.UI.Color Pink,
        Windows.UI.Color ToggleOn,
        Windows.UI.Color ToggleOver,
        Windows.UI.Color TogglePressed,
        Windows.UI.Color SliderValue,
        Windows.UI.Color SliderValueOver,
        Windows.UI.Color SliderThumb,
        Windows.UI.Color SliderThumbOver);

    private static Windows.UI.Color C(byte r, byte g, byte b) =>
        Windows.UI.Color.FromArgb(255, r, g, b);

    public static readonly ThemePalette StudioLight = new(
        C(0xF4, 0xF5, 0xF7), C(0xEA, 0xEC, 0xEF), C(0xFF, 0xFF, 0xFF),
        C(0x0F, 0x17, 0x2A), C(0x5B, 0x6B, 0x82), C(0xCB, 0xD5, 0xE1),
        C(0x1E, 0x29, 0x3B), C(0xE2, 0xE8, 0xF0), C(0x33, 0x41, 0x55), C(0x0B, 0x12, 0x20),
        C(0x04, 0x78, 0x57), C(0xF5, 0x9E, 0x0B), C(0xB9, 0x1C, 0x1C),
        C(0x07, 0x59, 0x85), C(0x7E, 0x22, 0xCE), C(0xBE, 0x12, 0x3C),
        C(0x1E, 0x29, 0x3B), C(0x33, 0x41, 0x55), C(0x47, 0x55, 0x69),
        C(0x1E, 0x29, 0x3B), C(0x33, 0x41, 0x55),
        C(0x1E, 0x29, 0x3B), C(0x33, 0x41, 0x55));

    public static readonly ThemePalette StudioDark = new(
        C(0x16, 0x18, 0x1D), C(0x11, 0x13, 0x17), C(0x20, 0x23, 0x2A),
        C(0xF1, 0xF5, 0xF9), C(0x94, 0xA3, 0xB8), C(0x33, 0x41, 0x55),
        C(0xE2, 0xE8, 0xF0), C(0x28, 0x2D, 0x37), C(0xF1, 0xF5, 0xF9), C(0xCB, 0xD5, 0xE1),
        C(0x34, 0xD3, 0x99), C(0xFB, 0xBF, 0x24), C(0xF8, 0x71, 0x71),
        C(0x38, 0xBD, 0xF8), C(0xC0, 0x84, 0xFC), C(0xF4, 0x72, 0xB6),
        C(0xE2, 0xE8, 0xF0), C(0xF8, 0xFA, 0xFC), C(0xFF, 0xFF, 0xFF),
        C(0xE2, 0xE8, 0xF0), C(0xF8, 0xFA, 0xFC),
        C(0xE2, 0xE8, 0xF0), C(0xF8, 0xFA, 0xFC));

    /// <summary>
    /// Starry Night, sampled from Van Gogh's 1889 painting (public domain,
    /// Google Art Project scan): deep-sky blues, moonlight ink, moon gold.
    /// </summary>
    public static readonly ThemePalette StarryNight = new(
        C(0x23, 0x2F, 0x4C), C(0x1B, 0x27, 0x40), C(0x2B, 0x3A, 0x5E),
        C(0xF3, 0xED, 0xDC), C(0x93, 0xA7, 0xC4), C(0x46, 0x58, 0x7E),
        C(0xD9, 0xB4, 0x4A), C(0x3D, 0x3A, 0x26), C(0xE8, 0xC8, 0x6A), C(0xB0, 0x8F, 0x3A),
        C(0x4A, 0xDE, 0x80), C(0xFB, 0xBF, 0x24), C(0xFF, 0x8A, 0x8A),
        C(0x7D, 0xD3, 0xFC), C(0xB7, 0x9C, 0xFF), C(0xF9, 0xA8, 0xD4),
        C(0xD9, 0xB4, 0x4A), C(0xE8, 0xC8, 0x6A), C(0xB0, 0x8F, 0x3A),
        C(0xD9, 0xB4, 0x4A), C(0xE8, 0xC8, 0x6A),
        C(0xD9, 0xB4, 0x4A), C(0xE8, 0xC8, 0x6A));

    public static bool IsDark(UiTheme theme) =>
        theme is UiTheme.Dark or UiTheme.DarkMeteorShower or UiTheme.StarryNight;

    public static string BaseKey(UiTheme theme) =>
        IsDark(theme) ? "Dark" : "Light";

    public static ThemePalette PaletteFor(UiTheme theme) => theme switch
    {
        UiTheme.Dark or UiTheme.DarkMeteorShower => StudioDark,
        UiTheme.StarryNight => StarryNight,
        _ => StudioLight,
    };

    private static void SetColor(ResourceDictionary dict, string key, Windows.UI.Color color) =>
        dict[key] = color;

    private static void SetBrush(ResourceDictionary dict, string key, Windows.UI.Color color) =>
        dict[key] = new SolidColorBrush(color);

    /// <summary>
    /// Writes the pack into the active Light/Dark dictionary. Callers flip
    /// RequestedTheme around this call so ThemeResource bindings re-resolve even
    /// when the base (Light/Dark) did not change.
    /// </summary>
    public static void Apply(UiTheme theme)
    {
        if (Application.Current?.Resources.ThemeDictionaries[BaseKey(theme)] is not ResourceDictionary dict)
        {
            return;
        }

        var p = PaletteFor(theme);
        SetColor(dict, "RuCanvasColor", p.Canvas);
        SetColor(dict, "RuSidebarColor", p.Sidebar);
        SetColor(dict, "RuSurfaceColor", p.Surface);
        SetColor(dict, "RuInkColor", p.Ink);
        SetColor(dict, "RuMutedColor", p.Muted);
        SetColor(dict, "RuLineColor", p.Line);
        SetColor(dict, "RuAccentColor", p.Accent);
        SetColor(dict, "RuAccentSoftColor", p.AccentSoft);
        SetColor(dict, "RuAccentHoverColor", p.AccentHover);
        SetColor(dict, "RuAccentPressedColor", p.AccentPressed);
        SetColor(dict, "RuSuccessColor", p.Success);
        SetColor(dict, "RuWarningColor", p.Warning);
        SetColor(dict, "RuDangerColor", p.Danger);
        SetColor(dict, "RuInfoColor", p.Info);
        SetColor(dict, "RuVioletColor", p.Violet);
        SetColor(dict, "RuPinkColor", p.Pink);

        SetBrush(dict, "ToggleSwitchFillOn", p.ToggleOn);
        SetBrush(dict, "ToggleSwitchFillOnPointerOver", p.ToggleOver);
        SetBrush(dict, "ToggleSwitchFillOnPressed", p.TogglePressed);
        SetBrush(dict, "ToggleSwitchStrokeOn", p.ToggleOn);
        SetBrush(dict, "ToggleSwitchStrokeOnPointerOver", p.ToggleOver);
        SetBrush(dict, "ToggleSwitchStrokeOnPressed", p.TogglePressed);
        SetBrush(dict, "SliderTrackValueFill", p.SliderValue);
        SetBrush(dict, "SliderTrackValueFillPointerOver", p.SliderValueOver);
        SetBrush(dict, "SliderThumbBackground", p.SliderThumb);
        SetBrush(dict, "SliderThumbBackgroundPointerOver", p.SliderThumbOver);
        SetBrush(dict, "ComboBoxItemPillFillBrush", p.Accent);

        SetBrush(dict, "ButtonBackgroundPointerOver", p.Canvas);
        SetBrush(dict, "ButtonBackgroundPressed", p.AccentSoft);
        SetBrush(dict, "ButtonForegroundPointerOver", p.Ink);
        SetBrush(dict, "ButtonForegroundPressed", p.Ink);

        SetBrush(dict, "SubtleFillColorSecondary", p.Canvas);
        SetBrush(dict, "SubtleFillColorTertiary", p.AccentSoft);

        SetBrush(dict, "ListViewItemBackgroundPointerOver", p.Canvas);
        SetBrush(dict, "ListViewItemBackgroundPressed", p.AccentSoft);
        SetBrush(dict, "ListViewItemBackgroundSelected", p.AccentSoft);
        SetBrush(dict, "ListViewItemBackgroundSelectedPointerOver", p.Line);
        SetBrush(dict, "ListViewItemBackgroundSelectedPressed", p.Line);
        SetBrush(dict, "ListViewItemForegroundPointerOver", p.Ink);
        SetBrush(dict, "ListViewItemForegroundSelected", p.Ink);
        SetBrush(dict, "ListViewItemForegroundSelectedPointerOver", p.Ink);

        SetBrush(dict, "ComboBoxBackground", p.Canvas);
        SetBrush(dict, "ComboBoxBackgroundPointerOver", p.AccentSoft);
        SetBrush(dict, "ComboBoxBackgroundPressed", p.Line);
        SetBrush(dict, "ComboBoxBackgroundDisabled", p.Canvas);
        SetBrush(dict, "ComboBoxBackgroundUnfocused", p.Canvas);
        SetBrush(dict, "ComboBoxBackgroundFocused", p.Canvas);
        SetBrush(dict, "ComboBoxBackgroundBorderBrushFocused", p.Ink);
        SetBrush(dict, "ComboBoxBackgroundBorderBrushUnfocused", p.Line);
        SetBrush(dict, "ComboBoxForeground", p.Ink);
        SetBrush(dict, "ComboBoxForegroundPointerOver", p.Ink);
        SetBrush(dict, "ComboBoxForegroundPressed", p.Ink);
        SetBrush(dict, "ComboBoxForegroundDisabled", p.Muted);
        SetBrush(dict, "ComboBoxForegroundFocused", p.Ink);
        SetBrush(dict, "ComboBoxForegroundFocusedPressed", p.Ink);
        SetBrush(dict, "ComboBoxBorderBrush", p.Line);
        SetBrush(dict, "ComboBoxBorderBrushPointerOver", p.Muted);
        SetBrush(dict, "ComboBoxBorderBrushPressed", p.Accent);
        SetBrush(dict, "ComboBoxBorderBrushDisabled", p.Line);
        SetBrush(dict, "ComboBoxDropDownGlyphForeground", p.Muted);
        SetBrush(dict, "ComboBoxDropDownGlyphForegroundDisabled", p.Muted);
        SetBrush(dict, "ComboBoxDropDownGlyphForegroundFocused", p.Ink);
        SetBrush(dict, "ComboBoxDropDownForeground", p.Ink);
        SetBrush(dict, "ComboBoxDropDownBackground", p.Surface);
        SetBrush(dict, "ComboBoxDropDownBorderBrush", p.Line);
        SetBrush(dict, "ComboBoxPlaceHolderForeground", p.Muted);
        SetBrush(dict, "ComboBoxPlaceHolderForegroundPointerOver", p.Muted);
        SetBrush(dict, "ComboBoxPlaceHolderForegroundPressed", p.Ink);
        SetBrush(dict, "ComboBoxPlaceHolderForegroundDisabled", p.Muted);
        SetBrush(dict, "ComboBoxPlaceHolderForegroundFocused", p.Muted);
        SetBrush(dict, "ComboBoxItemForeground", p.Ink);
        SetBrush(dict, "ComboBoxItemForegroundPointerOver", p.Ink);
        SetBrush(dict, "ComboBoxItemForegroundPressed", p.Ink);
        SetBrush(dict, "ComboBoxItemForegroundSelected", p.Ink);
        SetBrush(dict, "ComboBoxItemForegroundSelectedPointerOver", p.Ink);
        SetBrush(dict, "ComboBoxItemForegroundDisabled", p.Muted);
        SetBrush(dict, "ComboBoxItemBackgroundPointerOver", p.Canvas);
        SetBrush(dict, "ComboBoxItemBackgroundPressed", p.AccentSoft);
        SetBrush(dict, "ComboBoxItemBackgroundSelected", p.AccentSoft);
        SetBrush(dict, "ComboBoxItemBackgroundSelectedUnfocused", p.AccentSoft);
        SetBrush(dict, "ComboBoxItemBackgroundSelectedPointerOver", p.Line);
        SetBrush(dict, "ComboBoxItemBackgroundSelectedPressed", p.Line);
        SetBrush(dict, "ComboBoxDropDownBackgroundPointerOver", p.AccentSoft);
        SetBrush(dict, "ComboBoxDropDownBackgroundPointerPressed", p.Line);
    }
}
