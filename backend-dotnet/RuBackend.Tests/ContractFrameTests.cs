using System.Text.Json;
using RuBackend.Contracts;
using Xunit;

namespace RuBackend.Tests;

public sealed class ContractFrameTests
{
    [Fact]
    public void VoiceAudioEnvelopeUsesStableFieldNames()
    {
        var json = JsonSerializer.Serialize(new VoiceFrame(
            "audio", "r1", 0, 24_000, "wav"));

        Assert.Equal(
            "{\"type\":\"audio\",\"request_id\":\"r1\",\"sequence\":0,\"sample_rate\":24000,\"format\":\"wav\"}",
            json);
    }
}
