using RuAgent.Harness.Core;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

/// <summary>
/// The run's terminal state is durable and may be read by another build, so the
/// fingerprint is a validated shape, never free text: no message, no path, no secret.
/// </summary>
public sealed class RunFailureFingerprintTests
{
    [Fact]
    public void A_bounded_kind_and_site_round_trip()
    {
        var fingerprint = new RunFailureFingerprint(
            "store", "Microsoft.Data.Sqlite.SqliteCommand.ExecuteReader");

        Assert.Equal("store", fingerprint.Kind);
        Assert.Equal("Microsoft.Data.Sqlite.SqliteCommand.ExecuteReader", fingerprint.Site);
    }

    [Fact]
    public void A_kind_without_a_site_is_still_a_fingerprint()
    {
        var fingerprint = new RunFailureFingerprint("timeout", site: " ");

        Assert.Equal("timeout", fingerprint.Kind);
        Assert.Null(fingerprint.Site);
    }

    [Theory]
    [InlineData("Store")]
    [InlineData("1store")]
    [InlineData("sto re")]
    [InlineData("store!")]
    [InlineData("")]
    public void A_kind_outside_the_token_grammar_is_refused(string kind)
    {
        Assert.ThrowsAny<ArgumentException>(() => new RunFailureFingerprint(kind));
    }

    [Theory]
    [InlineData(@"C:\Users\someone\report.docx")]
    [InlineData("a message with spaces")]
    [InlineData("line\nbreak")]
    [InlineData("sk-0123456789abcdef")]
    public void A_site_outside_the_type_and_method_grammar_is_refused(string site)
    {
        Assert.Throws<ArgumentException>(() => new RunFailureFingerprint("store", site));
    }

    [Fact]
    public void Both_fields_are_bounded()
    {
        Assert.Throws<ArgumentException>(() => new RunFailureFingerprint(new string('k', 33)));
        Assert.Throws<ArgumentException>(() => new RunFailureFingerprint("store", new string('s', 161)));
    }
}
