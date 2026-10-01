extern alias Broker;

using System.Net.Http.Headers;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RuBackend.Business;
using RuBackend.Business.Settings;
using RuBackend.Diagnostics;
using RuBackend.Runtime;
using RuBackend.Tests.Fakes;
using BrokerHostHost = Broker::RuAgent.Broker.Runtime.BrokerHost;

namespace RuBackend.Tests;

public sealed class BackendWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string? _ownedDataRoot;
    private readonly DataPaths? _dataPaths;
    private readonly ICredentialStore? _credentialStore;
    private readonly ISettingsStore? _settingsStore;
    private readonly IDiagnosticEmitter? _diagnosticEmitter;
    private readonly bool _withInProcessBroker;
    private BrokerHostHost? _brokerHost;
    private string? _brokerDescriptorPath;
    private string? _brokerRoot;

    public BackendWebApplicationFactory(
        DataPaths? dataPaths = null,
        ICredentialStore? credentialStore = null,
        ISettingsStore? settingsStore = null,
        IDiagnosticEmitter? diagnosticEmitter = null,
        RuAgent.Harness.Core.IModelStepRunner? modelStepRunner = null,
        bool withInProcessBroker = false)
    {
        _ownedDataRoot = dataPaths is null
            ? Path.Combine(Path.GetTempPath(), "ru-backend-test-" + Guid.NewGuid().ToString("N"))
            : null;
        _dataPaths = dataPaths;
        _credentialStore = credentialStore;
        _settingsStore = settingsStore;
        _diagnosticEmitter = diagnosticEmitter;
        ModelStepRunnerOverride = modelStepRunner;
        _withInProcessBroker = withInProcessBroker;
        if (_withInProcessBroker)
        {
            // The Broker host builds its own generic host; starting it inside
            // ConfigureWebHost would recurse through the hosting startup
            // pipeline, so it must live before the test server is built.
            StartInProcessBroker();
        }
    }

    internal RuAgent.Harness.Core.IModelStepRunner? ModelStepRunnerOverride { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("RU_DESKTOP_TOKEN", "test-secret");
        if (_ownedDataRoot is not null)
        {
            builder.UseSetting("RU_DATA_ROOT", _ownedDataRoot);
            builder.UseSetting("RU_LOG_ROOT", Path.Combine(_ownedDataRoot, "logs"));
        }
        else if (_dataPaths is not null)
        {
            // A caller-supplied DataPaths is already isolated per test.  Keep
            // the structured logger on that same root; otherwise parallel
            // WebApplicationFactory instances all append to the user's daily
            // %LOCALAPPDATA%\Ru\logs file and teardown races on its handle.
            builder.UseSetting("RU_LOG_ROOT", _dataPaths.LogRoot);
        }

        builder.ConfigureTestServices(services =>
        {
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

            services.RemoveAll<ICredentialStore>();
            services.AddSingleton<ICredentialStore>(_credentialStore ?? new EphemeralTestCredentialStore());
            services.AddHttpClient("ProviderCatalog")
                .ConfigurePrimaryHttpMessageHandler(() => new SyntheticCatalogHandler());

            if (_settingsStore is not null)
            {
                services.RemoveAll<ISettingsStore>();
                services.AddSingleton(_settingsStore);
            }

            if (_diagnosticEmitter is not null)
            {
                services.RemoveAll<IDiagnosticEmitter>();
                services.AddSingleton(_diagnosticEmitter);
            }

            if (_withInProcessBroker)
            {
                services.RemoveAll<RuBackend.Harness.Broker.BrokerToolGatewayFactory>();
                services.AddSingleton(_ =>
                    new RuBackend.Harness.Broker.BrokerToolGatewayFactory(_brokerDescriptorPath));
            }

        });
    }

    private void StartInProcessBroker()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ru-backend-broker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sessionRoot = Path.Combine(root, "broker-session");
        Directory.CreateDirectory(sessionRoot);
        _brokerRoot = root;
        _brokerDescriptorPath = Path.Combine(sessionRoot, "broker.json");
        var descriptor = new RuAgent.Protocol.BrokerSessionDescriptor(
            "http://127.0.0.1:" + GetFreePort(),
            "broker-test-" + Guid.NewGuid().ToString("N"),
            1,
            sessionRoot,
            DateTimeOffset.UtcNow.AddHours(2));
        File.WriteAllText(
            _brokerDescriptorPath,
            System.Text.Json.JsonSerializer.Serialize(
                descriptor,
                RuAgent.Protocol.BrokerSessionDescriptor.JsonOptions));
        _brokerHost = BrokerHostHost.StartAsync(_brokerDescriptorPath)
            .GetAwaiter()
            .GetResult();
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && _brokerHost is not null)
        {
            try
            {
                _brokerHost.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (IOException)
            {
            }
        }

        _brokerHost = null;
        if (disposing && _brokerRoot is not null)
        {
            try
            {
                Directory.Delete(_brokerRoot, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        _brokerRoot = null;
        if (!disposing || _ownedDataRoot is null)
        {
            return;
        }

        // The harness stores pool their connections to this root's database. The pool
        // has to be dropped before the delete below or the file handle survives and the
        // directory is leaked; clear only this root's pool, never the process-global
        // ClearAllPools() that used to hide this cleanup gap for unrelated tests.
        SqliteTestPools.ClearHarnessDatabase(
            Path.Combine(_ownedDataRoot, "agent_harness.db"));

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
