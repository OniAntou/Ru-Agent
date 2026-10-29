using System.Text.Json;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Protocol.Tests;

/// <summary>The activity wire's newer kinds and the terminal exit code.</summary>
public sealed class AgentActivityWireKindsTests
{
    [Theory]
    [InlineData(AgentActivityKind.Web, "web")]
    [InlineData(AgentActivityKind.Mcp, "mcp")]
    [InlineData(AgentActivityKind.Computer, "computer")]
    [InlineData(AgentActivityKind.Skill, "skill")]
    [InlineData(AgentActivityKind.Program, "program")]
    public void New_kinds_round_trip_as_camel_case_tokens(AgentActivityKind kind, string token)
    {
        var json = AgentActivityWireCodec.Serialize(new AgentActivityWire("act_kind1", kind, AgentActivityState.Completed));

        Assert.Contains($"\"kind\":\"{token}\"", json, StringComparison.Ordinal);
        Assert.Equal(kind, AgentActivityWireCodec.Parse(json).Kind);
    }

    [Fact]
    public void An_exit_code_round_trips_including_a_negative_windows_status()
    {
        var wire = new AgentActivityWire("act_exit1", AgentActivityKind.Terminal, AgentActivityState.Failed,
            SafeCode: "process_exit_nonzero", ToolDisplay: "Terminal", ExitCode: unchecked((int)0xC0000005));

        var parsed = AgentActivityWireCodec.Parse(AgentActivityWireCodec.Serialize(wire));

        Assert.Equal(wire.ExitCode, parsed.ExitCode);
        Assert.DoesNotContain("exit_code", AgentActivityWireCodec.Serialize(wire with { ExitCode = null }), StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_fields_are_still_rejected()
    {
        Assert.Throws<JsonException>(() => AgentActivityWireCodec.Parse(
            "{\"activity_id\":\"act_x1\",\"kind\":\"terminal\",\"state\":\"completed\",\"stdout\":\"secret\"}"));
    }
}
