using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class StartupWordmarkWaveTests
{
    private static readonly StartupWordmarkWave.Rgb Ink = new(238, 238, 238);
    private static readonly StartupWordmarkWave.Rgb Green = new(74, 222, 128);
    private static readonly StartupWordmarkWave.Rgb Blue = new(56, 189, 248);

    private static StartupWordmarkWave.Rgb At(int letter, double seconds) =>
        StartupWordmarkWave.ColorAt(letter, seconds, Ink, Green, Blue);

    [Fact]
    public void The_wave_starts_on_ink_and_reaches_green_and_blue_in_one_period()
    {
        Assert.Equal(Ink, At(0, 0));
        Assert.Equal(Green, At(0, StartupWordmarkWave.PeriodSeconds / 3));
        Assert.Equal(Blue, At(0, StartupWordmarkWave.PeriodSeconds * 2 / 3));
    }

    [Fact]
    public void The_wave_loops_and_survives_bad_input()
    {
        Assert.Equal(At(2, 1.25), At(2, 1.25 + StartupWordmarkWave.PeriodSeconds));
        Assert.Equal(At(0, 0), At(0, double.NaN));
        Assert.Equal(At(0, 0), At(0, double.PositiveInfinity));
    }

    [Fact]
    public void Neighbouring_letters_sit_at_different_points_so_the_colour_travels()
    {
        Assert.NotEqual(At(0, 1.0), At(1, 1.0));
        Assert.NotEqual(At(3, 1.0), At(4, 1.0));
        // The same colour reaches the next letter a moment later.
        double delay = StartupWordmarkWave.LetterOffset * StartupWordmarkWave.PeriodSeconds;
        Assert.Equal(At(0, 1.0), At(1, 1.0 + delay));
    }

    [Fact]
    public void The_overlay_writes_out_Ru_Agent_and_rests_on_fixed_colours_when_not_loading()
    {
        var source = GuiSource.ViewText("WelcomeOverlay");
        Assert.Contains("\"Ru Agent\"", source, StringComparison.Ordinal);
        string render = GuiSource.MethodBody(source, "RenderWordmarkWave");
        Assert.Contains("_isReady", render, StringComparison.Ordinal);
        Assert.Contains("_hasError", render, StringComparison.Ordinal);
        Assert.Contains("!_motionEnabled", render, StringComparison.Ordinal);
        Assert.Contains("RenderWordmarkWave", GuiSource.MethodBody(source, "RenderFrame"), StringComparison.Ordinal);
        // The backdrop is the meteor shower, which stops when loading ends in an error.
        Assert.Contains("MeteorShowerField", source, StringComparison.Ordinal);
        string sync = GuiSource.MethodBody(source, "SyncMeteors");
        Assert.Contains("UiTiming.AnimationsEnabled", sync, StringComparison.Ordinal);
        Assert.Contains("_hasError", sync, StringComparison.Ordinal);
        Assert.DoesNotContain("Braille", source, StringComparison.Ordinal);
    }
}