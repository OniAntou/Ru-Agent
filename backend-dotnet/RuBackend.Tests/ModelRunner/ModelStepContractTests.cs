using RuBackend.Harness.Prompting;
using Xunit;

namespace RuBackend.Tests.ModelRunner;

/// <summary>
/// Trimmed with the typed model step: the envelope, codec and start/done
/// validation cases died with the socket transport and the retired worker wire
/// contract. What remains is the runtime capability projection the model-tool
/// settings are built from.
/// </summary>
public sealed class ModelStepContractTests
{
    [Theory]
    [InlineData("legacy_v1", "write", "powershell")]
    [InlineData("coding_v2", "edit_file", "apply_patch")]
    [InlineData("patch_v2", "apply_patch", "edit_file")]
    public void Profile_selects_one_mutation_surface(string profile, string included, string excluded)
    {
        var ids = AgentToolRuntimeProjection.Select(new AgentToolRuntimeAvailability(false, false, ToolProfile: profile))
            .Select(tool => tool.ToolId).ToArray();
        Assert.Contains(included, ids);
        Assert.DoesNotContain(excluded, ids);
        if (profile != "legacy_v1")
        {
            Assert.Contains("powershell", ids);
            Assert.Contains("read_file", ids);
            Assert.DoesNotContain("bash", ids);
        }
    }

    [Fact]
    public void Runtime_capabilities_advertise_both_web_tools()
    {
        var runtimeTools = AgentToolRuntimeProjection.Select(
            new AgentToolRuntimeAvailability(true, true));
        var search = Assert.Single(runtimeTools, definition => definition.ToolId == "web_search");
        var fetch = Assert.Single(runtimeTools, definition => definition.ToolId == "web_fetch");
        Assert.Equal("network.search", search.Scope);
        Assert.Equal("network.fetch", fetch.Scope);
        Assert.True(search.InputSchema["properties"]!.AsObject().ContainsKey("max_results"));

        var offlineTools = AgentToolRuntimeProjection.Select(
            new AgentToolRuntimeAvailability(false, false));
        Assert.DoesNotContain(offlineTools, definition => definition.ToolId == "web_search");
        Assert.DoesNotContain(offlineTools, definition => definition.ToolId == "web_fetch");
        var read = Assert.Single(offlineTools, definition => definition.ToolId == "read");
        // read has no op discriminator at all now, so the web tools cannot appear in one.
        Assert.False(read.InputSchema["properties"]!.AsObject().ContainsKey("op"));
    }
}
