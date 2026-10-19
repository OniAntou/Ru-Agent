using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;
namespace RuBackend.Tests.Business;
public sealed class SubagentModelSettingsTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("{\"coding\":{\"provider_id\":\"custom\",\"model\":\"old-coder\"}}")]
    [InlineData("{\"review\":false}")]
    public async Task Removed_role_settings_cannot_be_saved(string patch)
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        var before = (await client.GetFromJsonAsync<JsonObject>("/settings"))!;
        using var response = await client.PostAsJsonAsync("/settings", new JsonObject { ["subagents"] = JsonNode.Parse(patch) });
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("error", result["status"]!.GetValue<string>());
        var after = (await client.GetFromJsonAsync<JsonObject>("/settings"))!;
        Assert.Null(after["subagents"]);
        Assert.Equal(before["backend_choice"]!.ToJsonString(), after["backend_choice"]!.ToJsonString());
    }
}
