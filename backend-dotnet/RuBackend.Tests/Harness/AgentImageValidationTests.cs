using RuAgent.Protocol.Harness;
using RuBackend.Business;
using RuBackend.Business.Sessions;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentImageValidationTests
{
    [Theory]
    [InlineData("A")]
    [InlineData("====")]
    [InlineData("YWJj=")]
    [InlineData("AA=A")]
    [InlineData("AB==")]
    public async Task Malformed_image_base64_is_rejected_before_run_creation(string base64)
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var paths = new DataPaths(
            Path.Combine(fixture.RootPath, "settings.json"),
            Path.Combine(fixture.RootPath, "sessions"),
            Path.Combine(fixture.RootPath, "activity.log"));
        var sessions = new SessionStore(paths, TimeProvider.System);
        var sessionId = await sessions.CreateAsync(CancellationToken.None);
        var workspace = Path.Combine(fixture.RootPath, "image-workspace");
        Directory.CreateDirectory(workspace);
        var service = new AgentV1ServiceTestBuilder(fixture).Build().Creation;

        var result = await service.CreateRunAsync(new CreateAgentRunRequest(
            sessionId!,
            workspace,
            "invalid image regression",
            "standard",
            null,
            "image-validation-" + Guid.NewGuid().ToString("N"),
            [$"data:image/png;base64,{base64}"]), CancellationToken.None);

        Assert.Equal(400, result.StatusCode);
        var body = System.Text.Json.JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("invalid_image_base64", body.GetProperty("error_code").GetString());
    }
}
