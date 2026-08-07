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
}
