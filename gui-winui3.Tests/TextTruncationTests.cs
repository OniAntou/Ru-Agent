using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Display truncation must not cut a surrogate pair in half: an unpaired half renders
/// as the replacement glyph in front of the ellipsis.
/// </summary>
public sealed class TextTruncationTests
{
    private const string Astral = "\U0001F600";

    [Fact]
    public void A_cut_between_the_halves_of_a_pair_steps_back_to_its_start()
    {
        string text = new string('a', 46) + Astral + "b";

        string cut = TextTruncation.TakeAtMost(text, 47);

        Assert.Equal(new string('a', 46), cut);
        Assert.All(cut, character => Assert.False(char.IsSurrogate(character)));
    }

    [Fact]
    public void A_cut_after_a_pair_keeps_the_whole_pair()
    {
        string text = new string('a', 46) + Astral + "b";

        Assert.Equal(new string('a', 46) + Astral, TextTruncation.TakeAtMost(text, 48));
    }

    [Fact]
    public void A_budget_that_already_covers_the_text_returns_it_unchanged()
    {
        string text = new string('a', 10) + Astral;

        Assert.Same(text, TextTruncation.TakeAtMost(text, text.Length));
        Assert.Equal(string.Empty, TextTruncation.TakeAtMost(text, 0));
    }
}
