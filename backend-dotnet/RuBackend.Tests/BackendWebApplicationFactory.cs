using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RuBackend.Compatibility;
using RuBackend.Memory;
using RuBackend.Tests.Fakes;

namespace RuBackend.Tests;

public sealed class BackendWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly IUpstreamHttpTransport _httpTransport;
    private readonly IUpstreamWebSocketConnector? _webSocketConnector;
    private readonly IMemoryService? _memoryService;
    private readonly string _memoryOwnership;

    public BackendWebApplicationFactory(
        IUpstreamHttpTransport? httpTransport = null,
        IUpstreamWebSocketConnector? webSocketConnector = null,
        IMemoryService? memoryService = null,
        string memoryOwnership = "python")
    {
        _httpTransport = httpTransport ?? new StubUpstreamHttpTransport(
            new UpstreamResponse(
                StatusCodes.Status200OK,
                "application/json",
                Encoding.UTF8.GetBytes(
                    "{\"status\":\"ok\",\"message\":\"Backend is running and ready for the WinUI3 UI.\"}")));
        _webSocketConnector = webSocketConnector;
        _memoryService = memoryService;
        _memoryOwnership = memoryOwnership;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("RU_DESKTOP_TOKEN", "test-secret");
        builder.UseSetting("RuBackend:UpstreamUrl", "http://127.0.0.1:65531");
        builder.UseSetting("RuBackend:Ownership:Memory", _memoryOwnership);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IUpstreamHttpTransport>();
            services.AddSingleton(_httpTransport);
            if (_webSocketConnector is not null)
            {
                services.RemoveAll<IUpstreamWebSocketConnector>();
                services.AddSingleton(_webSocketConnector);
            }

            if (_memoryService is not null)
            {
                services.RemoveAll<IMemoryService>();
                services.AddSingleton(_memoryService);
            }
        });
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");
        return client;
    }
}
