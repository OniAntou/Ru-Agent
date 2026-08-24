using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentV1EndpointTests
{
    private static readonly (string Method, string Path)[] ExpectedRoutes =
    {
        ("POST", "/agent/v1/runs"),
        ("GET", "/agent/v1/runs/{runId}"),
        ("GET", "/agent/v1/runs/{runId}/events"),
        ("GET", "/agent/v1/runs/{runId}/payloads/{payloadId}"),
        ("POST", "/agent/v1/runs/{runId}/approvals/{approvalId}"),
        ("POST", "/agent/v1/runs/{runId}/trust"),
        ("POST", "/agent/v1/runs/{runId}/cancel"),
        ("POST", "/agent/v1/runs/{runId}/continue"),
        ("POST", "/agent/v1/runs/{runId}/reconcile"),
        ("GET", "/agent/v1/sessions/{sessionId}/runs"),
        ("GET", "/agent/v1/policies"),
        ("POST", "/agent/v1/policies"),
        ("DELETE", "/agent/v1/policies/{ruleId}"),
    };

    [Fact]
    public void Route_manifest_contains_exact_13_agent_routes()
    {
        var contractPath = FindContractPath();
        var json = File.ReadAllText(contractPath);
        var doc = JsonDocument.Parse(json);
        var routes = doc.RootElement.EnumerateArray()
            .Where(e => e.GetProperty("path").GetString()!.StartsWith("/agent/v1", StringComparison.Ordinal))
            .Select(e => (e.GetProperty("method").GetString()!, e.GetProperty("path").GetString()!))
            .ToHashSet();

        var expected = ExpectedRoutes.ToHashSet();
        Assert.Equal(expected, routes);
        Assert.Equal(54, doc.RootElement.GetArrayLength() - 2); // minus 2 websocket, HTTP count 54
    }

    [Theory]
    [InlineData("POST", "/agent/v1/runs")]
    [InlineData("GET", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("GET", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/events")]
    [InlineData("GET", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/payloads/payload_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/approvals/apr_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/trust")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/cancel")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/continue")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/reconcile")]
    [InlineData("GET", "/agent/v1/sessions/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/runs")]
    [InlineData("GET", "/agent/v1/policies")]
    [InlineData("POST", "/agent/v1/policies")]
    [InlineData("DELETE", "/agent/v1/policies/rule_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Agent_routes_reject_missing_bearer_with_401(string method, string path)
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient(); // no auth
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new { });
        }

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/agent/v1/runs")]
    [InlineData("GET", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("GET", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/events")]
    [InlineData("GET", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/payloads/payload_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/approvals/apr_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/trust")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/cancel")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/continue")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/reconcile")]
    [InlineData("GET", "/agent/v1/sessions/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/runs")]
    [InlineData("GET", "/agent/v1/policies")]
    [InlineData("POST", "/agent/v1/policies")]
    [InlineData("DELETE", "/agent/v1/policies/rule_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Agent_routes_reject_wrong_bearer_with_401(string method, string path)
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "wrong-secret");
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new { });
        }

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Policy_and_approval_and_reconcile_return_phase_not_available()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        using var getPol = await client.GetAsync("/agent/v1/policies");
        Assert.Equal(409, (int)getPol.StatusCode);
        var getJson = await getPol.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("phase_not_available", getJson.GetProperty("error_code").GetString());

        using var postPol = await client.PostAsJsonAsync("/agent/v1/policies", new { rule = new { } });
        Assert.Equal(409, (int)postPol.StatusCode);

        using var delPol = await client.DeleteAsync("/agent/v1/policies/rule_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        Assert.Equal(409, (int)delPol.StatusCode);

        using var appr = await client.PostAsJsonAsync("/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/approvals/apr_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", new { });
        Assert.Equal(409, (int)appr.StatusCode);
        var apprJson = await appr.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("phase_not_available", apprJson.GetProperty("error_code").GetString());

        using var rec = await client.PostAsJsonAsync("/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/reconcile", new { outcome = "applied" });
        Assert.Equal(409, (int)rec.StatusCode);
    }

    private static string FindContractPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "backend-dotnet", "public-route-contract.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException("contract not found");
    }
}
