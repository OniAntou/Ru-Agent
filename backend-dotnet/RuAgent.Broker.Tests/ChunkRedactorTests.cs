using System.Text;
using RuAgent.Broker.Tools.Terminal;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class ChunkRedactorTests
{
    [Fact]
    public void Secret_crossing_emission_boundary_is_redacted_as_a_whole()
    {
        const string key = "synthetic-api-key-12345";
        var redactor = new ChunkRedactor([key]);
        var output = redactor.Push("before:" + key + ":after") + redactor.Flush();

        Assert.Equal("before:[redacted]:after", output);
        Assert.True(redactor.RedactedAnything);
    }

    [Fact]
    public void Every_chunk_split_produces_the_same_redacted_text()
    {
        const string key = "synthetic-api-key-12345";
        var input = "before:" + key + ":middle:" + key + ":after";
        for (var split = 1; split < input.Length; split++)
        {
            var redactor = new ChunkRedactor([key]);
            var output = redactor.Push(input[..split]) + redactor.Push(input[split..]) + redactor.Flush();
            Assert.Equal("before:[redacted]:middle:[redacted]:after", output);
        }
    }

    [Fact]
    public void Overlapping_keys_at_different_positions_redact_the_complete_union()
    {
        var redactor = new ChunkRedactor(["abc", "bcd", "cdef"]);
        Assert.Equal("[redacted]!", redactor.Push("abcdef!") + redactor.Flush());
    }

    [Fact]
    public void Overlapping_keys_at_the_same_position_redact_the_longest()
    {
        var redactor = new ChunkRedactor(["key", "key-long-value"]);
        Assert.Equal("[redacted]:tail", redactor.Push("key-long-value:tail") + redactor.Flush());
    }

    [Fact]
    public void Short_output_is_emitted_immediately_even_with_a_long_secret()
    {
        // Regression: a fixed (longest seed - 1) hold-back withheld every short
        // line until the process exited, so the live tool-output stream starved.
        var redactor = new ChunkRedactor(["synthetic-api-key-1234567890abcdef"]);
        Assert.Equal("live-marker\n", redactor.Push("live-marker\n"));
        Assert.Equal(string.Empty, redactor.Flush());
        Assert.False(redactor.RedactedAnything);
    }

    [Fact]
    public void A_secret_longer_than_any_chunk_is_withheld_until_it_completes()
    {
        // Regression: a fixed maximum hold emitted the head of a long credential
        // and the remainder could no longer match. Every prefix must stay pending.
        var secret = "sk-" + string.Concat(Enumerable.Repeat("0123456789abcdef", 320));
        Assert.True(secret.Length > 5_000);
        var redactor = new ChunkRedactor([secret]);
        var emitted = new StringBuilder();
        emitted.Append(redactor.Push("before:"));
        for (var position = 0; position < secret.Length; position += 997)
        {
            var piece = secret.Substring(position, Math.Min(997, secret.Length - position));
            emitted.Append(redactor.Push(piece));
        }

        emitted.Append(redactor.Push(":after"));
        emitted.Append(redactor.Flush());

        Assert.Equal("before:[redacted]:after", emitted.ToString());
        Assert.True(redactor.RedactedAnything);
    }

    [Fact]
    public void Overlapping_secrets_across_a_chunk_boundary_do_not_leak_the_second_tail()
    {
        // "xyz" ends inside "yzQQQ". When the first match reaches into the held
        // tail, emitting its replacement would consume the start of the second
        // secret and stream "QQQ" unredacted.
        var redactor = new ChunkRedactor(["xyz", "yzQQQ"]);
        var first = redactor.Push("xyzQ");
        var second = redactor.Push("QQ") + redactor.Flush();

        Assert.Equal(string.Empty, first);
        Assert.Equal("[redacted]", first + second);
        Assert.DoesNotContain("QQQ", first + second, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_a_tail_that_could_become_a_secret_is_withheld()
    {
        const string key = "synthetic-api-key-12345";
        var redactor = new ChunkRedactor([key]);
        Assert.Equal("before:", redactor.Push("before:"));
        Assert.Equal(string.Empty, redactor.Push(key[..5]));
        Assert.Equal("[redacted]:after", redactor.Push(key[5..] + ":after") + redactor.Flush());
        Assert.True(redactor.RedactedAnything);
    }
}
