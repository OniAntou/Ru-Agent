using System.Text.Json;
using RuApp.Models;
using Xunit;

namespace RuApp.Tests;

public sealed class SettingsDtoTests
{
    [Fact]
    public void CustomApiKeyUsesTheBackendWireName()
    {
        string payload = JsonSerializer.Serialize(new AppSettingsDto
        {
            CustomApiKey = "sk-custom",
        });

        using JsonDocument document = JsonDocument.Parse(payload);

        Assert.Equal("sk-custom", document.RootElement.GetProperty("custom_api_key").GetString());
        Assert.False(document.RootElement.GetProperty("has_custom_api_key").GetBoolean());
    }

    [Fact]
    public void UnsetBackendUrlsAreOmittedFromTheSavePayload()
    {
        string payload = JsonSerializer.Serialize(new AppSettingsDto
        {
            CustomApiKey = "sk-custom",
        });

        using JsonDocument document = JsonDocument.Parse(payload);

        Assert.False(document.RootElement.TryGetProperty("ollama_url", out _));
        Assert.False(document.RootElement.TryGetProperty("lmstudio_url", out _));
        Assert.Equal(
            "http://localhost:8000/v1",
            document.RootElement.GetProperty("custom_url").GetString());
    }

    [Fact]
    public void KiloReasoningEnabledUsesTheBackendWireName()
    {
        string payload = JsonSerializer.Serialize(new AppSettingsDto
        {
            KiloReasoningEnabled = true,
        });

        using JsonDocument document = JsonDocument.Parse(payload);

        Assert.True(document.RootElement.GetProperty("kilo_reasoning_enabled").GetBoolean());
    }

    [Fact]
    public void VoiceSettingsAreNoLongerPartOfTheSavePayload()
    {
        string payload = JsonSerializer.Serialize(new AppSettingsDto());

        using JsonDocument document = JsonDocument.Parse(payload);

        Assert.False(document.RootElement.TryGetProperty("voice_style_id", out _));
        Assert.False(document.RootElement.TryGetProperty("auto_read_replies", out _));
        Assert.False(document.RootElement.TryGetProperty("show_subtitles", out _));
    }
}
