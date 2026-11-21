using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// Both workspace body routes decode their body through the strict-UTF-8 bounded
/// reader. Malformed bytes are a client error with the established
/// <c>400 invalid_request</c> envelope, never an unhandled 500 internal_error.
/// </summary>
public sealed class AgentWorkspaceRequestBodyTests
{
    // 0xC3 starts a two-byte UTF-8 sequence; 0x28 is not a continuation byte.
    private static readonly byte[] InvalidUtf8 = [0xC3, 0x28, 0x68, 0x65];

    [Fact]
    public async Task Workspace_concurrency_route_answers_400_for_invalid_utf8()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        using var content = new ByteArrayContent(InvalidUtf8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await client.PutAsync(
            $"/agent/v1/workspaces/ws_{new string('a', 32)}/concurrency",
            content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "invalid_request",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task Workspace_change_restore_route_answers_400_for_invalid_utf8()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        using var content = new ByteArrayContent(InvalidUtf8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await client.PostAsync(
            $"/agent/v1/workspaces/ws_{new string('a', 32)}"
                + $"/changes/change_{new string('b', 32)}/restore",
            content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "invalid_request",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error_code").GetString());
    }
}
