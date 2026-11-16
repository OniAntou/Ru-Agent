using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RuApp.Services;

public enum AppearanceBackgroundKind { None, Image, Video }

/// <summary>How a background picture fills the window.</summary>
public enum AppearanceBackgroundFit { Fill, Fit, Stretch, Center }

public enum AppearanceSidebarSide { Left, Right }

/// <summary>A step on the corner-radius scale: square, the stock radii, or half again as round.</summary>
public enum AppearanceCornerStyle { Default, Square, Round }

/// <summary>
/// Color overrides for one base theme. A null entry keeps the theme's own color;
/// every value is a <c>#RRGGBB</c> string once sanitized.
/// </summary>
public sealed record AppearanceColorSet(
    string? Canvas = null,
    string? Sidebar = null,
    string? Surface = null,
    string? Text = null,
    string? MutedText = null,
    string? Accent = null,
    string? Border = null)
{
    public static readonly AppearanceColorSet Empty = new();

    [JsonIgnore]
    public bool IsEmpty => this == Empty;

    public AppearanceColorSet Sanitized() => new(
        AppearanceColor.Normalize(Canvas),
        AppearanceColor.Normalize(Sidebar),
        AppearanceColor.Normalize(Surface),
        AppearanceColor.Normalize(Text),
        AppearanceColor.Normalize(MutedText),
        AppearanceColor.Normalize(Accent),
        AppearanceColor.Normalize(Border));
}

/// <summary>
/// The user's look-and-feel choices: background media, color overrides per base
/// theme, fonts and shell layout. Everything here is local GUI preference; none of
/// it reaches the Host or the Broker. <see cref="Sanitized"/> is the single gate
/// every loaded or imported value passes through.
/// </summary>
public sealed record AppearancePreferences
{
    public const double MinimumPanelOpacity = 0.3;
    public const double MaximumPanelOpacity = 1.0;
    public const double DefaultPanelOpacity = 0.85;
    public const int MaximumFontNameLength = 120;

    public static readonly AppearancePreferences Default = new();

    public AppearanceBackgroundKind BackgroundKind { get; init; } = AppearanceBackgroundKind.None;

    /// <summary>A file name inside the Ru backgrounds folder, never a path.</summary>
    public string? BackgroundFile { get; init; }

    public AppearanceBackgroundFit BackgroundFit { get; init; } = AppearanceBackgroundFit.Fill;

    /// <summary>
    /// How much of the theme covers the background, so text stays readable: the canvas
    /// scrim and the surfaces floating on it (composer, task shelf, side card) alike.
    /// </summary>
    public double PanelOpacity { get; init; } = DefaultPanelOpacity;

    public AppearanceColorSet LightColors { get; init; } = AppearanceColorSet.Empty;

    public AppearanceColorSet DarkColors { get; init; } = AppearanceColorSet.Empty;

    /// <summary>A font family name for the interface; null keeps Inter.</summary>
    public string? UiFont { get; init; }

    /// <summary>A font family name for code and terminals; null keeps Cascadia Code.</summary>
    public string? CodeFont { get; init; }

    public AppearanceSidebarSide SidebarSide { get; init; } = AppearanceSidebarSide.Left;

    public AppearanceCornerStyle CornerStyle { get; init; } = AppearanceCornerStyle.Default;

    [JsonIgnore]
    public bool HasBackground => BackgroundKind != AppearanceBackgroundKind.None && BackgroundFile is not null;

    public AppearanceColorSet ColorsFor(bool isDark) => isDark ? DarkColors : LightColors;

    public AppearancePreferences WithColors(bool isDark, AppearanceColorSet colors) =>
        isDark ? this with { DarkColors = colors } : this with { LightColors = colors };

    /// <summary>
    /// Clamps and validates every field. Unknown enum values, malformed colors,
    /// font names that look like paths and background names Ru did not generate
    /// all fall back to their defaults.
    /// </summary>
    public AppearancePreferences Sanitized()
    {
        var file = AppearanceMediaFiles.IsGeneratedName(BackgroundFile) ? BackgroundFile : null;
        var kind = Enum.IsDefined(BackgroundKind) ? BackgroundKind : AppearanceBackgroundKind.None;
        if (file is null || kind == AppearanceBackgroundKind.None || AppearanceMediaFiles.KindOf(file) != kind)
        {
            kind = AppearanceBackgroundKind.None;
            file = null;
        }

        return new AppearancePreferences
        {
            BackgroundKind = kind,
            BackgroundFile = file,
            BackgroundFit = Enum.IsDefined(BackgroundFit) ? BackgroundFit : AppearanceBackgroundFit.Fill,
            PanelOpacity = double.IsFinite(PanelOpacity)
                ? Math.Clamp(PanelOpacity, MinimumPanelOpacity, MaximumPanelOpacity)
                : DefaultPanelOpacity,
            LightColors = (LightColors ?? AppearanceColorSet.Empty).Sanitized(),
            DarkColors = (DarkColors ?? AppearanceColorSet.Empty).Sanitized(),
            UiFont = NormalizeFontName(UiFont),
            CodeFont = NormalizeFontName(CodeFont),
            SidebarSide = Enum.IsDefined(SidebarSide) ? SidebarSide : AppearanceSidebarSide.Left,
            CornerStyle = Enum.IsDefined(CornerStyle) ? CornerStyle : AppearanceCornerStyle.Default,
        };
    }

    /// <summary>
    /// A font is named, not loaded from a file: a value with a URI scheme or a path
    /// separator could point XAML at an arbitrary file, so it is dropped.
    /// </summary>
    public static string? NormalizeFontName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaximumFontNameLength) return null;
        if (trimmed.IndexOfAny([':', '/', '\\', '#', '<', '>', '"', '\0']) >= 0) return null;
        return trimmed;
    }
}

/// <summary>Parses and formats the <c>#RRGGBB</c> strings the preferences store.</summary>
public static partial class AppearanceColor
{
    [GeneratedRegex("^#?([0-9a-fA-F]{6})$")]
    private static partial Regex HexPattern();

    public static string? Normalize(string? value)
    {
        if (value is null) return null;
        var match = HexPattern().Match(value.Trim());
        return match.Success ? "#" + match.Groups[1].Value.ToUpperInvariant() : null;
    }

    public static bool TryParse(string? value, out Windows.UI.Color color)
    {
        color = default;
        if (Normalize(value) is not { } hex) return false;
        var rgb = int.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        color = Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    public static string ToHex(Windows.UI.Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
