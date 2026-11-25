using System;
using System.Collections.Generic;
using System.Linq;
using RuAgent.Harness.Core;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

/// <summary>
/// The stream behind the running thinking row: it publishes the absolute summary line
/// only when that line changes, keeps the reasoning tail bounded, and never lets a
/// secret split across deltas reach the wire.
/// </summary>
public sealed class ReasoningPreviewStreamTests
{
    private static readonly RunId Run = new("run_" + new string('b', 32));
    private static readonly ModelStepId Step = new("step_" + new string('a', 32));
    private static readonly string ActivityId = "act_" + new string('a', 32);

    private static ReasoningPreviewStream NewStream(List<HarnessTransientFrame> frames) =>
        new(new TransientFrameBuffer(32, frames.Add), new NoRedactionRedactor(), Run, Step, ActivityId);

    [Fact]
    public void Publishes_the_absolute_line_only_when_it_changes()
    {
        var frames = new List<HarnessTransientFrame>();
        var stream = NewStream(frames);

        stream.Append("First line\n");
        stream.Append("Second line\n");
        Assert.Single(frames);
        Assert.Equal("First line", frames[0].Content);
        Assert.Equal(TransientFrameKind.ReasoningPreview, frames[0].Kind);
        Assert.Equal(ActivityId, frames[0].ActivityId);
        Assert.Equal(Step, frames[0].ModelStepId);

        // Same paragraph, same first line: nothing to publish.
        stream.Append("Still second\nand more\n");
        Assert.Single(frames);

        stream.Append("\nThird paragraph\n");
        Assert.Equal(2, frames.Count);
        Assert.Equal("Third paragraph", frames[^1].Content);
        Assert.True(frames[^1].Sequence > frames[0].Sequence);
    }

    [Fact]
    public void A_leading_blank_line_moves_the_line_to_the_next_paragraph()
    {
        var frames = new List<HarnessTransientFrame>();
        var stream = NewStream(frames);

        stream.Append("First line\n");
        stream.Append("\nSecond paragraph\n");

        Assert.Equal(2, frames.Count);
        Assert.Equal("First line", frames[0].Content);
        Assert.Equal("Second paragraph", frames[1].Content);
    }

    [Fact]
    public void An_empty_delta_publishes_nothing()
    {
        var frames = new List<HarnessTransientFrame>();
        var stream = NewStream(frames);

        stream.Append(string.Empty);

        Assert.Empty(frames);
    }

    [Fact]
    public void Reset_with_nothing_published_publishes_no_frame()
    {
        // A step that failed before any reasoning line streamed has nothing to clear;
        // an empty reset frame would only perturb observers that count frames.
        var frames = new List<HarnessTransientFrame>();
        var stream = NewStream(frames);

        stream.Reset();

        Assert.Empty(frames);
    }

    [Fact]
    public void Reset_clears_the_line_and_the_next_paragraph_publishes_again()
    {
        var frames = new List<HarnessTransientFrame>();
        var stream = NewStream(frames);
        stream.Append("Old attempt\n");

        stream.Reset();

        Assert.True(frames[^1].IsReset);
        Assert.Equal(string.Empty, frames[^1].Content);
        Assert.Equal(ActivityId, frames[^1].ActivityId);

        stream.Append("Retry line\n");
        Assert.Equal("Retry line", frames[^1].Content);
        Assert.False(frames[^1].IsReset);
    }

    [Fact]
    public void Never_publishes_a_secret_split_across_deltas()
    {
        var frames = new List<HarnessTransientFrame>();
        var stream = new ReasoningPreviewStream(
            new TransientFrameBuffer(32, frames.Add), new HoldbackRedactor("split-secret"), Run, Step, ActivityId);

        stream.Append("try split-");
        stream.Append("secret here\nnext\n");

        var published = string.Concat(frames.Select(frame => frame.Content));
        Assert.DoesNotContain("split-secret", published, StringComparison.Ordinal);
        Assert.Contains("[secret]", published, StringComparison.Ordinal);
    }

    [Fact]
    public void Keeps_only_the_tracked_tail_of_a_long_reasoning_stream()
    {
        var frames = new List<HarnessTransientFrame>();
        var stream = NewStream(frames);
        stream.Append(new string('a', 5_000) + "\n\n");
        Assert.StartsWith("aaaa", frames[^1].Content, StringComparison.Ordinal);

        stream.Append(new string('b', 5_000) + "\n");

        Assert.Equal(2, frames.Count);
        Assert.StartsWith("bbbb", frames[^1].Content, StringComparison.Ordinal);
        Assert.True(frames[^1].Content.Length <= ReasoningPreview.MaximumCharacters);
    }

    private sealed class NoRedactionRedactor : ITransientContentRedactor
    {
        public ITransientContentRedactionSession CreateSession() => new Session();

        private sealed class Session : ITransientContentRedactionSession
        {
            public string Append(string content) => content;

            public string Complete() => string.Empty;
        }
    }

    /// <summary>Holds back the longest suffix that is still a prefix of the secret, like the real redactor.</summary>
    private sealed class HoldbackRedactor(string secret) : ITransientContentRedactor
    {
        public ITransientContentRedactionSession CreateSession() => new Session(secret);

        private sealed class Session(string secret) : ITransientContentRedactionSession
        {
            private string _buffer = string.Empty;

            public string Append(string content)
            {
                _buffer += content;
                var hold = 0;
                for (var length = Math.Min(secret.Length, _buffer.Length); length > 0; length--)
                {
                    if (_buffer.AsSpan(_buffer.Length - length).SequenceEqual(secret.AsSpan(0, length)))
                    {
                        hold = length;
                        break;
                    }
                }

                var emit = _buffer[..(_buffer.Length - hold)];
                _buffer = _buffer[^hold..];
                return emit.Replace(secret, "[secret]", StringComparison.Ordinal);
            }

            public string Complete() => _buffer;
        }
    }
}
