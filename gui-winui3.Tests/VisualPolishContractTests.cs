using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Visual polish: muted text clears WCAG AA on every theme surface, spacing follows
/// the 4/8/16 rhythm, and the activity wave stays throttled and motion-respecting.
///
/// The contrast cases compute real ratios from the theme tokens. The structural
/// cases read the declared values instead of searching for forbidden strings, so a
/// spacing or colour that drifts off the scale is caught by its value rather than by
/// matching one spelling of it.
/// </summary>
public sealed class VisualPolishContractTests
{
    /// <summary>
    /// The approved spacing values. The rhythm is 4/8/16/24/32, plus the hairline
    /// 1/2/5/6 optical nudges that the shipped views settled on where a strict step
    /// reads wrong — a glyph against its label, or a label against its value. A new
    /// value must be added here on purpose; spacing drifting one literal at a time is
    /// exactly what this guards.
    /// </summary>
    private static readonly double[] ApprovedSpacing = [0, 1, 2, 4, 5, 6, 8, 12, 16, 24, 32, 40, 48];

    private static readonly string[] ThemeViews =
    [
        "ChatView.xaml", "SettingsView.xaml", "DiagnosticsView.xaml", "LogView.xaml",
    ];

    [Fact]
    public void Light_muted_text_passes_aa_on_canvas_and_sidebar()
    {
        var light = ThemeBlock("Light");

        string muted = ColorValue(light, "RuMutedColor");
        Assert.True(Contrast(muted, ColorValue(light, "RuSurfaceColor")) >= 4.5,
            $"Muted {muted} must clear 4.5:1 on surface.");
        Assert.True(Contrast(muted, ColorValue(light, "RuSidebarColor")) >= 4.5,
            $"Muted {muted} must clear 4.5:1 on sidebar.");
    }

    [Fact]
    public void Dark_muted_text_passes_aa_on_surface()
    {
        var dark = ThemeBlock("Dark");

        Assert.True(Contrast(ColorValue(dark, "RuMutedColor"), ColorValue(dark, "RuSurfaceColor")) >= 4.5,
            "Dark muted must clear 4.5:1 on surface.");
    }

    [Fact]
    public void Accent_hue_tokens_pass_aa_on_light_surfaces()
    {
        var light = ThemeBlock("Light");
        string surface = ColorValue(light, "RuSurfaceColor");

        foreach (var token in new[] { "RuInfoColor", "RuVioletColor", "RuPinkColor", "RuDangerColor" })
        {
            Assert.True(Contrast(ColorValue(light, token), surface) >= 4.5,
                $"{token} must clear 4.5:1 on light surface.");
        }
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Every_body_text_token_clears_aa_on_its_own_surface(string themeName)
    {
        // Ink and muted are the two body-copy foregrounds; both must be readable on
        // the surface they are painted on, in both themes.
        var theme = ThemeBlock(themeName);
        string surface = ColorValue(theme, "RuSurfaceColor");

        foreach (var token in new[] { "RuInkColor", "RuMutedColor" })
        {
            Assert.True(Contrast(ColorValue(theme, token), surface) >= 4.5,
                $"{themeName}/{token} must clear 4.5:1 on surface.");
        }
    }

    [Fact]
    public void Status_text_never_dims_muted_foreground_with_opacity()
    {
        // Muted-on-surface already sits near the AA floor; extra opacity would push
        // small text below 4.5:1 on the light theme.
        var offenders = new List<string>();
        foreach (var element in GuiSource.Xml("Views", "ChatView.xaml").Descendants())
        {
            string? foreground = GuiSource.AttributeValue(element, "Foreground");
            if (foreground?.Contains("RuMutedBrush", StringComparison.Ordinal) != true)
            {
                continue;
            }

            if (GuiSource.HasAttribute(element, "Opacity"))
            {
                offenders.Add($"<{element.Name.LocalName}> muted + Opacity={GuiSource.AttributeValue(element, "Opacity")}");
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Diagnostic_surfaces_paint_text_with_theme_brushes()
    {
        // An opaque hardcoded foreground breaks one of the two themes; the only
        // allowed exception is white on an opaque saturated badge pill. Translucent
        // eight-digit tints are theme-neutral by construction.
        foreach (var view in new[] { "DiagnosticsView.xaml", "LogView.xaml" })
        {
            foreach (var element in GuiSource.Xml("Views", view).Descendants())
            {
                foreach (var attribute in element.Attributes())
                {
                    if (!attribute.Name.LocalName.StartsWith("Foreground", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var match = Regex.Match(attribute.Value, "^#(?<hex>[0-9A-Fa-f]{6})$");
                    if (!match.Success)
                    {
                        continue;
                    }

                    Assert.True(
                        string.Equals(attribute.Value, "#FFFFFF", StringComparison.OrdinalIgnoreCase),
                        $"{view}: <{element.Name.LocalName}> must not paint opaque hardcoded text {attribute.Value}.");
                }
            }
        }

        // The status colours must come from the shared palette, not local literals.
        var diagnostics = GuiSource.Text("Views", "DiagnosticsView.xaml");
        var log = GuiSource.Text("Views", "LogView.xaml");
        Assert.Contains("RuDangerBrush", diagnostics, StringComparison.Ordinal);
        Assert.Contains("RuInfoBrush", diagnostics, StringComparison.Ordinal);
        Assert.Contains("RuInfoBrush", log, StringComparison.Ordinal);
        Assert.Contains("RuVioletBrush", log, StringComparison.Ordinal);
        Assert.Contains("RuPinkBrush", log, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_spacing_value_comes_from_the_approved_scale()
    {
        var offScale = new List<string>();
        foreach (var view in ThemeViews)
        {
            foreach (var element in GuiSource.Xml("Views", view).Descendants())
            {
                foreach (var attribute in element.Attributes())
                {
                    if (!string.Equals(attribute.Name.LocalName, "Spacing", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!double.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var spacing))
                    {
                        continue;
                    }

                    if (!ApprovedSpacing.Contains(spacing) && spacing != 0)
                    {
                        offScale.Add($"{view}: <{element.Name.LocalName}> Spacing={attribute.Value}");
                    }
                }
            }
        }

        Assert.Empty(offScale);
    }

    [Fact]
    public void Margins_in_settings_come_from_the_approved_scale()
    {
        var offScale = new List<string>();
        foreach (var element in GuiSource.Xml("Views", "SettingsView.xaml").Descendants())
        {
            var margin = GuiSource.AttributeValue(element, "Margin");
            if (margin is null)
            {
                continue;
            }

            foreach (var part in margin.Split(',', StringSplitOptions.TrimEntries))
            {
                if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    && value != 0
                    && !ApprovedSpacing.Contains(value))
                {
                    offScale.Add($"<{element.Name.LocalName}> Margin={margin}");
                    break;
                }
            }
        }

        Assert.Empty(offScale);
    }

    [Fact]
    public void Welcome_overlay_paints_only_from_theme_tokens()
    {
        var overlay = GuiSource.Text("Controls", "WelcomeOverlay.cs");
        var paths = GuiSource.Text("Controls", "BackgroundPathsControl.cs");

        foreach (var token in new[]
                 {
                     "RuCanvasBrush", "RuSurfaceBrush", "RuInkBrush", "RuMutedBrush",
                     "RuLineBrush", "RuAccentBrush", "RuAccentSoftBrush",
                 })
        {
            Assert.Contains(token, overlay, StringComparison.Ordinal);
        }

        // One deliberate literal: the welcome path stroke is pure white on the dark
        // canvas and theme ink on light, where white would vanish.
        Assert.Contains("Color.FromArgb(255, 255, 255, 255)", paths, StringComparison.Ordinal);
        Assert.Contains("RuInkBrush", paths, StringComparison.Ordinal);
    }

    [Fact]
    public void Wave_respects_the_os_animation_setting_with_a_static_fallback()
    {
        // The frame interval is a named constant, not a literal at the call site.
        Assert.True(
            UiTiming.WaveFrame >= TimeSpan.FromMilliseconds(40),
            $"WaveFrame {UiTiming.WaveFrame} must stay throttled at 40ms or slower.");

        // The wave yields to reduced motion: a static muted tint replaces the
        // 25fps loop when the user disables OS animations.
        var codeBehind = GuiSource.Text("Views", "ChatView.xaml.cs");
        Assert.Contains("StartTextSweep", codeBehind, StringComparison.Ordinal);

        var sweepStart = codeBehind.IndexOf("private void StartTextSweep", StringComparison.Ordinal);
        var sweepEnd = codeBehind.IndexOf("private void StopTextSweep", sweepStart, StringComparison.Ordinal);
        Assert.True(sweepStart >= 0 && sweepEnd > sweepStart, "StartTextSweep body boundary was not found.");
        Assert.Contains("AnimationsEnabled", codeBehind[sweepStart..sweepEnd], StringComparison.Ordinal);
        Assert.Contains("RuMutedBrush", codeBehind[sweepStart..sweepEnd], StringComparison.Ordinal);
    }

    [Fact]
    public void Decorative_loops_respect_the_os_animation_setting()
    {
        // Contrast with the wave above: the decorative loops must consult the
        // accessibility setting rather than always running, and must ask the shared gate
        // rather than reading the OS setting themselves — a second reader is how the
        // loops come to disagree about the setting.
        foreach (var control in new[] { "WelcomeOverlay.cs", "BackgroundPathsControl.cs", "MeteorShowerField.cs" })
        {
            var source = GuiSource.Text("Controls", control);

            Assert.Contains("UiTiming.AnimationsEnabled", source, StringComparison.Ordinal);
            Assert.DoesNotContain("UISettings", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Activity_speed_tuning_is_configured_and_consumed()
    {
        Assert.InRange(UiTiming.ActivityWavePeriodSeconds, 0.05, 5.0);
        Assert.InRange(UiTiming.ActivityFlushBatch, TimeSpan.FromMilliseconds(16), TimeSpan.FromMilliseconds(250));

        var codeBehind = GuiSource.Text("Views", "ChatView.xaml.cs");
        Assert.Contains("ActivityWavePeriodSeconds", codeBehind, StringComparison.Ordinal);

        // The feed renders every frame as it arrives. A completion used to wait out a
        // minimum hold, which is what made the feed stand still and then jump.
        Assert.DoesNotContain("ActivityMinHold", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ActivityFlushBatch", codeBehind, StringComparison.Ordinal);
    }

    private static string ThemeBlock(string theme)
    {
        var document = GuiSource.Xml("App.xaml");

        // x:Key lives in the x: namespace, which AttributeValue deliberately skips so
        // that x:Name cannot answer a lookup for AutomationProperties.Name.
        var block = document.Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "ResourceDictionary"
                && element.Attribute(GuiSource.Xaml + "Key")?.Value == theme);

        Assert.True(block is not null, $"Missing {theme} theme block.");

        var colors = block!.Descendants()
            .Where(element => element.Name.LocalName == "Color")
            .ToDictionary(
                element => element.Attribute(GuiSource.Xaml + "Key")?.Value ?? string.Empty,
                element => element.Value.Trim(),
                StringComparer.Ordinal);

        return string.Join("\n", colors.Select(pair => $"<Color x:Key=\"{pair.Key}\">{pair.Value}</Color>"));
    }

    private static string ColorValue(string themeBlock, string key)
    {
        var match = Regex.Match(themeBlock, $"<Color x:Key=\"{Regex.Escape(key)}\">([^<]+)</Color>");
        Assert.True(match.Success, $"Missing {key}.");
        return match.Groups[1].Value.Trim();
    }

    private static double Contrast(string foreground, string background)
    {
        double l1 = Luminance(foreground);
        double l2 = Luminance(background);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    private static double Luminance(string hex)
    {
        var rgb = hex.TrimStart('#');
        double channel(int i)
        {
            double c = Convert.ToInt32(rgb.Substring(i, 2), 16) / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * channel(0) + 0.7152 * channel(2) + 0.0722 * channel(4);
    }
}
