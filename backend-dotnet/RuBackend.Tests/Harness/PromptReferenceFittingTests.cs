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
    public void Oversized_content_keeps_the_largest_prefix_that_fits()
    {
        // Bisection replaced a scan that re-encoded the whole prefix per character. Both find
        // the boundary; this pins that the boundary is still the LAST character that fits, so
        // a future change cannot quietly shorten the content the model receives.
        string content = new('x', PromptReference.ContentMaxBytes * 3 + 7);

        string fitted = RuPromptContextProvider.FitReferenceContent(content);

        // The note is appended after the kept prefix; splitting there recovers both halves.
        int noteStart = fitted.IndexOf('\n');
        Assert.True(noteStart > 0, "a truncated result must carry the note after its content");
        int kept = noteStart;
        int fittedBytes = Encoding.UTF8.GetByteCount(fitted);
        int noteBytes = Encoding.UTF8.GetByteCount(fitted.AsSpan(noteStart));
        Assert.Equal(kept, fitted.AsSpan(0, noteStart).Count('x'));
        Assert.True(
            fittedBytes <= PromptReference.ContentMaxBytes,
            "the fitted content must fit the bound a reference accepts");
        // One more character would not have fit: the bisection found the boundary, not a
        // midpoint short of it.
        Assert.True(
            kept + 1 + noteBytes > PromptReference.ContentMaxBytes,
            $"the cut kept {kept} characters, but one more would still have fit");
    }

    [Fact]
    public void Truncating_a_large_result_stays_fast()
    {
        // One character at a time re-encoded the whole prefix on every step: a 4 MiB tool
        // result -- the documented output cap -- took roughly eight minutes inside a
        // synchronous host call. The generous ceiling here only has to catch a return to
        // that behaviour, not measure the machine.
        string content = new('x', 4 * 1024 * 1024);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        string fitted = RuPromptContextProvider.FitReferenceContent(content);
        stopwatch.Stop();

        Assert.True(Encoding.UTF8.GetByteCount(fitted) <= PromptReference.ContentMaxBytes);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"fitting a 4 MiB result took {stopwatch.Elapsed.TotalSeconds:F1}s");
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
