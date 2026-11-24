using Xunit;
using RuAgent.Harness.Persistence.Privacy;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class KnownSecretRedactorTests
{
    [Fact]
    public void Streaming_redactor_detects_secrets_split_across_chunk_boundaries()
    {
        var redactor = new KnownSecretRedactor(["alpha-secret-omega"]);
        var stream = redactor.CreateSession();

        var output = string.Concat(
            stream.Append("before alpha-se"),
            stream.Append("cret-om"),
            stream.Append("ega after"),
            stream.Complete());

        Assert.Equal($"before {KnownSecretRedactor.Replacement} after", output);
        Assert.DoesNotContain("alpha-secret-omega", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_streamed_line_that_cannot_start_a_secret_is_released_at_once()
    {
        // A configured provider key used to hold back the last (key length - 1)
        // characters of every chunk, so a command's newest line showed up a line late.
        var redactor = new KnownSecretRedactor(["sk-live-0123456789abcdefghijklmnop"]);
        var stream = redactor.CreateSession();

        Assert.Equal("[7/10] compiling module_7.py ...\r\n", stream.Append("[7/10] compiling module_7.py ...\r\n"));
        Assert.Equal("[8/10] compiling", stream.Append("[8/10] compiling"));
        Assert.Equal(string.Empty, stream.Complete());
    }

    [Fact]
    public void Only_a_run_that_could_still_become_a_secret_is_held()
    {
        var redactor = new KnownSecretRedactor(["sk-live-0123456789abcdefghijklmnop"]);
        var stream = redactor.CreateSession();

        Assert.Equal("key=", stream.Append("key=sk-live-0123"));
        Assert.Equal(KnownSecretRedactor.Replacement + " done", stream.Append("456789abcdefghijklmnop done"));
    }

    [Fact]
    public void A_complete_secret_that_may_grow_into_a_longer_one_is_held_until_it_cannot()
    {
        var redactor = new KnownSecretRedactor(["token-123456", "token-123456-extended"]);
        var stream = redactor.CreateSession();

        Assert.Equal("x ", stream.Append("x token-123456"));
        var output = string.Concat(stream.Append("-extended y"), stream.Complete());

        Assert.Equal($"{KnownSecretRedactor.Replacement} y", output);
        Assert.DoesNotContain("extended", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Longest_known_secret_wins_when_values_overlap()
    {
        var redactor = new KnownSecretRedactor(["token", "token-long"]);

        var output = redactor.Redact("token-long token");

        Assert.Equal(
            $"{KnownSecretRedactor.Replacement} {KnownSecretRedactor.Replacement}",
            output);
    }

    [Fact]
    public void Unknown_arbitrary_project_values_are_not_promised_detectable()
    {
        var redactor = new KnownSecretRedactor(["known-secret"]);

        var output = redactor.Redact("unknown-project-value");

        Assert.Equal("unknown-project-value", output);
    }

    [Fact]
    public void Large_non_secret_text_is_processed_with_bounded_allocations()
    {
        var redactor = new KnownSecretRedactor(["known-secret-value"]);
        _ = redactor.Redact("warmup");
        var input = new string('x', 16 * 1024);
        var before = GC.GetAllocatedBytesForCurrentThread();

        var output = redactor.Redact(input);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(input, output);
        Assert.True(
            allocated < input.Length * 128L,
            $"Redaction allocated {allocated} bytes for {input.Length} input characters.");
    }

    [Fact]
    public void A_stored_key_added_later_is_redacted_from_then_on()
    {
        var redactor = new KnownSecretRedactor(["environment-secret"]);
        Assert.Equal("sk-stored-provider-key", redactor.Redact("sk-stored-provider-key"));

        redactor.AddStoredSecrets(["sk-stored-provider-key"]);

        Assert.Equal(
            $"{KnownSecretRedactor.Replacement} {KnownSecretRedactor.Replacement}",
            redactor.Redact("sk-stored-provider-key environment-secret"));
    }

    [Fact]
    public void Short_or_blank_stored_values_are_ignored_and_the_shared_empty_redactor_never_learns()
    {
        var redactor = new KnownSecretRedactor([]);
        redactor.AddStoredSecrets(["short", "            ", null]);
        Assert.Equal("short", redactor.Redact("short"));

        KnownSecretRedactor.Empty.AddStoredSecrets(["sk-stored-provider-key"]);
        Assert.Equal("sk-stored-provider-key", KnownSecretRedactor.Empty.Redact("sk-stored-provider-key"));
    }

    [Fact]
    public void Stored_seeds_stop_at_the_bound_instead_of_throwing()
    {
        var redactor = new KnownSecretRedactor([]);
        redactor.AddStoredSecrets(Enumerable.Range(0, 300).Select(i => $"sk-stored-key-{i:000}"));
        Assert.Equal(KnownSecretRedactor.Replacement, redactor.Redact("sk-stored-key-000"));
        Assert.Equal("sk-stored-key-299", redactor.Redact("sk-stored-key-299"));
    }
}
