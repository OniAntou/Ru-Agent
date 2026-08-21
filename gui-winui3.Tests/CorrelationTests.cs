using System.Text.Json;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class CorrelationTests
{
    [Fact]
    public void VoiceStreamClient_SerializeStartFrame_includes_valid_correlation_id()
    {
        var json = VoiceStreamClient.SerializeStartFrame("req-1", "hello", "ru", 1.0);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("correlation_id", out var corrProp));
        var corr = corrProp.GetString();
        Assert.NotNull(corr);
        Assert.StartsWith("c_", corr);
        Assert.InRange(corr.Length, 8, 64);
    }

    [Fact]
    public void VoiceStreamClient_SerializeStartFrame_preserves_explicit_correlation_id()
    {
        var json = VoiceStreamClient.SerializeStartFrame("req-1", "hello", "ru", 1.0, "c_custom_id_123");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("correlation_id", out var corrProp));
        Assert.Equal("c_custom_id_123", corrProp.GetString());
    }
}
