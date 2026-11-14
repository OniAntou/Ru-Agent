using System.Text.Json.Nodes;
using RuAgent.Harness.Persistence;
using RuBackend.Providers;
using Xunit;

namespace RuBackend.Tests.Providers;

/// <summary>ADR 0047: the advisor setting and its run marker.</summary>
public sealed class AdvisorPolicyTests
{
    private static string Normalize(string value) => value.ToLowerInvariant() switch
    {
        "anthropic" => "Anthropic",
        "openai" => "OpenAI",
        _ => throw new ArgumentException("unknown backend"),
    };

    [Fact]
    public void Settings_save_selects_and_clears_the_advisor_as_a_pair()
    {
        var raw = new JsonObject();
        AdvisorPolicy.Apply(new JsonObject { ["advisor_backend"] = "anthropic", ["advisor_model"] = " claude-opus-5-5 " }, raw, Normalize);
        Assert.Equal("Anthropic", raw["advisor_backend"]!.GetValue<string>());
        Assert.Equal("claude-opus-5-5", raw["advisor_model"]!.GetValue<string>());

        // Unrelated saves leave the advisor alone.
        AdvisorPolicy.Apply(new JsonObject { ["model"] = "x" }, raw, Normalize);
        Assert.Equal("claude-opus-5-5", raw["advisor_model"]!.GetValue<string>());

        AdvisorPolicy.Apply(new JsonObject { ["advisor_backend"] = null, ["advisor_model"] = null }, raw, Normalize);
        Assert.False(raw.ContainsKey("advisor_backend"));
        Assert.False(raw.ContainsKey("advisor_model"));
    }

    [Theory]
    [InlineData("""{"advisor_backend":"anthropic"}""")]
    [InlineData("""{"advisor_model":"m"}""")]
    [InlineData("""{"advisor_backend":"anthropic","advisor_model":null}""")]
    [InlineData("""{"advisor_backend":"anthropic","advisor_model":""}""")]
    [InlineData("""{"advisor_backend":"anthropic","advisor_model":7}""")]
    [InlineData("""{"advisor_backend":"nowhere","advisor_model":"m"}""")]
    public void Settings_save_rejects_a_partial_or_invalid_advisor(string payload)
    {
        var raw = new JsonObject { ["advisor_backend"] = "OpenAI", ["advisor_model"] = "gpt-5" };
        Assert.ThrowsAny<ArgumentException>(() => AdvisorPolicy.Apply(JsonNode.Parse(payload)!.AsObject(), raw, Normalize));
        Assert.Equal("gpt-5", raw["advisor_model"]!.GetValue<string>());
    }

    [Fact]
    public void The_marker_round_trips_and_is_removed_before_provider_options()
    {
        var advisor = new AgentRunProviderSelection("Anthropic", "anthropic", "anthropic_messages",
            "https://api.anthropic.com/", "claude-opus-5-5", "anthropic_api_key",
            """{"_ru_tool_profile":"x","thinking":{"type":"adaptive"}}""", null, "claude-opus-5-5",
            AgentRunCallingModeCodec.Native, 1_000_000, "provider_catalog", "catalog-v1", 64000);
        var options = new JsonObject { [AdvisorPolicy.PinField] = AdvisorPolicy.Marker(advisor), ["temperature"] = 0.2 };

        var pin = AdvisorPolicy.TakePinnedCapability(options);
        Assert.False(options.ContainsKey(AdvisorPolicy.PinField));
        Assert.Equal("claude-opus-5-5", pin!.Selection!.Model);
        Assert.Equal(64000, pin.Selection.OutputTokenReserve);
        Assert.Equal(1_000_000, pin.Selection.ContextWindowTokens);
        Assert.DoesNotContain("_ru_", pin.Selection.RequestOptionsJson);
        Assert.Contains("adaptive", pin.Selection.RequestOptionsJson);
        Assert.Null(AdvisorPolicy.TakePinnedCapability(options));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("""{"unavailable":"something_else"}""")]
    [InlineData("""{"backend":"Anthropic"}""")]
    [InlineData("""{"selected":true}""")]
    public void A_malformed_marker_fails_closed(string marker)
    {
        var options = new JsonObject { [AdvisorPolicy.PinField] = JsonNode.Parse(marker) };
        Assert.Throws<ProviderCatalogException>(() => AdvisorPolicy.TakePinnedCapability(options));
        Assert.Throws<ProviderCatalogException>(() => AdvisorPolicy.ReadPinned(Selection(
            new JsonObject { [AdvisorPolicy.PinField] = JsonNode.Parse(marker) }.ToJsonString())));
    }

    [Fact]
    public void Unavailable_and_missing_markers_are_read_back()
    {
        var unavailable = AdvisorPolicy.ReadPinned(Selection(
            new JsonObject { [AdvisorPolicy.PinField] = AdvisorPolicy.UnavailableMarker() }.ToJsonString()));
        Assert.Equal("advisor_unavailable", unavailable!.UnavailableCode);
        Assert.Null(unavailable.Selection);
        var notSelected = AdvisorPolicy.ReadPinned(Selection(
            new JsonObject { [AdvisorPolicy.PinField] = AdvisorPolicy.NotSelectedMarker() }.ToJsonString()));
        Assert.False(notSelected!.IsSelected);
        Assert.Null(AdvisorPolicy.ReadPinned(Selection("{}")));
        Assert.Null(AdvisorPolicy.ReadPinned(Selection("not json")));
        Assert.Null(AdvisorPolicy.ReadPinned(null));
    }

    private static AgentRunProviderSelection Selection(string options) =>
        new("OpenAI", "openai", "responses", "https://api.example.com/", "model", null, options, null);
}
