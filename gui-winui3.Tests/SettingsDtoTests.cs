using System.Text.Json;
using RuApp.Models;
using Xunit;

namespace RuApp.Tests;

public sealed class SettingsDtoTests
{
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
            VoiceStyleId = "low_pitch",
        });

        using JsonDocument document = JsonDocument.Parse(payload);

        Assert.Equal(
            "low_pitch",
            document.RootElement.GetProperty("voice_style_id").GetString());
    }

    [Fact]
    public void SettingsDtoReadsTheSavedVoiceStyle()
    {
        AppSettingsDto? settings = JsonSerializer.Deserialize<AppSettingsDto>(
            """{"voice_style_id":"whisper"}""");

        Assert.NotNull(settings);
        Assert.Equal("whisper", settings!.VoiceStyleId);
    }
}
