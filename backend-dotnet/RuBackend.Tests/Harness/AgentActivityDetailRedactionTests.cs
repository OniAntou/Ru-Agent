using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Protocol.Harness;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>Replay and the live feed share one redaction of a row's detail.</summary>
public sealed class AgentActivityDetailRedactionTests
{
    private const string Secret = "sk-synthetic-secret-123456";
    private static readonly KnownSecretRedactor Secrets = new([Secret]);

    [Fact]
    public void A_command_detail_loses_a_known_secret()
    {
        var redacted = AgentActivityDetailRedaction.Redact($"curl -H \"Authorization: Bearer {Secret}\" https://api", Secrets);

        Assert.DoesNotContain(Secret, redacted);
        Assert.Contains(KnownSecretRedactor.Replacement, redacted);
    }

    [Fact]
    public void An_edit_preview_is_redacted_inside_its_encoding()
    {
        var redacted = AgentActivityDetailRedaction.Redact(AgentEditPreviewCodec.Encode("old", "key = " + Secret), Secrets);

        Assert.True(AgentEditPreviewCodec.TryDecode(redacted, out var before, out var after));
        Assert.Equal("old", before);
        Assert.DoesNotContain(Secret, after);
    }

    [Fact]
    public void An_empty_detail_stays_empty()
    {
        Assert.Null(AgentActivityDetailRedaction.Redact(null, Secrets));
        Assert.Equal("", AgentActivityDetailRedaction.Redact("", Secrets));
    }
}
