using System.Text.Json;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentRunSummaryProtocolTests
{
    [Fact]
    public void Round_trip_uses_snake_case_and_omits_unknown_optional_values()
    {
        var summary = new AgentRunSummaryWire(
            "succeeded",
            "verified",
            "Test Agent",
            FilesScanned: 5,
            FilesChanged: 1,
            FilesCreated: 0,
            FilesDeleted: 0);

        var json = AgentRunSummaryWireCodec.Serialize(summary);
        using var document = JsonDocument.Parse(json);

        Assert.Equal("succeeded", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("verified", document.RootElement.GetProperty("verification_state").GetString());
        Assert.Equal("Test Agent", document.RootElement.GetProperty("workspace_label").GetString());
        Assert.Equal(5, document.RootElement.GetProperty("files_scanned").GetInt32());
        var parsed = AgentRunSummaryWireCodec.Parse(json);
        Assert.Equal(summary, parsed);
    }

    [Fact]
    public void Optional_fields_are_omitted_when_the_runtime_has_no_safe_counts()
    {
        var json = AgentRunSummaryWireCodec.Serialize(
            new AgentRunSummaryWire("cancelled", "not_applicable", null, null, null, null, null));

        Assert.DoesNotContain("workspace_label", json, StringComparison.Ordinal);
        Assert.DoesNotContain("files_scanned", json, StringComparison.Ordinal);
        Assert.Equal(
            new AgentRunSummaryWire("cancelled", "not_applicable", null, null, null, null, null),
            AgentRunSummaryWireCodec.Parse(json));
    }

    [Theory]
    [InlineData("{\"status\":\"unknown\",\"verification_state\":\"verified\"}")]
    [InlineData("{\"status\":\"failed\",\"verification_state\":\"secret\"}")]
    [InlineData("{\"status\":\"failed\",\"verification_state\":\"not_applicable\",\"command\":\"dir\"}")]
    [InlineData("{\"status\":\"failed\",\"verification_state\":\"not_applicable\",\"files_changed\":-1}")]
    [InlineData("{\"status\":\"failed\",\"verification_state\":\"not_applicable\",\"workspace_label\":\"C:\\\\secret\"}")]
    public void Unsafe_or_unknown_fields_are_rejected(string json)
    {
        Assert.Throws<JsonException>(() => AgentRunSummaryWireCodec.Parse(json));
    }
}
