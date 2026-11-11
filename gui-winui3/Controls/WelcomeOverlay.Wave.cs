using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using RuApp.Services;
using Windows.UI;

namespace RuApp.Controls;

/// <summary>
/// "Ru Agent" is written out in Inter, and while the app loads a colour wave travels through
/// its letters. Ready, error and reduced motion all fall back to the resting colours.
/// </summary>
public sealed partial class WelcomeOverlay
{
    private const string WordmarkText = "Ru Agent";
    private readonly List<Run> _letters = new();
    private bool _waveShown;

    private TextBlock BuildWordmark(FontFamily font)
    {
        var block = new TextBlock { FontFamily = font, FontSize = 52, CharacterSpacing = -28 };
        for (int i = 0; i < WordmarkText.Length; i++)
        {
            var run = new Run
            {
                Text = WordmarkText[i].ToString(),
                FontWeight = i < 2 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            };
            block.Inlines.Add(run);
            _letters.Add(run);
        }

        return block;
    }

    /// <summary>Resting colours: Ru in the brand green, Agent in ink; grey after an error.</summary>
    private void ApplyWordmarkResting()
    {
        _waveShown = false;
        for (int i = 0; i < _letters.Count; i++)
        {
            string key = _hasError ? "RuMutedBrush" : i < 2 ? "RuBrandGreenBrush" : "RuInkBrush";
            _letters[i].Foreground = ThemeBrush(key);
        }
    }

    private void RenderWordmarkWave(double seconds)
    {
        if (_letters.Count == 0) return;
        if (!_motionEnabled || _isReady || _hasError)
        {
            if (_waveShown || _letters[0].Foreground is null) ApplyWordmarkResting();
            return;
        }

        _waveShown = true;
        var ink = ToRgb("RuInkBrush");
        var green = ToRgb("RuBrandGreenBrush");
        var blue = ToRgb("RuInfoBrush");
        for (int i = 0; i < _letters.Count; i++)
        {
            var c = StartupWordmarkWave.ColorAt(i, seconds, ink, green, blue);
            _letters[i].Foreground = new SolidColorBrush(Color.FromArgb(255, c.R, c.G, c.B));
        }
    }

    private static StartupWordmarkWave.Rgb ToRgb(string key)
    {
        var color = ((SolidColorBrush)ThemeBrush(key)).Color;
        return new StartupWordmarkWave.Rgb(color.R, color.G, color.B);
    }
}