using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RuBackend.Compatibility;
using RuBackend.Tests.Fakes;

namespace RuBackend.Tests;

public sealed class BackendWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly IUpstreamHttpTransport _httpTransport;

    public BackendWebApplicationFactory()
        : this(null)
    {
    }

    public BackendWebApplicationFactory(IUpstreamHttpTransport? httpTransport)
    {
        _httpTransport = httpTransport ?? new StubUpstreamHttpTransport(
            new UpstreamResponse(
                StatusCodes.Status200OK,
                "application/json",
                Encoding.UTF8.GetBytes(
                    "{\"status\":\"ok\",\"message\":\"Backend is running and ready for the WinUI3 UI.\"}")));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("RuBackend:DesktopToken", "test-secret");
        builder.UseSetting("RuBackend:UpstreamUrl", "http://127.0.0.1:65531");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IUpstreamHttpTransport>();
            services.AddSingleton(_httpTransport);
        });
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");
        return client;
    }
}
