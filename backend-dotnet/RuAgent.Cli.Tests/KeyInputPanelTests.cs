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
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(5000)]
    public void MaskText_ShowsOneBulletPerCharAndACaret(int bullets)
    {
        var mask = KeyInputPanel.MaskText(bullets);

        Assert.Equal(bullets, mask.Count(c => c == KeyInputPanel.MaskGlyph));
        Assert.EndsWith(KeyInputPanel.Caret.ToString(), mask, StringComparison.Ordinal);
    }

    [Fact]
    public void CardTitle_NamesTheProvider()
    {
        Assert.Equal("api key · models.dev:openrouter", KeyInputPanel.CardTitle("models.dev:openrouter"));
        Assert.Equal("api key · provider", KeyInputPanel.CardTitle("  "));
    }

    [Fact]
    public void CardBody_IsTheMaskedFieldAndCarriesNoSecretContent()
    {
        var body = KeyInputPanel.CardBody(9);

        var row = Assert.Single(body);
        Assert.Equal(CardRowKind.Field, row.Kind);
        Assert.Equal(KeyInputPanel.MaskText(9), row.Label);
        Assert.DoesNotContain("sk-", row.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void Card_TruncatesALongMaskToTheCardWidthWithoutLosingTheCaretContract()
    {
        var lines = TuiCard.Compose(
            40,
            KeyInputPanel.CardTitle("models.dev:a-very-long-provider-id-that-overflows"),
            "esc",
            KeyInputPanel.CardBody(5000),
            "ctrl+v paste · enter save · esc cancel");

        Assert.All(lines, line => Assert.Equal(40, TuiText.VisibleWidth(line.Text)));
        Assert.Contains(lines, line => line.Text.Contains(KeyInputPanel.Caret, StringComparison.Ordinal));
    }
}
