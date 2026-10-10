using System.Net.Http.Headers;
using System.Net;
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

namespace RuBackend.Tests;

public sealed class BackendWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string? _ownedDataRoot;
    private readonly DataPaths? _dataPaths;
    private readonly ICredentialStore? _credentialStore;
    private readonly ISettingsStore? _settingsStore;
    private readonly IDiagnosticEmitter? _diagnosticEmitter;
    private readonly HttpMessageHandler? _providerHandler;

    public BackendWebApplicationFactory(
        DataPaths? dataPaths = null,
        ICredentialStore? credentialStore = null,
        ISettingsStore? settingsStore = null,
        IDiagnosticEmitter? diagnosticEmitter = null,
        RuAgent.Harness.Core.IModelStepRunner? modelStepRunner = null,
        HttpMessageHandler? providerHandler = null)
    {
        _ownedDataRoot = dataPaths is null
            ? Path.Combine(Path.GetTempPath(), "ru-backend-test-" + Guid.NewGuid().ToString("N"))
            : null;
        _dataPaths = dataPaths;
        _credentialStore = credentialStore;
        _settingsStore = settingsStore;
        _diagnosticEmitter = diagnosticEmitter;
        ModelStepRunnerOverride = modelStepRunner;
        _providerHandler = providerHandler;
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
            if (_providerHandler is not null)
            {
                services.RemoveAll<HttpClient>();
                services.AddSingleton(new HttpClient(_providerHandler, disposeHandler: false)
                {
                    Timeout = Timeout.InfiniteTimeSpan,
                });
            }

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

            // The execution authority is the production in-process one
            // (InProcessExecutionAuthority + InProcessToolGatewayFactory);
            // no test seam replaces it.
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
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
