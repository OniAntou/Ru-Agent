using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class KeyInputPanelTests : IDisposable
{
    private readonly IDisposable _levelPin;

    public KeyInputPanelTests()
    {
        _levelPin = TuiPalette.PinLevel(TuiColorLevel.None);
    }

    public void Dispose() => _levelPin.Dispose();

    [Theory]
    [InlineData(24)]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(120)]
    public void Compose_FillsExactlyTheViewportWidth(int width)
    {
        var lines = KeyInputPanel.Compose(width, "models.dev:openrouter", 7);

        Assert.Equal(3, lines.Count);
        Assert.All(lines, line => Assert.Equal(width, TuiText.VisibleWidth(line)));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(12, 12)]
    [InlineData(5000, 74)]
    public void MaskRow_ShowsOneBulletPerCharCappedToTheFrame(int bullets, int expected)
    {
        var row = TuiText.Strip(KeyInputPanel.ComposeMaskRow(80, bullets));

        Assert.Equal(expected, row.Count(c => c == KeyInputPanel.MaskGlyph));
    }

    [Fact]
    public void Compose_NamesProviderAndHintsWithoutSecretContent()
    {
        var text = string.Join("\n", KeyInputPanel.Compose(80, "models.dev:openrouter", 9));

        Assert.Contains("openrouter", text, StringComparison.Ordinal);
        Assert.Contains("esc", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_TrimsALongProviderButKeepsTheFrame()
    {
        var lines = KeyInputPanel.Compose(40, "models.dev:a-very-long-provider-id-that-overflows", 3);

        Assert.All(lines, line => Assert.Equal(40, TuiText.VisibleWidth(line)));
    }
}
