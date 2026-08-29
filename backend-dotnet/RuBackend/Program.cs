using RuBackend.Business;
using RuBackend.Business.Activity;
using RuBackend.Business.Sessions;
using RuBackend.Business.Settings;
using RuBackend.Agent;
using RuBackend.Configuration;
using RuBackend.Conversation;
using RuBackend.Diagnostics;
using RuBackend.Harness;
using RuBackend.Harness.Broker;
using RuBackend.Harness.Worker;
using RuBackend.Harness.Workspace;
using RuBackend.Memory;
using RuBackend.Runtime;
using RuBackend.Security;
using RuBackend.Speech;
using RuBackend.Vts;
using RuBackend.Worker;
using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Policy;
using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Harness.Persistence.Workspace;
using RuAgent.Hosting;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Bootstrap;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

var isManagedRuntime = args.Contains("--managed-runtime", StringComparer.Ordinal);
RuAgent.Hosting.PublicHostSingleInstance? managedSingleInstance = null;
RuAgent.Hosting.WorkerBrokerProcessManager? managedWorkerBrokerManager = null;
RuAgent.Hosting.RuntimeDescriptorWriter? managedDescriptorWriter = null;
RuAgent.Hosting.RuntimeClientLeaseRegistry? managedLeaseRegistry = null;
var managedAgentWorkerToken = string.Empty;
var managedDeviceWorkerToken = string.Empty;
var managedAgentWorkerPort = PublicHostRuntimeOptions.DefaultWorkerPort;
var managedDeviceWorkerPort = PublicHostRuntimeOptions.DefaultDeviceWorkerPort;

var builder = WebApplication.CreateBuilder(args);
var migrationMode = RuntimeMigrationModeParser.Parse(
    builder.Configuration["RuBackend:RuntimeMigrationMode"]);
builder.Logging.ClearProviders();
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

// Managed runtime bootstrap BEFORE DI: resolve paths, check port conflict, acquire single instance, generate credentials, start Worker/Broker, publish descriptor
string? managedPublicBearer = null;
if (isManagedRuntime)
{
    var configDataRoot = builder.Configuration["RU_DATA_ROOT"] ?? Environment.GetEnvironmentVariable("RU_DATA_ROOT");
    var configLogRoot = builder.Configuration["RU_LOG_ROOT"] ?? Environment.GetEnvironmentVariable("RU_LOG_ROOT");
    var projectRoot = Directory.GetCurrentDirectory();
    // Resolve descriptor path via hosting options
    var descriptorPath = RuAgent.Hosting.PublicHostRuntimeOptions.ResolveDescriptorPath(configDataRoot, configLogRoot, projectRoot);
    managedDescriptorWriter = new RuAgent.Hosting.RuntimeDescriptorWriter(descriptorPath);

    // Determine public port from BindUrl
    var bindUrl = builder.Configuration["RuBackend:BindUrl"] ?? "http://127.0.0.1:8000";
    int publicPort = 8000;
    if (Uri.TryCreate(bindUrl, UriKind.Absolute, out var bindUri))
        publicPort = bindUri.Port;

    // Port conflict check: if port occupied and no valid owned descriptor, fail fast
    bool IsPortOccupied(int port)
    {
        try { using var l = new TcpListener(IPAddress.Loopback, port); l.Start(); l.Stop(); return false; }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse) { return true; }
        catch { return false; }
    }

    if (IsPortOccupied(publicPort) && managedDescriptorWriter.IsStale())
    {
        Console.Error.WriteLine("{\"error_code\":\"public_host_port_conflict\"}");
        // Do not attach to or kill foreign process; exit immediately
        Environment.Exit(2);
    }

    // Single instance mutex (global per port)
    managedSingleInstance = new RuAgent.Hosting.PublicHostSingleInstance($"Global\\RuPublicHost-{publicPort}");
    if (!managedSingleInstance.IsAcquired)
    {
        // Try Local fallback
        managedSingleInstance.Dispose();
        managedSingleInstance = new RuAgent.Hosting.PublicHostSingleInstance($"Local\\RuPublicHost-{publicPort}");
        if (!managedSingleInstance.IsAcquired)
        {
            Console.Error.WriteLine("{\"error_code\":\"public_host_already_running\"}");
            Environment.Exit(3);
        }
    }

    // Generate independent private credentials for the Agent and device workers.
    managedAgentWorkerToken = RuAgent.Hosting.PublicHostRuntimeOptions.GenerateBearerToken();
    managedDeviceWorkerToken = RuAgent.Hosting.PublicHostRuntimeOptions.GenerateBearerToken();
    managedPublicBearer = builder.Configuration["RU_DESKTOP_TOKEN"] ?? Environment.GetEnvironmentVariable("RU_DESKTOP_TOKEN");
    if (string.IsNullOrWhiteSpace(managedPublicBearer))
    {
        managedPublicBearer = RuAgent.Hosting.PublicHostRuntimeOptions.GenerateBearerToken();
        Environment.SetEnvironmentVariable("RU_DESKTOP_TOKEN", managedPublicBearer);
        builder.Configuration["RU_DESKTOP_TOKEN"] = managedPublicBearer;
        builder.Configuration["RuBackend:DesktopToken"] = managedPublicBearer;
    }
    var configuredLogRoot = builder.Configuration["RU_LOG_ROOT"]
        ?? Environment.GetEnvironmentVariable("RU_LOG_ROOT");
    var managedLogRoot = PublicHostRuntimeOptions.ResolveLogRoot(projectRoot, configuredLogRoot);
    Environment.SetEnvironmentVariable("RU_LOG_ROOT", managedLogRoot);
    builder.Configuration["RU_LOG_ROOT"] = managedLogRoot;

    // Resolve ports before creating the protected bootstrap. The manager uses the
    // same log/session root and returns the final values after readiness succeeds.
    managedAgentWorkerPort = ReadConfiguredPort(
        builder.Configuration["RuBackend:AgentWorkerPort"]
            ?? Environment.GetEnvironmentVariable("RU_BACKEND_AGENT_WORKER_PORT")
            ?? Environment.GetEnvironmentVariable("RU_BACKEND_WORKER_PORT"),
        PublicHostRuntimeOptions.DefaultWorkerPort);
    managedDeviceWorkerPort = ReadConfiguredPort(
        builder.Configuration["RuBackend:DeviceWorkerPort"]
            ?? Environment.GetEnvironmentVariable("RU_BACKEND_DEVICE_WORKER_PORT"),
        PublicHostRuntimeOptions.DefaultDeviceWorkerPort);

    var startupSettings = new SettingsStore(BuildDataPaths(builder.Configuration));
    var startupCredentials = new WindowsCredentialStore();
    var bootstrapRoot = Path.Combine(managedLogRoot, "agent", "session");
    var providerBootstrapPath = migrationMode == RuntimeMigrationMode.Dotnet
        ? new ManagedProviderBootstrapFactory()
            .CreateAsync(
                bootstrapRoot,
                startupSettings.ReadAsync(CancellationToken.None).GetAwaiter().GetResult(),
                startupCredentials,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult()
        : string.Empty;

    // Start Worker and Broker in one Job
    managedWorkerBrokerManager = new RuAgent.Hosting.WorkerBrokerProcessManager();
    var startup = new WorkerBrokerStartupOptions(
        migrationMode,
        managedAgentWorkerToken,
        managedDeviceWorkerToken,
        providerBootstrapPath,
        managedAgentWorkerPort,
        managedDeviceWorkerPort,
        null,
        Environment.GetEnvironmentVariable("RU_AGENT_BROKER_EXE"));
    var workerStarted = managedWorkerBrokerManager
        .StartAsync(projectRoot, startup)
        .GetAwaiter()
        .GetResult();
    if (!workerStarted)
    {
        if (migrationMode == RuntimeMigrationMode.Dotnet
            && !string.IsNullOrWhiteSpace(providerBootstrapPath))
        {
            try
            {
                ProtectedProviderBootstrap.Delete(bootstrapRoot, providerBootstrapPath);
            }
            catch
            {
                // Startup must still fail closed; do not expose cleanup details.
            }
        }

        Console.Error.WriteLine($"Worker/Broker start failed: {managedWorkerBrokerManager.LastError}");
        Environment.Exit(4);
    }

    managedAgentWorkerPort = managedWorkerBrokerManager.AgentWorkerPort;
    managedDeviceWorkerPort = managedWorkerBrokerManager.DeviceWorkerPort;

    // Propagate broker descriptor to Host's env for Broker client
    var brokerDescriptorPath = managedWorkerBrokerManager.BrokerDescriptorPath;
    if (!string.IsNullOrWhiteSpace(brokerDescriptorPath))
    {
        Environment.SetEnvironmentVariable("RU_AGENT_BROKER_DESCRIPTOR", brokerDescriptorPath);
        builder.Configuration["RU_AGENT_BROKER_DESCRIPTOR"] = brokerDescriptorPath;
    }

    // Publish ACL-restricted descriptor after children are running and before health is available
    var proc = Process.GetCurrentProcess();
    var descriptor = new RuntimeDescriptor(
        RuntimeDescriptor.CurrentProtocolVersion,
        $"http://127.0.0.1:{publicPort}/",
        managedPublicBearer,
        proc.Id,
        proc.StartTime.ToUniversalTime().ToString("O"),
        RuAgent.Hosting.PublicHostRuntimeOptions.GenerateNonce(),
        DateTimeOffset.UtcNow.ToString("O"));
    managedDescriptorWriter.WriteAsync(descriptor).GetAwaiter().GetResult();

    // Lease registry and idle monitor
    managedLeaseRegistry = new RuAgent.Hosting.RuntimeClientLeaseRegistry();
    // Idle monitor will be created after DataPaths etc but we can create here with lease registry; need hasActiveRun/hasApproval hooks
    // We'll create idle monitor as singleton later with proper hooks; for now keep registry singleton
}

builder.Services.Configure<BackendOptions>(options =>
{
    builder.Configuration.GetSection(BackendOptions.SectionName).Bind(options);
    if (string.IsNullOrWhiteSpace(options.DesktopToken))
    {
        options.DesktopToken = builder.Configuration["RU_DESKTOP_TOKEN"] ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(managedPublicBearer))
            options.DesktopToken = managedPublicBearer;
    }
});
builder.Services.AddSingleton(BuildDataPaths(builder.Configuration));
builder.Services.AddSingleton(BuildAgentWorkerClientOptions(
    builder.Configuration,
    migrationMode,
    managedAgentWorkerPort,
    managedAgentWorkerToken));
builder.Services.AddSingleton(BuildDeviceWorkerClientOptions(
    builder.Configuration,
    managedDeviceWorkerPort,
    managedDeviceWorkerToken));
builder.Services.AddSingleton<LoopbackWorkerClient>();
builder.Services.AddSingleton<IWorkerConnectionClient>(services =>
    services.GetRequiredService<LoopbackWorkerClient>());
builder.Services.AddSingleton<IRuntimeProtocolClient>(services =>
    services.GetRequiredService<LoopbackWorkerClient>());
builder.Services.AddSingleton<IVoiceProtocolClient>(services =>
    services.GetRequiredService<LoopbackWorkerClient>());
builder.Services.AddSingleton<IVtsProtocolClient>(services =>
    services.GetRequiredService<LoopbackWorkerClient>());
builder.Services.AddSingleton<IRuntimeWorkerClient, LoopbackRuntimeWorkerClient>();
builder.Services.AddSingleton<IAgentVtsClient, WorkerAgentVtsClient>();
builder.Services.AddSingleton<AgentReplyPolicy>();
builder.Services.AddSingleton<IAgentReplyPolicy>(services =>
    services.GetRequiredService<AgentReplyPolicy>());
builder.Services.AddSingleton<AgentReplyEffectSink>();
builder.Services.AddSingleton<IAgentReplyEffectSink>(services =>
    services.GetRequiredService<AgentReplyEffectSink>());
builder.Services.AddSingleton<IMetadataPolicy, MetadataPolicy>();
builder.Services.AddSingleton<IStreamSanitizer, StreamSanitizer>();


var brokerDescriptor = Environment.GetEnvironmentVariable("RU_AGENT_BROKER_DESCRIPTOR");
if (!string.IsNullOrWhiteSpace(brokerDescriptor))
{
    builder.Services.AddSingleton<IAgentBrokerClient>(_ => new AgentBrokerClient(brokerDescriptor));
}
builder.Services.AddSingleton<ISettingsStore, SettingsStore>();
builder.Services.AddSingleton<ICredentialStore, WindowsCredentialStore>();
builder.Services.AddSingleton<IRuntimeSettingsLoader, LoopbackRuntimeSettingsLoader>();
builder.Services.AddSingleton<ISettingsTransitionService, SettingsTransitionService>();
builder.Services.AddSingleton<ISessionStore, SessionStore>();
builder.Services.AddSingleton(services => new HarnessSqliteOptions(
    services.GetRequiredService<DataPaths>().AgentHarnessDatabase));
builder.Services.AddSingleton<HarnessFeatureOptions>(services =>
    new HarnessFeatureOptions(services.GetRequiredService<IConfiguration>()["RuBackend:AgentHarness:Version"]));
builder.Services.AddSingleton<WindowsWorkspaceIdentityResolver>();
builder.Services.AddSingleton<RuBackend.Harness.Prompting.RuInstructionLoader>();
builder.Services.AddSingleton<SqliteWorkspaceStore>(services =>
    new SqliteWorkspaceStore(services.GetRequiredService<HarnessSqliteOptions>()));
builder.Services.AddSingleton<WorkspaceTrustRequestStore>(services =>
    new WorkspaceTrustRequestStore(services.GetRequiredService<HarnessSqliteOptions>()));
builder.Services.AddSingleton<WorkspaceTrustService>();
builder.Services.AddSingleton<IIdGenerator, CryptographicIdGenerator>();
builder.Services.AddSingleton<ITimeProvider, SystemTimeProvider>();
builder.Services.AddSingleton<IHarnessSessionArtifactStore, HarnessSessionArtifactStore>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IHarnessSessionDeletionCoordinator,
    HarnessSessionDeletionCoordinator>();
builder.Services.AddSingleton<AgentSessionPermissionStore>(services =>
    new AgentSessionPermissionStore(services.GetRequiredService<HarnessSqliteOptions>()));
builder.Services.AddSingleton<IHarnessKeyStore>(services =>
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
    return new EphemeralHarnessKeyStore();
});
builder.Services.AddSingleton<KnownSecretRedactor>(_ => BuildKnownSecretRedactor());
builder.Services.AddSingleton<AgentReplyTransientRedactor>();
builder.Services.AddSingleton<ITransientContentRedactor>(services =>
    services.GetRequiredService<AgentReplyTransientRedactor>());
builder.Services.AddSingleton<HarnessPayloadCryptography>(services =>
    new HarnessPayloadCryptography(services.GetRequiredService<IHarnessKeyStore>()));
builder.Services.AddSingleton<HarnessPayloadStore>(services =>
    new HarnessPayloadStore(
        services.GetRequiredService<HarnessSqliteOptions>(),
        services.GetRequiredService<HarnessPayloadCryptography>(),
        services.GetRequiredService<KnownSecretRedactor>()));
builder.Services.AddSingleton<RuBackend.Harness.Policy.HarnessPolicySnapshotService>();
builder.Services.AddSingleton<IHarnessEventStore>(services =>
    new SqliteHarnessEventStore(
        services.GetRequiredService<HarnessSqliteOptions>(),
        services.GetRequiredService<HarnessPayloadStore>()));
builder.Services.AddSingleton<HarnessLiveEventHub>();
builder.Services.AddSingleton<AgentRunSummaryLivePublisher>();
// Headless runtime loop: per-run coordinators, outbox delivery, and model/tool driving.
builder.Services.AddSingleton<IAgentRuntimeRootRegistry, AgentRuntimeRootRegistry>();
builder.Services.AddSingleton<RuAgent.Harness.Core.Reconciliation.ReconciliationInspectionGate>();
builder.Services.AddSingleton<RuBackend.Harness.Reconciliation.IAgentApprovalGrants, RuBackend.Harness.Reconciliation.InMemoryAgentApprovalGrants>();
builder.Services.AddSingleton<IAgentRuntimeCoordinatorFactory, AgentRuntimeCoordinatorFactory>();
builder.Services.AddSingleton(sp => new RuBackend.Harness.Git.SqliteRunGitStateStore(
    Path.Combine(
        Path.GetDirectoryName(sp.GetRequiredService<HarnessSqliteOptions>().DatabasePath)!,
        "agent_git_state.db")));
builder.Services.AddSingleton(sp => new RuBackend.Harness.Reconciliation.WorkspaceStateSnapshotService(
    sp.GetRequiredService<RuBackend.Harness.Git.SqliteRunGitStateStore>(),
    runId => sp.GetRequiredService<IAgentRuntimeRootRegistry>()
        .Snapshot()
        .Where(pair => string.Equals(pair.RunId, runId.Value, StringComparison.Ordinal))
        .Select(pair => pair.WorkspaceRoot)
        .FirstOrDefault()));
builder.Services.AddHostedService<RuBackend.Harness.Reconciliation.HarnessRecoveryStartupService>();
builder.Services.AddSingleton<HarnessOutboxStore>();
builder.Services.AddSingleton<RuBackend.Harness.IHarnessModelContextBootstrapper, RuBackend.Harness.HarnessModelContextBootstrapper>();
builder.Services.AddSingleton<AgentMemoryContextProvider>();
builder.Services.AddSingleton<IAgentMemoryContextProvider>(services =>
    services.GetRequiredService<AgentMemoryContextProvider>());
builder.Services.AddSingleton<AgentConversationMemoryProjector>();
builder.Services.AddSingleton<AgentConversationActivityProjector>();
builder.Services.AddSingleton<AgentRunHistoryTransitionSink>();
builder.Services.AddSingleton<IAgentHistoryTransitionSink>(services =>
    services.GetRequiredService<AgentRunHistoryTransitionSink>());
builder.Services.AddSingleton<AgentConversationOutboxDispatcher>();
builder.Services.AddHostedService<AgentRuntimeLoopService>();
builder.Services.AddSingleton<AgentV1Service>();
// Harness Core adapters: Worker v2 and ToolGateway via factory
builder.Services.AddSingleton<LoopbackModelStepRunner>(services =>
    new LoopbackModelStepRunner(services.GetRequiredService<AgentWorkerClientOptions>()));
builder.Services.AddSingleton<IModelStepRunner>(services => services.GetRequiredService<LoopbackModelStepRunner>());
// Lease registry and idle monitor singletons (managed or not)
if (managedLeaseRegistry is not null)
{
    builder.Services.AddSingleton(managedLeaseRegistry);
    // Create idle monitor that checks active runs/approvals via store? For now simple suppressed check via lease and stub active checks
    // We'll provide a monitor that defaults to 10 min and suppressed while lease exists
    builder.Services.AddSingleton<RuntimeIdleMonitor>(services =>
    {
        var registry = services.GetRequiredService<RuntimeClientLeaseRegistry>();
        return new RuntimeIdleMonitor(registry);
    });
    builder.Services.AddHostedService<RuntimeIdleHostedService>();
}
else
{
    var fallbackRegistry = new RuntimeClientLeaseRegistry();
    builder.Services.AddSingleton(fallbackRegistry);
    builder.Services.AddSingleton<RuntimeIdleMonitor>(services => new RuntimeIdleMonitor(services.GetRequiredService<RuntimeClientLeaseRegistry>()));
}
builder.Services.AddSingleton<HarnessStoreHealth>(services =>
{
    var options = services.GetRequiredService<HarnessSqliteOptions>();
    try
    {
        var health = HarnessSqliteSchema.InitializeAsync(options, CancellationToken.None).GetAwaiter().GetResult();
        return health.IsAvailable ? new HarnessStoreHealth(true, health.SchemaVersion, null) : new HarnessStoreHealth(false, health.SchemaVersion, health.ErrorCode);
    }
    catch
    {
        return new HarnessStoreHealth(false, 0, "agent_store_unavailable");
    }
});
builder.Services.AddSingleton<IHistoryStore, HistoryStore>();
builder.Services.AddSingleton<IActivityLogStore, ActivityLogStore>();
builder.Services.AddSingleton<ActivityLogMigrationService>();
builder.Services.AddHostedService<ActivityOutboxIngestionService>();
builder.Services.AddSingleton(services =>
    new StructuredDiagnosticLogWriter(services.GetRequiredService<DataPaths>().LogRoot, "aspnetcore-"));
builder.Services.AddSingleton<IDiagnosticEmitter, DiagnosticEmitter>();
builder.Services.AddSingleton<ILoggerProvider, StructuredDiagnosticLoggerProvider>();
builder.Services.AddSingleton(services =>
    new DiagnosticRetentionService(services.GetRequiredService<DataPaths>().LogRoot));
builder.Services.AddHostedService(services =>
    services.GetRequiredService<DiagnosticRetentionService>());
builder.Services.AddSingleton(BuildMemorySqliteOptions(builder.Configuration));
builder.Services.AddSingleton<IMemoryRepository, SqliteMemoryRepository>();
builder.Services.AddSingleton<IMemoryPolicy, MemoryPolicy>();
builder.Services.AddSingleton<IMemoryService, MemoryService>();
builder.WebHost.UseUrls(
    builder.Configuration["RuBackend:BindUrl"] ?? "http://127.0.0.1:8000");

var app = builder.Build();

// Hook shutdown cleanup for managed runtime: descriptor cleanup, job shutdown
if (isManagedRuntime)
{
    var lifetime = app.Lifetime;
    lifetime.ApplicationStopping.Register(() =>
    {
        try { managedDescriptorWriter?.Cleanup(); } catch { }
        try { managedWorkerBrokerManager?.Dispose(); } catch { }
        try { managedSingleInstance?.Dispose(); } catch { }
    });
}

using (var scope = app.Services.CreateScope())
{
    var migration = scope.ServiceProvider.GetRequiredService<ActivityLogMigrationService>();
    await migration.MigrateAsync();
}

app.UseWebSockets();
app.UseMiddleware<CorrelationMiddleware>();
app.UseMiddleware<DesktopTokenMiddleware>();
app.UseMiddleware<RequestSizeMiddleware>();
app.UseMiddleware<DiagnosticRequestMiddleware>();
// Harness store health: disable only /agent/v1/* with agent_store_unavailable
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/agent/v1", StringComparison.OrdinalIgnoreCase))
    {
        var health = context.RequestServices.GetService<HarnessStoreHealth>();
        if (health is not null && !health.IsAvailable)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { error_code = "agent_store_unavailable" });
            return;
        }
    }

    await next();
});

app.MapMethods("/{**path}", new[] { HttpMethods.Options }, () => Results.NoContent());
VoiceStreamEndpoint.Map(app);
BusinessEndpoints.Map(app);
AgentV1Endpoints.Map(app);
DiagnosticEndpoints.Map(app);
MemoryEndpoints.Map(app);
StatusEndpoints.Map(app);
VoiceEndpoints.Map(app);
ObservationEndpoints.Map(app);
SttEndpoints.Map(app);
VtsEndpoints.Map(app);

app.Run();

static DataPaths BuildDataPaths(IConfiguration configuration)
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

static MemorySqliteOptions BuildMemorySqliteOptions(IConfiguration configuration)
{
    var dataRoot = configuration["RU_DATA_ROOT"]
        ?? Environment.GetEnvironmentVariable("RU_DATA_ROOT");
    var root = string.IsNullOrWhiteSpace(dataRoot)
        ? Path.Combine(Directory.GetCurrentDirectory(), "data", "conversations")
        : dataRoot.Trim();

    return new MemorySqliteOptions
    {
        DatabasePath = configuration["RuBackend:Memory:DatabasePath"]
            ?? Path.Combine(root, "memory.db"),
        LegacyTextsPath = configuration["RuBackend:Memory:LegacyTextsPath"]
            ?? Path.Combine(root, "memory_texts.json"),
        ReadOnly = false,
    };
}

static KnownSecretRedactor BuildKnownSecretRedactor()
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

static AgentWorkerClientOptions BuildAgentWorkerClientOptions(
    IConfiguration configuration,
    RuntimeMigrationMode migrationMode,
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
            ?? configuration["RU_WORKER_TOKEN"]
            ?? Environment.GetEnvironmentVariable("RU_WORKER_TOKEN")
            ?? string.Empty;

    // The mode is intentionally consumed only by startup selection. The client
    // contract remains explicit even for the trusted Python compatibility mode.
    _ = migrationMode;
    return new AgentWorkerClientOptions(
        string.IsNullOrWhiteSpace(workerUrl)
            ? new Uri("ws://127.0.0.1:8005/worker/v2")
            : new Uri(workerUrl, UriKind.Absolute),
        workerToken);
}

static DeviceWorkerClientOptions BuildDeviceWorkerClientOptions(
    IConfiguration configuration,
    int managedPort,
    string managedToken)
{
    var configuredUrl = configuration["RuBackend:DeviceWorkerUrl"];
    var workerUrl = !string.IsNullOrWhiteSpace(managedToken)
        ? $"ws://127.0.0.1:{managedPort}/worker/v1"
        : configuredUrl;
    var workerToken = !string.IsNullOrWhiteSpace(managedToken)
        ? managedToken
        : configuration["RU_DEVICE_WORKER_TOKEN"]
            ?? Environment.GetEnvironmentVariable("RU_DEVICE_WORKER_TOKEN")
            ?? configuration["RU_WORKER_TOKEN"]
            ?? Environment.GetEnvironmentVariable("RU_WORKER_TOKEN")
            ?? string.Empty;

    return new DeviceWorkerClientOptions(
        string.IsNullOrWhiteSpace(workerUrl)
            ? new Uri("ws://127.0.0.1:8006/worker/v1")
            : new Uri(workerUrl, UriKind.Absolute),
        workerToken);
}

static int ReadConfiguredPort(string? raw, int fallback)
{
    if (string.IsNullOrWhiteSpace(raw))
    {
        return fallback;
    }

    return int.TryParse(raw, out var parsed) ? parsed : fallback;
}

internal sealed class EphemeralHarnessKeyStore : RuAgent.Harness.Persistence.Privacy.IHarnessKeyStore
{
    private readonly byte[] _key;

    public EphemeralHarnessKeyStore()
    {
        _key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    }

    public ValueTask<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken)
    {
        var copy = new byte[32];
        Buffer.BlockCopy(_key, 0, copy, 0, 32);
        return ValueTask.FromResult(copy);
    }
}

internal sealed class RuntimeIdleHostedService : BackgroundService
{
    private readonly RuntimeIdleMonitor _monitor;
    private readonly IHostApplicationLifetime _lifetime;
    public RuntimeIdleHostedService(RuntimeIdleMonitor monitor, IHostApplicationLifetime lifetime)
    {
        _monitor = monitor;
        _lifetime = lifetime;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            if (_monitor.ShouldShutdown())
            {
                _lifetime.StopApplication();
                break;
            }
        }
    }
}

public partial class Program { }
