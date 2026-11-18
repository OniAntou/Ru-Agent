using RuAgent.Protocol.Harness;
using System.Text.Json;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed class PublicHostSessionTextTests
{
    private const string SyntheticBearer = "synthetic-bearer-value-0123456789";

    private static RuntimeDescriptor Descriptor() => new(
        RuntimeDescriptor.CurrentProtocolVersion,
        "http://127.0.0.1:41000/",
        SyntheticBearer,
        1234,
        "2026-11-18T00:00:00Z",
        "nonce",
        "2026-11-18T00:00:00Z");

    [Fact]
    public void Record_text_redacts_the_bearer()
    {
        var descriptor = Descriptor();
        var session = PublicHostSession.FromDescriptor(descriptor);

        Assert.DoesNotContain(SyntheticBearer, session.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticBearer, descriptor.ToString(), StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:41000", session.ToString(), StringComparison.Ordinal);
        Assert.Contains("<redacted>", descriptor.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Descriptor_serialization_still_carries_the_bearer()
    {
        var json = JsonSerializer.Serialize(Descriptor(), RuntimeDescriptor.JsonOptions);

        Assert.Contains(SyntheticBearer, json, StringComparison.Ordinal);
    }
}
