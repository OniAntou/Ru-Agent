using System.Text.Json.Nodes;
using Xunit;

namespace RuAgent.Protocol.Tests;

public sealed class CodingToolContractTests
{
    [Theory]
    [InlineData("write_file", "path", "content")]
    [InlineData("edit_file", "path", "edits")]
    [InlineData("powershell", "script", null)]
    [InlineData("run_process", "executable", null)]
    [InlineData("terminal_read", "execution_id", null)]
    [InlineData("terminal_stop", "execution_id", null)]
    public void Simple_tools_express_required_arguments_without_mode_or_op(string id, string first, string? second)
    {
        var definition = Assert.IsType<AgentToolDefinition>(AgentToolDefinitionCatalog.Find(id, "1.0.0"));
        var required = definition.InputSchema["required"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray();
        Assert.Contains(first, required);
        if (second is not null) Assert.Contains(second, required);
        Assert.DoesNotContain("op", required);
        Assert.DoesNotContain("mode", required);
    }

    [Fact]
    public void Windows_command_surface_cannot_choose_a_different_mode()
    {
        var args = CodingToolArguments.ToAdapterArguments("powershell", new JsonObject { ["script"] = "dotnet test" });
        Assert.Equal("shell", args["mode"]!.GetValue<string>());
        Assert.Equal("powershell", args["shell"]!.GetValue<string>());
        Assert.Throws<ArgumentException>(() => CodingToolArguments.ToAdapterArguments("powershell",
            new JsonObject { ["script"] = "dotnet test", ["mode"] = "run" }));
    }

    [Fact]
    public void Profiles_are_explicit_and_missing_pin_keeps_legacy_runs_unchanged()
    {
        Assert.Equal(AgentToolProfiles.Legacy, AgentToolProfiles.ReadPin(new JsonObject()));
        Assert.Throws<AgentProtocolException>(() => AgentToolProfiles.ReadPin(new JsonObject { [AgentToolProfiles.PinField] = "unknown" }));
        Assert.True(AgentToolProfiles.Includes(AgentToolProfiles.Legacy, "write"));
        Assert.False(AgentToolProfiles.Includes(AgentToolProfiles.Legacy, "write_file"));
        Assert.False(AgentToolProfiles.Includes(AgentToolProfiles.Minimal, "write"));
        Assert.True(AgentToolProfiles.Includes(AgentToolProfiles.Minimal, "write_file"));
        Assert.False(AgentToolProfiles.Includes(AgentToolProfiles.Minimal, "bash"));
        Assert.True(AgentToolProfiles.Includes(AgentToolProfiles.Minimal, "powershell"));
        foreach (var profile in new[] { AgentToolProfiles.Legacy, AgentToolProfiles.Minimal, AgentToolProfiles.Patch })
        {
            Assert.False(AgentToolProfiles.Includes(profile, "future_catalog_tool"));
            Assert.False(AgentToolProfiles.Includes(profile, "run_code"));
        }
        Assert.True(AgentToolProfiles.Includes(AgentToolProfiles.Programmatic, "run_code"));
        Assert.Equal(AgentToolProfiles.Programmatic, AgentToolProfiles.ForNewRun("coding_code_v1"));
    }
}
