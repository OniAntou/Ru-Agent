using System.Text;
using RuAgent.Broker.Tools.Desktop;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class EndpointProofTests
{
    [Fact]
    public void Endpoint_proof_is_stable_per_salt_and_ephemeral_between_salts()
    {
        var firstSalt = Encoding.UTF8.GetBytes("01234567890123456789012345678901");
        var secondSalt = Encoding.UTF8.GetBytes("abcdefghijklmnopqrstuvwxyzABCDEF");

        var first = EndpointProof.Hash("raw-endpoint-id", firstSalt);
        var repeated = EndpointProof.Hash("raw-endpoint-id", firstSalt);
        var otherRun = EndpointProof.Hash("raw-endpoint-id", secondSalt);

        Assert.Equal(first, repeated);
        Assert.NotEqual(first, otherRun);
        Assert.Matches("^[0-9a-f]{16}$", first);
        Assert.DoesNotContain("endpoint", first, StringComparison.OrdinalIgnoreCase);
    }
}
