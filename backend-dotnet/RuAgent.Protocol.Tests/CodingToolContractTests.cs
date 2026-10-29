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
    public void Profiles_require_an_explicit_supported_pin_and_retired_tools_are_absent()
    {
        Assert.Equal("tool_profile_required", Assert.Throws<AgentProtocolException>(() => AgentToolProfiles.ReadPin(new JsonObject())).Code);
        Assert.Throws<AgentProtocolException>(() => AgentToolProfiles.ReadPin(new JsonObject { [AgentToolProfiles.PinField] = "unknown" }));
        Assert.Throws<AgentProtocolException>(() => AgentToolProfiles.ReadPin(new JsonObject { [AgentToolProfiles.PinField] = "legacy_v1" }));
        foreach (var tool in new[] { "write", "edit", "bash" }) Assert.Null(AgentToolDefinitionCatalog.Find(tool, "1.0.0"));
        Assert.False(AgentToolProfiles.Includes(AgentToolProfiles.Minimal, "write"));
        Assert.True(AgentToolProfiles.Includes(AgentToolProfiles.Minimal, "write_file"));
        Assert.False(AgentToolProfiles.Includes(AgentToolProfiles.Minimal, "bash"));
        Assert.True(AgentToolProfiles.Includes(AgentToolProfiles.Minimal, "powershell"));
        foreach (var profile in new[] { AgentToolProfiles.Minimal, AgentToolProfiles.Patch })
        {
            Assert.False(AgentToolProfiles.Includes(profile, "future_catalog_tool"));
            Assert.False(AgentToolProfiles.Includes(profile, "run_code"));
        }
        Assert.True(AgentToolProfiles.Includes(AgentToolProfiles.Programmatic, "run_code"));
        Assert.Equal(AgentToolProfiles.Programmatic, AgentToolProfiles.ForNewRun("coding_code_v1"));
    }

    [Fact]
    public void Semantic_profile_is_opt_in_and_existing_inventories_stay_unchanged()
    {
        Assert.False(AgentToolProfiles.Includes(AgentToolProfiles.Minimal, "code_intelligence"));
        Assert.False(AgentToolProfiles.Includes(AgentToolProfiles.Patch, "code_intelligence"));
        Assert.False(AgentToolProfiles.Includes(AgentToolProfiles.Programmatic, "code_intelligence"));
        Assert.True(AgentToolProfiles.Includes(AgentToolProfiles.Semantic, "code_intelligence"));
        Assert.Equal(AgentToolProfiles.Semantic, AgentToolProfiles.ReadPin(new JsonObject { [AgentToolProfiles.PinField] = AgentToolProfiles.Semantic }));
        Assert.Equal(AgentToolProfiles.Minimal, AgentToolProfiles.ForNewRun());
        Assert.Contains("code_intelligence", AgentToolDefinitionCatalog.Get().Select(tool => tool.ToolId));
    }
}
