using System.Text;
using RuAgent.Harness.Core.Prompting;
using RuBackend.Harness.Prompting;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// Tool output has to fit in a prompt reference, or the run ends.
/// </summary>
/// <remarks>
/// A reference over its bound is rejected, and a rejected reference fails the prompt, which
/// fails the run. So one large listing or file read used to end the turn outright -- not
/// because the model misbehaved, but because the product had nowhere to put the result.
/// </remarks>
public sealed class PromptReferenceFittingTests
{
    [Fact]
    public void Content_that_fits_is_returned_unchanged()
    {
        const string content = "a short tool result";

        Assert.Equal(content, RuPromptContextProvider.FitReferenceContent(content));
    }

    [Fact]
    public void Content_exactly_at_the_bound_is_returned_unchanged()
    {
        // The bound is inclusive, so content that exactly fits must not be marked truncated.
        string content = new('x', PromptReference.ContentMaxBytes);

        Assert.Equal(content, RuPromptContextProvider.FitReferenceContent(content));
    }

    [Fact]
    public void Oversized_content_is_shortened_instead_of_rejected()
    {
        string content = new('x', PromptReference.ContentMaxBytes * 3);

        string fitted = RuPromptContextProvider.FitReferenceContent(content);

        Assert.True(
            Encoding.UTF8.GetByteCount(fitted) <= PromptReference.ContentMaxBytes,
            "the fitted content must fit the bound a reference accepts");
    }

    [Fact]
    public void The_result_still_builds_a_reference()
    {
        // The point of the exercise: the constructor no longer throws.
        string fitted = RuPromptContextProvider.FitReferenceContent(
            new string('x', PromptReference.ContentMaxBytes * 4));

        var reference = new PromptReference("tool_result", "untrusted tool result", fitted);

        Assert.NotEmpty(reference.Content);
    }

    [Fact]
    public void A_truncated_result_says_that_it_was_truncated()
    {
        // A silently shortened listing reads as a complete one, and the model would draw
        // conclusions from half the files without knowing half were missing.
        string fitted = RuPromptContextProvider.FitReferenceContent(
            new string('x', PromptReference.ContentMaxBytes * 2));

        Assert.Contains("truncated", fitted, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("allows)", fitted, StringComparison.Ordinal);
    }

    [Fact]
    public void Multi_byte_text_is_cut_on_a_character_boundary()
    {
        // Vietnamese is three bytes a character in UTF-8, so a byte budget is well under the
        // character count and a naive cut would split a character into invalid text.
        string content = string.Concat(Enumerable.Repeat("Tiếng Việt có dấu. ", 4000));

        string fitted = RuPromptContextProvider.FitReferenceContent(content);

        Assert.True(Encoding.UTF8.GetByteCount(fitted) <= PromptReference.ContentMaxBytes);
        Assert.DoesNotContain('\uFFFD', fitted);

        // Round-tripping proves the cut did not land inside a character.
        Assert.Equal(fitted, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(fitted)));
    }
}
