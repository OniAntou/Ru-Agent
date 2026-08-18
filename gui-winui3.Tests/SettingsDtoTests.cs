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
    public void VoiceStyleIdUsesTheBackendWireName()
    {
        string payload = JsonSerializer.Serialize(new VoiceSettingsUpdateDto
        {
            VoiceStyleId = "original",
        });

        using JsonDocument document = JsonDocument.Parse(payload);

        Assert.Equal(
            "original",
            document.RootElement.GetProperty("voice_style_id").GetString());
    }

    [Fact]
    public void SettingsDtoReadsASupportedVoiceStyle()
    {
        AppSettingsDto? settings = JsonSerializer.Deserialize<AppSettingsDto>(
            """{"voice_style_id":"original"}""");

        Assert.NotNull(settings);
        Assert.Equal("original", settings!.VoiceStyleId);
    }

    [Fact]
    public void VoiceSettingsDefaultToOriginalWithoutAutomaticPace()
    {
        var settings = new VoiceSettingsUpdateDto();

        Assert.Equal("original", settings.VoiceStyleId);
        Assert.False(settings.AutoPaceEnabled);
    }

    [Fact]
    public void ManagedVoiceSettingsDefaultToAutomaticPaceDisabled()
    {
        Assert.False(new AppSettingsDto().AutoPaceEnabled);
        Assert.False(new VoiceStatusDto().AutoPaceEnabled);
    }
}
