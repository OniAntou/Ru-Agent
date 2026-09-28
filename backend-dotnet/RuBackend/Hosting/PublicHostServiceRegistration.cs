using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http;
using RuBackend.Agent;
using RuBackend.Business;
using RuBackend.Business.Activity;
using RuBackend.Business.Sessions;
using RuBackend.Business.Settings;
using RuBackend.Configuration;
using RuBackend.Conversation;
using RuBackend.Diagnostics;
using RuBackend.Harness;
using RuBackend.Harness.Broker;
using RuBackend.Harness.Reconciliation;
using RuBackend.Harness.Worker;
using RuBackend.Harness.Workspace;
using RuBackend.Providers;
using RuBackend.Runtime;
using RuBackend.Security;
using RuBackend.Worker;
using RuAgent.Broker.Configuration;
using RuAgent.Broker.Tools.Web;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Approval;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Reconciliation;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Policy;
using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Harness.Persistence.Workspace;
using RuAgent.Hosting;
using RuAgent.Protocol.Harness;

namespace RuBackend.Hosting;

// DI composition groups for the Public Host. Moved verbatim from Program.cs;
// call order in Program.cs is unchanged so registration semantics are identical.
internal static class PublicHostServiceRegistration
{
    internal static IServiceCollection AddRuFoundation(
        this IServiceCollection services,
        IConfiguration configuration,
        string? managedPublicBearer,
        int agentWorkerPort,
        string agentWorkerToken)
    {
        services.Configure<BackendOptions>(options =>
        {
            configuration.GetSection(BackendOptions.SectionName).Bind(options);
            if (string.IsNullOrWhiteSpace(options.DesktopToken))
            {
                options.DesktopToken = configuration["RU_DESKTOP_TOKEN"] ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(managedPublicBearer))
                    options.DesktopToken = managedPublicBearer;
            }
        });
        services.AddSingleton(BuildDataPaths(configuration));
        services.AddSingleton(BuildAgentWorkerClientOptions(
            configuration,
            agentWorkerPort,
            agentWorkerToken));
        services.AddSingleton<AgentReplyPolicy>();
        services.AddSingleton<IAgentReplyPolicy>(services =>
            services.GetRequiredService<AgentReplyPolicy>());
        services.AddSingleton<AgentReplyEffectSink>();
        services.AddSingleton<IAgentReplyEffectSink>(services =>
            services.GetRequiredService<AgentReplyEffectSink>());
        services.AddSingleton<IMetadataPolicy, MetadataPolicy>();
        services.AddSingleton<IStreamSanitizer, StreamSanitizer>();
        return services;
    }

    internal static IServiceCollection AddRuSettingsAndSessions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var brokerDescriptor = Environment.GetEnvironmentVariable("RU_AGENT_BROKER_DESCRIPTOR");
        if (!string.IsNullOrWhiteSpace(brokerDescriptor))
        {
            services.AddSingleton<IAgentBrokerClient>(_ => new AgentBrokerClient(brokerDescriptor));
        }
        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<ICredentialStore, WindowsCredentialStore>();
        services.AddHttpClient("ProviderCatalog")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton(services => new ProviderCatalogService(
            services.GetRequiredService<IHttpClientFactory>().CreateClient("ProviderCatalog"),
            services.GetRequiredService<ICredentialStore>(),
            services.GetRequiredService<TimeProvider>(),
            Path.Combine(services.GetRequiredService<DataPaths>().BusinessDataRoot, "models-dev.json")));
        services.AddSingleton<ISettingsTransitionService, SettingsTransitionService>();
        services.AddSingleton<ISessionStore, SessionStore>();
        return services;
    }

    internal static IServiceCollection AddRuHarnessStores(this IServiceCollection services)
    {
        services.AddSingleton(services => new HarnessSqliteOptions(
            services.GetRequiredService<DataPaths>().AgentHarnessDatabase));
        services.AddSingleton<WindowsWorkspaceIdentityResolver>();
        services.AddSingleton<RuBackend.Harness.Prompting.RuInstructionLoader>();
        services.AddSingleton<SqliteWorkspaceStore>(services =>
            new SqliteWorkspaceStore(services.GetRequiredService<HarnessSqliteOptions>()));
        services.AddSingleton<WorkspaceCoordinatorStore>(services =>
            new WorkspaceCoordinatorStore(services.GetRequiredService<HarnessSqliteOptions>()));
        services.AddSingleton<WorkspaceExternalChangeMonitor>();
        services.AddSingleton<WorkspaceCoordinatorService>();
        services.AddSingleton<WorkspaceTrustRequestStore>(services =>
            new WorkspaceTrustRequestStore(services.GetRequiredService<HarnessSqliteOptions>()));
        services.AddSingleton<WorkspaceTrustService>();
        services.AddSingleton<IIdGenerator, CryptographicIdGenerator>();
        services.AddSingleton<ITimeProvider, SystemTimeProvider>();
        services.AddSingleton<IHarnessSessionArtifactStore, HarnessSessionArtifactStore>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IHarnessSessionDeletionCoordinator,
            HarnessSessionDeletionCoordinator>();
        services.AddSingleton<AgentSessionPermissionStore>(services =>
            new AgentSessionPermissionStore(services.GetRequiredService<HarnessSqliteOptions>()));
        services.AddSingleton<IHarnessKeyStore>(services =>
        {
            var paths = services.GetRequiredService<DataPaths>();
            var options = services.GetRequiredService<HarnessSqliteOptions>();
            var keyPath = Path.Combine(Path.GetDirectoryName(options.DatabasePath) ?? paths.BusinessDataRoot, "harness_key.dat");
            if (OperatingSystem.IsWindows())
            {
                // Fail closed: a broken DPAPI key store must disable the Agent store instead of
                // silently switching to an ephemeral key that cannot decrypt existing payloads.
                return new WindowsDpapiKeyStore(keyPath, options.DatabasePath);
            }

            // Development-only platforms; Windows is the supported production host.
            // Fail closed: no ephemeral fallback, an in-memory key could not
            // decrypt existing AES-GCM payloads and would overwrite them.
            throw new PlatformNotSupportedException(
                "Harness key store requires Windows DPAPI.");
        });
        services.AddSingleton<WorkspaceJournalProtector>(services =>
            new WorkspaceJournalProtector(services.GetRequiredService<IHarnessKeyStore>()));
        services.AddSingleton<KnownSecretRedactor>(_ => BuildKnownSecretRedactor());
        services.AddSingleton<AgentReplyTransientRedactor>();
        services.AddSingleton<ITransientContentRedactor>(services =>
            services.GetRequiredService<AgentReplyTransientRedactor>());
        services.AddSingleton<HarnessPayloadCryptography>(services =>
            new HarnessPayloadCryptography(services.GetRequiredService<IHarnessKeyStore>()));
        services.AddSingleton<HarnessPayloadStore>(services =>
            new HarnessPayloadStore(
                services.GetRequiredService<HarnessSqliteOptions>(),
                services.GetRequiredService<HarnessPayloadCryptography>(),
                services.GetRequiredService<KnownSecretRedactor>()));
        services.AddSingleton<RetainedApprovalRehydrator>();
        services.AddSingleton<RuBackend.Harness.Policy.HarnessPolicySnapshotService>();
        services.AddSingleton<IHarnessEventStore>(services =>
            new SqliteHarnessEventStore(
                services.GetRequiredService<HarnessSqliteOptions>(),
                services.GetRequiredService<HarnessPayloadStore>()));
        services.AddSingleton<HarnessLiveEventHub>();
        services.AddSingleton<AgentRunSummaryLivePublisher>();
        // Headless runtime loop: per-run coordinators, outbox delivery, and model/tool driving.
        services.AddSingleton<IAgentRuntimeRootRegistry, AgentRuntimeRootRegistry>();
        services.AddSingleton<RuAgent.Harness.Core.Reconciliation.ReconciliationInspectionGate>();
        services.AddSingleton<IAgentRuntimeCoordinatorFactory, AgentRuntimeCoordinatorFactory>();
        var brokerDescriptor = Environment.GetEnvironmentVariable("RU_AGENT_BROKER_DESCRIPTOR");
        services.AddSingleton(_ =>
            new RuBackend.Harness.Broker.BrokerToolGatewayFactory(brokerDescriptor));
        services.AddSingleton(sp => new RuBackend.Harness.Git.SqliteRunGitStateStore(
            Path.Combine(
                Path.GetDirectoryName(sp.GetRequiredService<HarnessSqliteOptions>().DatabasePath)!,
                "agent_git_state.db")));
        services.AddSingleton(sp => new RuBackend.Harness.Reconciliation.WorkspaceStateSnapshotService(
            sp.GetRequiredService<RuBackend.Harness.Git.SqliteRunGitStateStore>(),
            runId => sp.GetRequiredService<IAgentRuntimeRootRegistry>()
                .Snapshot()
                .Where(pair => string.Equals(pair.RunId, runId.Value, StringComparison.Ordinal))
                .Select(pair => pair.WorkspaceRoot)
                .FirstOrDefault()));
        services.AddHostedService<RuBackend.Harness.Reconciliation.HarnessRecoveryStartupService>();
        return services;
    }

    internal static IServiceCollection AddRuAgentRuntime(
        this IServiceCollection services,
        bool isDeterministicHarness,
        bool isManagedRuntime)
    {
        services.AddSingleton<HarnessOutboxStore>();
        services.AddSingleton<RuBackend.Harness.IHarnessModelContextBootstrapper, RuBackend.Harness.HarnessModelContextBootstrapper>();
        services.AddSingleton<AgentConversationActivityProjector>();
        services.AddSingleton<AgentRunHistoryTransitionSink>();
        services.AddSingleton<IAgentHistoryTransitionSink>(services =>
            services.GetRequiredService<AgentRunHistoryTransitionSink>());
        services.AddSingleton<AgentConversationOutboxDispatcher>();
        services.AddHostedService<AgentRuntimeLoopService>();
        services.AddSingleton<AgentV1Service>();
        // Image-input support follows what the provider's catalog declares for the
        // selected model. A name check alone read a gateway model carrying no vendor
        // token as non-vision and rejected its attachments. Wired as a property so
        // this service keeps the dozen optional collaborators DI already supplies.
        services.AddSingleton<IPostConfigureOptions<AgentV1Service>, AgentV1CatalogVisionConfigurator>();

        // Harness Core adapters: Worker v2 and ToolGateway via factory
        services.AddSingleton<LoopbackModelStepRunner>(services =>
            new LoopbackModelStepRunner(services.GetRequiredService<AgentWorkerClientOptions>())
            {
                CreateProviderBootstrapAsync = isDeterministicHarness
                    ? null
                    : services.GetRequiredService<AgentProviderSnapshotFactory>().CreateAsync,
                LoadRunImagesAsync = async (runId, ct) =>
                {
                    var store = services.GetRequiredService<HarnessPayloadStore>();
                    // Reads through the payload store so the audience rule and the
                    // encrypted-payload contract stay in one place instead of being
                    // re-implemented as SQL with a raw kind number here.
                    var payloadIds = await store
                        .ListPayloadIdsAsync(runId, HarnessPayloadKind.UserImage, ct)
                        .ConfigureAwait(false);
                    var list = new List<string>();
                    foreach (var pid in payloadIds)
                    {
                        var content = await store.ReadForModelAsync(runId, pid, ct).ConfigureAwait(false);
                        list.Add(System.Text.Encoding.UTF8.GetString(content.Plaintext.Span));
                    }

                    return list;
                },
            });
        services.AddSingleton(services => new AgentProviderSnapshotFactory(
            services.GetRequiredService<ISettingsStore>(), services.GetRequiredService<ICredentialStore>(),
            services.GetRequiredService<ProviderCatalogService>(),
            Path.Combine(services.GetRequiredService<DataPaths>().LogRoot, "agent", "session"),
            async (runId, ct) =>
            {
                var run = await services.GetRequiredService<IHarnessEventStore>().GetRunAsync(runId, ct);
                return run is null || run.TerminalAt.HasValue;
            }));
        // A scripted model replaces the worker connection entirely when requested, so the
        // whole product can be exercised without a provider key. Registered before the
        // loopback runner because the last registration wins, and this one has to win.
        var scriptedRunner = RuBackend.Harness.ScriptedModelStepRunner.FromEnvironment();
        if (scriptedRunner is not null)
        {
            services.AddSingleton<IModelStepRunner>(scriptedRunner);
        }
        else
        {
            services.AddSingleton<IModelStepRunner>(services => services.GetRequiredService<LoopbackModelStepRunner>());
        }
        if (isManagedRuntime)
        {
            services.AddHostedService<RuAgent.Hosting.ParentProcessWatcher>();
        }
        // Non-blocking store health: Lazy<Task<...>> starts init asynchronously on first
        // use (or via warmup hosted service) instead of .GetAwaiter().GetResult() in a
        // singleton factory, which could deadlock the host startup thread pool.
        // Middleware awaits the task asynchronously and fails closed (503) until ready.
        services.AddSingleton<Lazy<Task<HarnessStoreHealth>>>(services =>
            new Lazy<Task<HarnessStoreHealth>>(
                () => HarnessStoreHealthInitializer.InitializeHarnessStoreHealthAsync(services.GetRequiredService<HarnessSqliteOptions>()),
                LazyThreadSafetyMode.ExecutionAndPublication));
        services.AddHostedService<HarnessStoreHealthWarmupService>();
        return services;
    }

    internal static IServiceCollection AddRuActivityAndDiagnostics(this IServiceCollection services)
    {
        services.AddSingleton<IActivityLogStore, ActivityLogStore>();
        services.AddHostedService<ActivityOutboxIngestionService>();
        services.AddSingleton(services =>
            new StructuredDiagnosticLogWriter(services.GetRequiredService<DataPaths>().LogRoot, "aspnetcore-"));
        services.AddSingleton<StructuredDiagnosticLogWriterHostedService>();
        services.AddSingleton<IHostedService>(services =>
            services.GetRequiredService<StructuredDiagnosticLogWriterHostedService>());
        services.AddSingleton<IDiagnosticEmitter, DiagnosticEmitter>();
        services.AddSingleton<ILoggerProvider, StructuredDiagnosticLoggerProvider>();
        services.AddSingleton(services =>
            new DiagnosticRetentionService(services.GetRequiredService<DataPaths>().LogRoot));
        services.AddHostedService(services =>
            services.GetRequiredService<DiagnosticRetentionService>());
        return services;
    }

    private static DataPaths BuildDataPaths(IConfiguration configuration)
    {
        var dataRoot = configuration["RU_DATA_ROOT"]
            ?? Environment.GetEnvironmentVariable("RU_DATA_ROOT");
        var logRoot = configuration["RU_LOG_ROOT"]
            ?? Environment.GetEnvironmentVariable("RU_LOG_ROOT");
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            userProfile = Directory.GetCurrentDirectory();
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = userProfile;
        }

        return DataPaths.Resolve(
            Directory.GetCurrentDirectory(),
            userProfile,
            localAppData,
            dataRoot,
            logRoot);
    }


    private static KnownSecretRedactor BuildKnownSecretRedactor()
    {
        // Seed the runtime redactor with values of process environment variables whose
        // names look secret-bearing, so known credentials never leak into payloads or
        // transient SSE frames. Names only decide membership; values become seeds.
        var secrets = new List<string>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string name || entry.Value is not string value || value.Length == 0)
            {
                continue;
            }

            var flattened = name.Replace("_", string.Empty).Replace("-", string.Empty);
            if (flattened.Contains("token", StringComparison.OrdinalIgnoreCase)
                || flattened.Contains("secret", StringComparison.OrdinalIgnoreCase)
                || flattened.Contains("password", StringComparison.OrdinalIgnoreCase)
                || flattened.Contains("passwd", StringComparison.OrdinalIgnoreCase)
                || flattened.Contains("apikey", StringComparison.OrdinalIgnoreCase)
                || flattened.Contains("credential", StringComparison.OrdinalIgnoreCase)
                || flattened.Contains("privatekey", StringComparison.OrdinalIgnoreCase)
                || flattened.Contains("accesskey", StringComparison.OrdinalIgnoreCase)
                || flattened.Contains("authkey", StringComparison.OrdinalIgnoreCase))
            {
                secrets.Add(value);
            }
        }

        return new KnownSecretRedactor(secrets);
    }

    private static AgentWorkerClientOptions BuildAgentWorkerClientOptions(
        IConfiguration configuration,
        int managedPort,
        string managedToken)
    {
        var configuredUrl = configuration["RuBackend:AgentWorkerUrl"];
        var workerUrl = !string.IsNullOrWhiteSpace(managedToken)
            ? $"ws://127.0.0.1:{managedPort}/worker/v2"
            : configuredUrl;
        var workerToken = !string.IsNullOrWhiteSpace(managedToken)
            ? managedToken
            : configuration["RU_AGENT_WORKER_TOKEN"]
                ?? Environment.GetEnvironmentVariable("RU_AGENT_WORKER_TOKEN")
                ?? string.Empty;

        return new AgentWorkerClientOptions(
            string.IsNullOrWhiteSpace(workerUrl)
                ? new Uri("ws://127.0.0.1:8005/worker/v2")
                : new Uri(workerUrl, UriKind.Absolute),
            workerToken);
    }
}

internal static class HarnessStoreHealthInitializer
{
    public static async Task<HarnessStoreHealth> InitializeHarnessStoreHealthAsync(
        HarnessSqliteOptions options,
        CancellationToken cancellationToken = default)
    {
        return await RuAgent.Harness.Persistence.HarnessSqliteSchema.InitializeAsync(options, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class HarnessStoreHealthWarmupService : IHostedService
{
    private readonly Lazy<Task<HarnessStoreHealth>> _healthLazy;
    private readonly ILogger<HarnessStoreHealthWarmupService> _logger;

    public HarnessStoreHealthWarmupService(
        Lazy<Task<HarnessStoreHealth>> healthLazy,
        ILogger<HarnessStoreHealthWarmupService> logger)
    {
        _healthLazy = healthLazy;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Trigger async initialization; result is cached in the Lazy.
        _ = _healthLazy.Value.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _logger.LogWarning("Harness store health initialization failed: {Error}", t.Exception?.GetBaseException().Message);
            }
            else if (t.Result.IsAvailable)
            {
                _logger.LogInformation("Harness store initialized (schema v{Version})", t.Result.SchemaVersion);
            }
            else
            {
                _logger.LogWarning("Harness store unavailable: {ErrorCode}", t.Result.ErrorCode);
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Points <see cref="AgentV1Service"/> at the provider catalog so image-input support
/// follows what the provider declares for the selected model rather than a guess made
/// from the model name.
/// </summary>
internal sealed class AgentV1CatalogVisionConfigurator(
    ProviderCatalogService catalog,
    ISettingsStore? settingsStore) : IPostConfigureOptions<AgentV1Service>
{
    public void PostConfigure(string? name, AgentV1Service service)
    {
        service.DeclaredVision = async (model, ct) =>
        {
            // Absence is not a verdict: no settings store means no selected model to
            // look up, and a catalog that cannot be read says nothing about the model.
            // Either way the name check decides rather than this throwing.
            if (settingsStore is null) return null;
            var settings = await settingsStore.ReadAsync(ct).ConfigureAwait(false);
            try
            {
                return await catalog.DeclaresVisionAsync(settings.Backend, model, ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                return null;
            }
        };
    }
}
