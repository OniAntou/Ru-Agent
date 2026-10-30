using System.Text.Json;
using RuAgent.Harness.Core.Prompting;
using Xunit;

namespace RuAgent.Harness.Core.Tests.Prompting;

public sealed class PromptImageCapabilityTests
{
    [Fact]
    public void Unspecified_capability_keeps_the_existing_context_shape()
    {
        var context = SystemPromptContext.Empty;
        using var json = JsonDocument.Parse(context.SerializeUtf8());
        Assert.False(json.RootElement.GetProperty("environment").TryGetProperty("image_input", out _));
        Assert.DoesNotContain("image_input:", SystemPromptComposer.Compose(context).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Environment_binds_the_declared_image_capability_and_distinguishes_receipt_from_vision(bool accepts)
    {
        var context = new SystemPromptContext(SystemPromptContext.PromptContextSchemaVersion, "ru-agent", "standard",
            new PromptEnvironment("model", "windows", "vi-VN", "UTC", "2026-10-30", null,
                imageInputAccepted: accepts), [], [], new PromptRuntimeContext([]));

        var rendered = SystemPromptComposer.Compose(context);

        Assert.Contains(accepts ? "image_input: supported" : "image_input: unavailable", rendered.Text);
        if (!accepts)
        {
            Assert.Contains("Do not claim to read or see an image", rendered.Text);
            Assert.Contains("Do not confuse unavailable contents with no attachment", rendered.Text);
        }
        using var json = JsonDocument.Parse(context.SerializeUtf8());
        Assert.Equal(accepts, json.RootElement.GetProperty("environment").GetProperty("image_input").GetBoolean());
        rendered.Receipt.ValidateAgainst(context, rendered.Text);
    }
}
