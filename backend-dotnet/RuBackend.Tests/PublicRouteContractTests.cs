using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace RuBackend.Tests;

public sealed class PublicRouteContractTests
{
    [Fact]
    public async Task EndpointDataSource_matches_the_public_route_contract()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        using var _ = await client.GetAsync("/status");

        var contract = JsonSerializer.Deserialize<List<RouteContractEntry>>(
            await File.ReadAllTextAsync(FindContractPath()),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var expected = contract
            .Select(entry => RouteKey(
                entry.Transport == "websocket" ? HttpMethods.Get : entry.Method,
                entry.Path))
            .ToHashSet(StringComparer.Ordinal);

        var actual = factory.Services
            .GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText is not null
                && !endpoint.RoutePattern.RawText.Contains("**", StringComparison.Ordinal))
            .SelectMany(endpoint =>
                (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(method => RouteKey(method, endpoint.RoutePattern.RawText!)))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Retired_chat_route_is_not_exposed()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/chat");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    private static string RouteKey(string method, string path) =>
        $"{method.ToUpperInvariant()} {path}";

    private static string FindContractPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "backend-dotnet",
                "public-route-contract.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate backend-dotnet/public-route-contract.json.");
    }

    private sealed record RouteContractEntry(
        string Method,
        string Path,
        string Transport);
}
