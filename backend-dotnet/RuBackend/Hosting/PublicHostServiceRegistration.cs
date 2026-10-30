using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http;
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
using RuBackend.Harness.ModelRunner;
using RuBackend.Harness.Workspace;
using RuBackend.Providers;
using RuBackend.Runtime;
using RuBackend.Security;
using RuAgent.Broker.Configuration;
using RuAgent.Broker.Tools.Web;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Approval;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Core.Reconciliation;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Policy;
using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Harness.Persistence.Workspace;
using RuAgent.Hosting;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Models;
using RuAgent.Runtime.Model;

namespace RuBackend.Hosting;

// DI composition groups for the Public Host. Moved verbatim from Program.cs;
// call order in Program.cs is unchanged so registration semantics are identical.
internal static class PublicHostServiceRegistration
{
    internal static IServiceCollection AddRuFoundation(
        this IServiceCollection services,
        IConfiguration configuration,
        string? managedPublicBearer)
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
        services.AddHostedService<WorkspaceJournalRetentionService>();
        services.AddSingleton<WorkspaceTrustRequestStore>(services =>
            new WorkspaceTrustRequestStore(services.GetRequiredService<HarnessSqliteOptions>()));
        services.AddSingleton<WorkspaceTrustService>();
        services.AddSingleton<IIdGenerator, CryptographicIdGenerator>();
        services.AddSingleton<ITimeProvider, SystemTimeProvider>();
        services.AddSingleton<IHarnessSessionArtifactStore, HarnessSessionArtifactStore>();
        services.AddSingleton(TimeProvider.System);
        // HTTP activity for the managed host's idle shutdown; registered always so the
        // request-activity middleware can resolve it in every hosting mode.
        services.AddSingleton<RuAgent.Hosting.HostActivityTracker>();
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
        services.AddSingleton<RuAgent.Harness.Persistence.Subagents.SqliteSubagentStore>();
        services.AddSingleton<RuAgent.Harness.Core.Subagents.ISubagentInbox>(services =>
            services.GetRequiredService<RuAgent.Harness.Persistence.Subagents.SqliteSubagentStore>());
        services.AddSingleton<RuAgent.Harness.Persistence.Subagents.SqliteSubagentBudgetStore>();
        services.AddSingleton<RuBackend.Harness.Subagents.SubagentFamilyUsageReader>();
        services.AddSingleton<RuAgent.Harness.Core.Subagents.IAgentFamilyBudget,
            RuBackend.Harness.Subagents.SubagentFamilyBudget>();
        services.AddSingleton<RuAgent.Harness.Persistence.Subagents.SqliteSubagentScopeStore>();
        services.AddSingleton<RuAgent.Harness.Persistence.Subagents.SqliteSubagentWorkspaceAllocationStore>();
        services.AddSingleton<RuBackend.Harness.Subagents.SubagentRuntimeBinder>();
        services.AddSingleton<RuBackend.Harness.Subagents.SubagentScheduler>();
        services.AddSingleton<RuBackend.Harness.Subagents.SubagentCancellationCoordinator>();
        services.AddSingleton<RuBackend.Harness.Subagents.ISubagentModelSelectionProvider,
            RuBackend.Harness.Subagents.SubagentModelSelectionProvider>();
        services.AddSingleton<RuBackend.Harness.Subagents.SubagentWorkspaceStorage>(services =>
            new RuBackend.Harness.Subagents.SubagentWorkspaceStorage(
                Path.Combine(Path.GetDirectoryName(services.GetRequiredService<HarnessSqliteOptions>().DatabasePath)!, "subagent-workspaces")));
        services.AddSingleton<RuBackend.Harness.Subagents.ISubagentWorkspaceFactory>(services =>
            new RuBackend.Harness.Subagents.SubagentWorkspaceFactory(
                services.GetRequiredService<IToolGatewayFactory>(), services.GetRequiredService<WorkspaceTrustService>(),
                services.GetRequiredService<RuBackend.Harness.Subagents.SubagentWorkspaceStorage>().Root,
                services.GetRequiredService<RuAgent.Harness.Persistence.Subagents.SqliteSubagentWorkspaceAllocationStore>()));
        services.AddSingleton<RuBackend.Harness.Subagents.SubagentRunFactory>();
        services.AddSingleton<RuBackend.Harness.Subagents.SubagentContinuationFactory>();
        services.AddSingleton<RuBackend.Harness.Subagents.SubagentContinuationCoordinator>();
        services.AddSingleton<RuBackend.Harness.Subagents.SubagentRecoveryCoordinator>();
        services.AddSingleton<RuAgent.Harness.Persistence.Subagents.SqliteSubagentResultStore>();
        services.AddSingleton<RuAgent.Harness.Persistence.Subagents.SqliteSubagentChangeSetStore>();
        services.AddSingleton<RuAgent.Harness.Persistence.Subagents.SqliteSubagentApplyJournal>();
        services.AddSingleton<RuBackend.Harness.Subagents.SubagentCodingResultPublisher>();
        services.AddSingleton<RuBackend.Harness.Subagents.SubagentArtifactCleanupCoordinator>();
        services.AddSingleton<RuBackend.Harness.Subagents.SubagentApplyService>();
        services.AddSingleton<RuAgent.Harness.Persistence.Subagents.SqliteSubagentSnapshotReader>();
        services.AddSingleton<RuAgent.Harness.Persistence.Subagents.SqliteSubagentUserApplyStore>();
        services.AddSingleton<RuBackend.Harness.Subagents.SubagentUserApplyCoordinator>();
        services.AddSingleton<RuBackend.Harness.Subagents.SubagentPublicService>();
        services.AddSingleton<RuBackend.Harness.Subagents.SubagentLivePublisher>();
        services.AddSingleton<RuBackend.Harness.Subagents.SubagentControlDispatcher>();
        services.AddSingleton<RuBackend.Harness.Subagents.SubagentWaitCoordinator>();
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
        services.AddSingleton<SessionWorkStateCryptography>(services =>
            new SessionWorkStateCryptography(services.GetRequiredService<IHarnessKeyStore>()));
        services.AddSingleton<SessionWorkStateStore>();
        services.AddSingleton<RetainedApprovalRehydrator>();
        services.AddSingleton<RetainedInputRehydrator>();
        services.AddSingleton<RuBackend.Harness.Policy.HarnessPolicySnapshotService>();
        services.AddSingleton<SqliteHarnessEventStore>(services =>
            new SqliteHarnessEventStore(
                services.GetRequiredService<HarnessSqliteOptions>(),
                services.GetRequiredService<HarnessPayloadStore>(),
                services.GetRequiredService<SessionWorkStateStore>()));
        services.AddSingleton<IHarnessEventStore>(services => services.GetRequiredService<SqliteHarnessEventStore>());
        services.AddSingleton<HarnessLiveEventHub>();
        services.AddSingleton(services => new AgentRunSummaryLivePublisher(
            services.GetRequiredService<HarnessLiveEventHub>(), services.GetRequiredService<KnownSecretRedactor>(),
            new RuAgent.Harness.Persistence.Context.SqliteModelContextSource(
                services.GetRequiredService<HarnessSqliteOptions>(), services.GetRequiredService<HarnessPayloadStore>()),
            services.GetRequiredService<ILogger<AgentRunSummaryLivePublisher>>()));
        // Headless runtime loop: per-run coordinators, outbox delivery, and model/tool driving.
        services.AddSingleton<AgentRuntimeWakeSignal>();
        services.AddSingleton(services => new RuBackend.Harness.Hooks.RunLifecycleHooks(
            new RuBackend.Harness.Hooks.HookProcessRunner(),
            services.GetRequiredService<ILogger<RuBackend.Harness.Hooks.RunLifecycleHooks>>(),
            services.GetRequiredService<IHostApplicationLifetime>(),
            async (run, cancellationToken) =>
            {
                var workspaces = services.GetRequiredService<RuAgent.Harness.Persistence.Workspace.SqliteWorkspaceStore>();
                var stored = await workspaces.GetAsync(run.WorkspaceId, cancellationToken).ConfigureAwait(false);
                return stored is not null
                    && await workspaces.IsTrustedAsync(stored.Identity, run.Preset, cancellationToken).ConfigureAwait(false);
            }));
        services.AddSingleton<IAgentRuntimeRootRegistry, AgentRuntimeRootRegistry>();
        services.AddSingleton<RuAgent.Harness.Core.Reconciliation.ReconciliationInspectionGate>();
        services.AddSingleton<IAgentRuntimeCoordinatorFactory, AgentRuntimeCoordinatorFactory>();
        // The Host's execution authority is in-process: one runtime per process,
        // composed lazily by the authority so a host that never executes a tool
        // never creates the tool service or touches the spool root. The gateway
        // factory is the seam every call site resolves; it takes the lazy
        // runtime so constructing it at host startup does not compose it.
        // MCP servers (ADR 0025): the user's own ~/.ru/mcp.json, credentials read from
        // Windows Credential Manager by entry name when a server starts.
        services.AddSingleton(_ => new RuAgent.Broker.Tools.Mcp.McpServerHost(
            () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            name => new RuBackend.Business.Settings.WindowsCredentialApi().Read(name)?.Secret));
        services.AddSingleton(_ => new RuAgent.Broker.Skills.SkillPackageInstaller(
            RuAgent.Protocol.SkillRoots.UserInstallationRoot()));
        // Computer use (ADR 0031): one desktop controller per Host. The stop hotkey is
        // registered on the first action, and pressing it cancels the controlling run.
        services.AddSingleton(_ => new RuAgent.Broker.Tools.Computer.ComputerStopHotkey());
        services.AddSingleton(services => new RuAgent.Broker.Tools.Computer.ComputerUseService(
            new RuAgent.Broker.Tools.Computer.WindowsDesktopController(),
            services.GetRequiredService<RuAgent.Broker.Tools.Computer.ComputerStopHotkey>()));
        services.AddHostedService<RuBackend.Harness.ComputerUseStopService>();
        services.AddSingleton(services => new InProcessExecutionAuthority(
            services.GetRequiredService<DataPaths>(),
            services.GetRequiredService<ILogger<InProcessExecutionAuthority>>(),
            mcpServers: services.GetRequiredService<RuAgent.Broker.Tools.Mcp.McpServerHost>(),
            programJournal: services.GetRequiredService<HarnessPayloadStore>(),
            computer: services.GetRequiredService<RuAgent.Broker.Tools.Computer.ComputerUseService>()));
        services.AddSingleton<IToolGatewayFactory>(services =>
            new InProcessToolGatewayFactory(
                services.GetRequiredService<InProcessExecutionAuthority>().Runtime,
                new Lazy<RuBackend.Harness.Subagents.SubagentControlDispatcher>(() =>
                    services.GetRequiredService<RuBackend.Harness.Subagents.SubagentControlDispatcher>()))
            {
                ImageInputAccepted = async (runId, ct) => RuBackend.Providers.ImageInputPolicy.IsPinned(
                    (await services.GetRequiredService<SqliteAgentRunProviderSelectionStore>()
                        .GetAsync(new RunId(runId), ct).ConfigureAwait(false))?.Selection),
                  ObserveToolResult = (lease, tool, output, ct) => services.GetRequiredService<AgentRuntimeLoopService>()
                      .ObserveFileToolResultAsync(lease.CanonicalRoot, tool, output, ct),
            });
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
        bool isManagedRuntime)
    {
        services.AddSingleton<HarnessOutboxStore>();
        services.AddSingleton<RuBackend.Harness.IHarnessModelContextBootstrapper, RuBackend.Harness.HarnessModelContextBootstrapper>();
        services.AddSingleton<AgentConversationActivityProjector>();
        services.AddSingleton<AgentRunHistoryTransitionSink>();
        services.AddSingleton<IAgentHistoryTransitionSink>(services =>
            services.GetRequiredService<AgentRunHistoryTransitionSink>());
        services.AddSingleton<AgentConversationOutboxDispatcher>();
        // AddHostedService<T> only wires IHostedService; the idle probe resolves the
        // concrete loop, so the singleton must own the instance and the hosted
        // registration must forward to it (two registrations would create two loops).
        services.AddSingleton<AgentRuntimeLoopService>();
        services.AddHostedService(services => services.GetRequiredService<AgentRuntimeLoopService>());
        // Session deletion resolves the same singleton loop: the runtime half of
        // deleting a session cancels its in-flight runs before the rows vanish.
        services.AddSingleton<IAgentRuntimeRunCleanup>(services =>
            services.GetRequiredService<AgentRuntimeLoopService>());
        services.AddSingleton<AgentV1SubagentGuard>();
        services.AddSingleton<AgentRunWorkspaceAdmissionService>();
        services.AddSingleton<AgentRunCreationService>();
        services.AddSingleton<AgentRunQueryService>();
        services.AddSingleton<AgentRunLifecycleService>();
        services.AddSingleton<AgentApprovalsService>();
        services.AddSingleton<AgentPoliciesService>();
        services.AddSingleton<AgentWorkspaceService>();
        services.AddSingleton<AgentWorkspaceTrustService>();
        services.AddSingleton<AgentActivitiesService>();
        services.AddSingleton<AgentCommandsService>();
        services.AddSingleton<AgentInputsService>();
        // Harness Core adapters: the typed in-process model step service. One
        // Request deadlines end at response headers; the adapters retain an infinite
        // whole-request timeout so healthy long SSE streams are bounded by their idle
        // watchdog. Disable redirects here so provider credentials and prompts never
        // follow a 307/308 to another origin.
        services.AddSingleton(_ => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        });
        services.AddSingleton<IModelProviderResolver>(services =>
        {
            var modelHttpClient = services.GetRequiredService<HttpClient>();
            return new ModelProviderResolver(new Dictionary<string, IModelProvider>
            {
                ["chat_completions"] = new OpenAiCompatibleModelProvider(modelHttpClient),
                ["openai_responses"] = new OpenAiResponsesModelProvider(modelHttpClient),
                ["anthropic_messages"] = new AnthropicMessagesModelProvider(modelHttpClient),
            });
        });
        // Local single-slot runtimes (llama.cpp, vLLM, Ollama) accept one request
        // at a time; the operator lowers the bound for those with the env var.
        services.AddSingleton(services => new ModelStepService(
            services.GetRequiredService<IModelProviderResolver>(),
            ModelStepService.ResolveMaxConcurrentProviderSteps(
                Environment.GetEnvironmentVariable(ModelStepService.MaxConcurrentProviderStepsVariable))));
        services.AddSingleton(services => new SqliteAgentRunProviderSelectionStore(
            services.GetRequiredService<HarnessSqliteOptions>()));
        services.AddSingleton(services => new AgentProviderSnapshotFactory(
            services.GetRequiredService<ISettingsStore>(), services.GetRequiredService<ICredentialStore>(),
            services.GetRequiredService<ProviderCatalogService>(),
            services.GetRequiredService<SqliteAgentRunProviderSelectionStore>(),
            async (runId, ct) =>
            {
                var run = await services.GetRequiredService<IHarnessEventStore>().GetRunAsync(runId, ct);
                return run is null || run.TerminalAt.HasValue;
            }));
        services.AddSingleton(services => new SqliteProviderContinuationReader(
            services.GetRequiredService<HarnessSqliteOptions>(),
            services.GetRequiredService<HarnessPayloadStore>()));
        services.AddSingleton(services =>
            new InProcessModelStepRunner(services.GetRequiredService<ModelStepService>())
            {
                CreateProviderBootstrapAsync = (runId, ct) =>
                    services.GetRequiredService<AgentProviderSnapshotFactory>().GetAsync(runId, ct),
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
                LoadProviderContinuationsAsync = (runId, bootstrap, retainedAssistantEventIds, ct) =>
                    services.GetRequiredService<SqliteProviderContinuationReader>()
                        .ReadAsync(runId, bootstrap, retainedAssistantEventIds, ct),
            });
        // A scripted model replaces the in-process provider call entirely when requested,
        // so the whole product can be exercised without a provider key. Registered before
        // the in-process runner because the last registration wins, and this one has to win.
        var scriptedRunner = RuBackend.Harness.ScriptedModelStepRunner.FromEnvironment();
        if (scriptedRunner is not null)
        {
            services.AddSingleton<IModelStepRunner>(scriptedRunner);
        }
        else
        {
            services.AddSingleton<IModelStepRunner>(services => services.GetRequiredService<InProcessModelStepRunner>());
            services.AddSingleton<IAgentRunModelMetadataResolver>(services =>
                new AgentRunModelMetadataResolver(
                    services.GetRequiredService<AgentProviderSnapshotFactory>(),
                    services.GetRequiredService<SqliteAgentRunProviderSelectionStore>()));
        }
        if (isManagedRuntime)
        {
            services.AddHostedService<RuAgent.Hosting.ParentProcessWatcher>();
            // A managed host outlives the terminal that spawned it, so it also stops on
            // its own once nothing has used it for the idle window (RU_HOST_IDLE_MINUTES,
            // default 10, 0 disables). Clients restart it through GetOrStartAsync.
            services.AddSingleton<RuAgent.Hosting.IHostIdleProbe>(services =>
                services.GetRequiredService<AgentRuntimeLoopService>());
            services.AddHostedService(services => new RuAgent.Hosting.HostIdleShutdownService(
                services.GetRequiredService<IHostApplicationLifetime>(),
                services.GetRequiredService<RuAgent.Hosting.HostActivityTracker>(),
                services.GetRequiredService<ILogger<RuAgent.Hosting.HostIdleShutdownService>>(),
                services.GetRequiredService<TimeProvider>(),
                services.GetRequiredService<RuAgent.Hosting.IHostIdleProbe>()));
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
