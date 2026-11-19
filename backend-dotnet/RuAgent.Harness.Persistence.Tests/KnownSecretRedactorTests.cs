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
