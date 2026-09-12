using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Visual polish contracts: muted text must clear WCAG AA (4.5:1) on every
/// theme surface, spacing follows the 4/8/16 rhythm, and the activity wave
/// stays throttled and motion-respecting.
/// </summary>
public sealed class VisualPolishContractTests
{
    [Fact]
    public void Light_muted_text_passes_aa_on_canvas_and_sidebar()
    {
        var appXaml = File.ReadAllText(FindSource("App.xaml"));
        var light = ThemeBlock(appXaml, "Light");

        string muted = ColorValue(light, "RuMutedColor");
        Assert.True(Contrast(muted, ColorValue(light, "RuSurfaceColor")) >= 4.5,
            $"Muted {muted} must clear 4.5:1 on surface.");
        Assert.True(Contrast(muted, ColorValue(light, "RuSidebarColor")) >= 4.5,
            $"Muted {muted} must clear 4.5:1 on sidebar.");
    }

    [Fact]
    public void Dark_muted_text_passes_aa_on_surface()
    {
        var appXaml = File.ReadAllText(FindSource("App.xaml"));
        var dark = ThemeBlock(appXaml, "Dark");

        Assert.True(Contrast(ColorValue(dark, "RuMutedColor"), ColorValue(dark, "RuSurfaceColor")) >= 4.5,
            "Dark muted must clear 4.5:1 on surface.");
    }

    [Fact]
    public void Accent_hue_tokens_pass_aa_on_light_surfaces()
    {
        var appXaml = File.ReadAllText(FindSource("App.xaml"));
        var light = ThemeBlock(appXaml, "Light");
        string surface = ColorValue(light, "RuSurfaceColor");

        foreach (var token in new[] { "RuInfoColor", "RuVioletColor", "RuPinkColor", "RuDangerColor" })
        {
            Assert.True(Contrast(ColorValue(light, token), surface) >= 4.5,
                $"{token} must clear 4.5:1 on light surface.");
        }
    }

    [Fact]
    public void Status_text_never_dims_muted_foreground_with_opacity()
    {
        var chatXaml = File.ReadAllText(FindSource("Views", "ChatView.xaml"));

        // Muted-on-surface already sits near the AA floor; extra opacity
        // would push small text below 4.5:1 on the light theme.
        foreach (Match match in Regex.Matches(chatXaml, @"<TextBlock[^>]*>"))
        {
            string tag = match.Value;
            if (tag.Contains("RuMutedBrush", StringComparison.Ordinal))
            {
                Assert.DoesNotContain("Opacity=", tag, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Diagnostic_surfaces_use_theme_brushes_not_hardcoded_colors()
    {
        var diagnostics = File.ReadAllText(FindSource("Views", "DiagnosticsView.xaml"));
        var log = File.ReadAllText(FindSource("Views", "LogView.xaml"));

        // Opaque hardcoded foregrounds break one of the two themes; the only
        // exception is white on opaque saturated badge pills. Translucent
        // 8-digit tint overlays are theme-neutral by construction.
        foreach (var (name, xaml) in new[] { ("Diagnostics", diagnostics), ("Log", log) })
        {
            foreach (Match match in Regex.Matches(xaml, @"Foreground=""(#[0-9A-Fa-f]{6})"""))
            {
                Assert.True(
                    string.Equals(match.Groups[1].Value, "#FFFFFF", StringComparison.OrdinalIgnoreCase),
                    $"{name} must not use opaque hardcoded text {match.Groups[1].Value}.");
            }

            Assert.DoesNotContain("#141418", xaml, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("RuDangerBrush", diagnostics, StringComparison.Ordinal);
        Assert.Contains("RuInfoBrush", diagnostics, StringComparison.Ordinal);
        Assert.Contains("RuInfoBrush", log, StringComparison.Ordinal);
        Assert.Contains("RuVioletBrush", log, StringComparison.Ordinal);
        Assert.Contains("RuPinkBrush", log, StringComparison.Ordinal);
    }

    [Fact]
    public void Spacing_follows_the_rhythm_scale()
    {
        foreach (var view in new[] { "ChatView.xaml", "SettingsView.xaml", "VoiceView.xaml", "DiagnosticsView.xaml", "LogView.xaml" })
        {
            var xaml = File.ReadAllText(FindSource("Views", view));
            foreach (var off in new[] { "Spacing=\"20\"", "Spacing=\"7\"", "Spacing=\"10\"", "Spacing=\"14\"" })
            {
                Assert.DoesNotContain(off, xaml, StringComparison.Ordinal);
            }
        }

        var settings = File.ReadAllText(FindSource("Views", "SettingsView.xaml"));
        Assert.DoesNotContain("0,0,10,0", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("0,0,0,20", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void Wave_stays_throttled_and_motion_respecting()
    {
        var timing = File.ReadAllText(FindSource("Services", "UiTiming.cs"));
        var codeBehind = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));

        var wave = Regex.Match(timing, @"WaveFrame = TimeSpan\.FromMilliseconds\((\d+)\)");
        Assert.True(wave.Success, "WaveFrame must stay a named UiTiming constant.");
        Assert.True(int.Parse(wave.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) >= 40,
            "WaveFrame must stay throttled at 40ms or slower.");

        Assert.Contains("!Services.UiTiming.AnimationsEnabled", codeBehind, StringComparison.Ordinal);
    }

    private static string ThemeBlock(string appXaml, string theme)
    {
        var start = appXaml.IndexOf($"<ResourceDictionary x:Key=\"{theme}\">", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing {theme} theme block.");
        var end = appXaml.IndexOf("</ResourceDictionary>", start, StringComparison.Ordinal);
        return appXaml[start..end];
    }

    private static string ColorValue(string themeBlock, string key)
    {
        var match = Regex.Match(themeBlock, $"<Color x:Key=\"{key}\">([^<]+)</Color>");
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

    private static string FindSource(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string sourceRoot = Path.Combine(directory.FullName, "gui-winui3");
            if (Directory.Exists(sourceRoot))
            {
                return Path.Combine(new[] { sourceRoot }.Concat(parts).ToArray());
            }
        }

        throw new DirectoryNotFoundException("gui-winui3 source root was not found.");
    }
}
