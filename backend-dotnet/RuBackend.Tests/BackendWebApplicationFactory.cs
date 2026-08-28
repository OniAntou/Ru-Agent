using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RuBackend.Business;
using RuBackend.Business.Settings;
using RuBackend.Agent;
using RuBackend.Conversation;
using RuBackend.Diagnostics;
using RuBackend.Memory;
using RuBackend.Runtime;
using RuBackend.Worker;
using RuBackend.Tests.Fakes;

namespace RuBackend.Tests;

public sealed class BackendWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string? _ownedDataRoot;
    private readonly IMemoryService? _memoryService;
    private readonly IRuntimeWorkerClient _runtimeWorkerClient;
    private readonly DataPaths? _dataPaths;
    private readonly ICredentialStore? _credentialStore;
    private readonly IRuntimeSettingsLoader? _runtimeSettingsLoader;
    private readonly ISettingsStore? _settingsStore;
    private readonly IActionOutcomeContextStore? _actionOutcomeContextStore;
    private readonly IVoiceProtocolClient? _voiceProtocolClient;
    private readonly IAgentBrokerClient? _agentBrokerClient;
    private readonly IDiagnosticEmitter? _diagnosticEmitter;
    private readonly IWorkerClient? _workerClient;

    public BackendWebApplicationFactory(
        IMemoryService? memoryService = null,
        IRuntimeWorkerClient? runtimeWorkerClient = null,
        DataPaths? dataPaths = null,
        ICredentialStore? credentialStore = null,
        IRuntimeSettingsLoader? runtimeSettingsLoader = null,
        ISettingsStore? settingsStore = null,
        IActionOutcomeContextStore? actionOutcomeContextStore = null,
        IVoiceProtocolClient? voiceProtocolClient = null,
        IAgentBrokerClient? agentBrokerClient = null,
        IDiagnosticEmitter? diagnosticEmitter = null,
        IWorkerClient? workerClient = null,
        RuAgent.Harness.Core.IModelStepRunner? modelStepRunner = null)
    {
        _ownedDataRoot = dataPaths is null
            ? Path.Combine(Path.GetTempPath(), "ru-backend-test-" + Guid.NewGuid().ToString("N"))
            : null;
        _memoryService = memoryService;
        _runtimeWorkerClient = runtimeWorkerClient ?? StubRuntimeWorkerClient.Healthy();
        _dataPaths = dataPaths;
        _credentialStore = credentialStore;
        _runtimeSettingsLoader = runtimeSettingsLoader;
        _settingsStore = settingsStore;
        _actionOutcomeContextStore = actionOutcomeContextStore;
        _voiceProtocolClient = voiceProtocolClient;
        _agentBrokerClient = agentBrokerClient;
        _diagnosticEmitter = diagnosticEmitter;
        _workerClient = workerClient;
        ModelStepRunnerOverride = modelStepRunner;
    }

    internal RuAgent.Harness.Core.IModelStepRunner? ModelStepRunnerOverride { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("RU_DESKTOP_TOKEN", "test-secret");
        builder.UseSetting("RU_WORKER_TOKEN", "test-worker-secret");
        if (_ownedDataRoot is not null)
        {
            builder.UseSetting("RU_DATA_ROOT", _ownedDataRoot);
            builder.UseSetting("RU_LOG_ROOT", Path.Combine(_ownedDataRoot, "logs"));
        }

        builder.ConfigureTestServices(services =>
        {
            if (_memoryService is not null)
            {
                services.RemoveAll<IMemoryService>();
                services.AddSingleton(_memoryService);
            }

            services.RemoveAll<IRuntimeWorkerClient>();
            services.AddSingleton(_runtimeWorkerClient);

            if (ModelStepRunnerOverride is not null)
            {
                services.RemoveAll<RuAgent.Harness.Core.IModelStepRunner>();
                services.AddSingleton(ModelStepRunnerOverride);
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

            if (_actionOutcomeContextStore is not null)
            {
                services.RemoveAll<IActionOutcomeContextStore>();
                services.AddSingleton(_actionOutcomeContextStore);
            }


            if (_voiceProtocolClient is not null)
            {
                services.RemoveAll<IVoiceProtocolClient>();
                services.AddSingleton(_voiceProtocolClient);
            }

            if (_agentBrokerClient is not null)
            {
                services.RemoveAll<IAgentBrokerClient>();
                services.AddSingleton(_agentBrokerClient);
            }

            if (_diagnosticEmitter is not null)
            {
                services.RemoveAll<IDiagnosticEmitter>();
                services.AddSingleton(_diagnosticEmitter);
            }

            if (_workerClient is not null)
            {
                services.RemoveAll<IWorkerClient>();
                services.AddSingleton(_workerClient);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing || _ownedDataRoot is null)
        {
            return;
        }

        try
        {
            if (Directory.Exists(_ownedDataRoot))
            {
                Directory.Delete(_ownedDataRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");
        return client;
    }
}
