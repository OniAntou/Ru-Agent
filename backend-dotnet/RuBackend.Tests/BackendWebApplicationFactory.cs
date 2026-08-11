using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RuBackend.Business;
using RuBackend.Business.Settings;
using RuBackend.Compatibility;
using RuBackend.Conversation;
using RuBackend.Memory;
using RuBackend.Runtime;
using RuBackend.Tests.Fakes;

namespace RuBackend.Tests;

public sealed class BackendWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly IUpstreamHttpTransport _httpTransport;
    private readonly IUpstreamWebSocketConnector? _webSocketConnector;
    private readonly IMemoryService? _memoryService;
    private readonly string _memoryOwnership;
    private readonly IRuntimeWorkerClient? _runtimeWorkerClient;
    private readonly string _sttOwnership;
    private readonly string _vtsOwnership;
    private readonly DataPaths? _dataPaths;
    private readonly ICredentialStore? _credentialStore;
    private readonly IRuntimeSettingsLoader? _runtimeSettingsLoader;
    private readonly ISettingsStore? _settingsStore;
    private readonly string _settingsOwnership;
    private readonly string _sessionsOwnership;
    private readonly string _activityOwnership;
    private readonly IConversationCoordinator? _conversationCoordinator;
    private readonly string _chatOwnership;

    public BackendWebApplicationFactory(
        IUpstreamHttpTransport? httpTransport = null,
        IUpstreamWebSocketConnector? webSocketConnector = null,
        IMemoryService? memoryService = null,
        string memoryOwnership = "python",
        IRuntimeWorkerClient? runtimeWorkerClient = null,
        string sttOwnership = "python",
        string vtsOwnership = "python",
        DataPaths? dataPaths = null,
        ICredentialStore? credentialStore = null,
        IRuntimeSettingsLoader? runtimeSettingsLoader = null,
        ISettingsStore? settingsStore = null,
        string settingsOwnership = "python",
        string sessionsOwnership = "python",
        string activityOwnership = "python",
        IConversationCoordinator? conversationCoordinator = null,
        string chatOwnership = "python")
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
        _runtimeWorkerClient = runtimeWorkerClient;
        _sttOwnership = sttOwnership;
        _vtsOwnership = vtsOwnership;
        _dataPaths = dataPaths;
        _credentialStore = credentialStore;
        _runtimeSettingsLoader = runtimeSettingsLoader;
        _settingsStore = settingsStore;
        _settingsOwnership = settingsOwnership;
        _sessionsOwnership = sessionsOwnership;
        _activityOwnership = activityOwnership;
        _conversationCoordinator = conversationCoordinator;
        _chatOwnership = chatOwnership;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("RU_DESKTOP_TOKEN", "test-secret");
        builder.UseSetting("RuBackend:UpstreamUrl", "http://127.0.0.1:65531");
        builder.UseSetting("RuBackend:Ownership:Memory", _memoryOwnership);
        builder.UseSetting("RuBackend:Ownership:Stt", _sttOwnership);
        builder.UseSetting("RuBackend:Ownership:Vts", _vtsOwnership);
        builder.UseSetting("RuBackend:Ownership:Settings", _settingsOwnership);
        builder.UseSetting("RuBackend:Ownership:Sessions", _sessionsOwnership);
        builder.UseSetting("RuBackend:Ownership:Activity", _activityOwnership);
        builder.UseSetting("RuBackend:Ownership:Chat", _chatOwnership);
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

            if (_runtimeWorkerClient is not null)
            {
                services.RemoveAll<IRuntimeWorkerClient>();
                services.AddSingleton(_runtimeWorkerClient);
            }

            if (_dataPaths is not null)
            {
                services.RemoveAll<DataPaths>();
                services.AddSingleton(_dataPaths);
            }

            if (_credentialStore is not null)
            {
                services.RemoveAll<ICredentialStore>();
                services.AddSingleton(_credentialStore);
            }

            if (_runtimeSettingsLoader is not null)
            {
                services.RemoveAll<IRuntimeSettingsLoader>();
                services.AddSingleton(_runtimeSettingsLoader);
            }

            if (_settingsStore is not null)
            {
                services.RemoveAll<ISettingsStore>();
                services.AddSingleton(_settingsStore);
            }

            if (_conversationCoordinator is not null)
            {
                services.RemoveAll<IConversationCoordinator>();
                services.AddSingleton(_conversationCoordinator);
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
