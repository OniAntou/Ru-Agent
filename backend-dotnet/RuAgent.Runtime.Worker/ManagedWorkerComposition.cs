using System.Net;
using System.Net.Http;
using RuAgent.Runtime.Adapters.Audio;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Devices;
using RuAgent.Runtime.Adapters.Models;
using RuAgent.Runtime.Adapters.Observation;
using RuAgent.Runtime.Adapters.Runtime;
using RuAgent.Runtime.Adapters.Speech;
using RuAgent.Runtime.Core.Contracts;
using RuAgent.Runtime.Core.Lifecycle;

namespace RuAgent.Runtime.Worker;

/// <summary>
/// Explicit object graph for the managed Worker. Agent v2 receives the one-shot
/// model bootstrap; device v1 receives only typed settings and adapters, never
/// the provider credential or bootstrap file.
/// </summary>
public sealed record ManagedWorkerCompositionOptions
{
    public ManagedWorkerCompositionOptions(
        int agentPort,
        int devicePort,
        string agentWorkerToken,
        string deviceWorkerToken,
        ProviderBootstrapData agentProviderBootstrap,
        string projectRoot)
    {
        ValidatePort(agentPort, nameof(agentPort));
        ValidatePort(devicePort, nameof(devicePort));
        if (agentPort != 0 && devicePort != 0 && agentPort == devicePort)
        {
            throw new ArgumentException("Agent and device worker ports must differ.");
        }

        AgentWorkerToken = ValidateToken(agentWorkerToken, nameof(agentWorkerToken));
        DeviceWorkerToken = ValidateToken(deviceWorkerToken, nameof(deviceWorkerToken));
        if (string.Equals(AgentWorkerToken, DeviceWorkerToken, StringComparison.Ordinal))
        {
            throw new ArgumentException("Agent and device worker tokens must differ.");
        }
        AgentProviderBootstrap = agentProviderBootstrap ?? throw new ArgumentNullException(nameof(agentProviderBootstrap));
        ProjectRoot = ResolveDirectory(projectRoot, nameof(projectRoot));
    }

    public int AgentPort { get; }

    public int DevicePort { get; }

    public string AgentWorkerToken { get; }

    public string DeviceWorkerToken { get; }

    public ProviderBootstrapData AgentProviderBootstrap { get; }

    public string ProjectRoot { get; }

    public string? LogRoot { get; init; }

    public Uri? VoiceEndpoint { get; init; }

    public Uri? VtubeStudioEndpoint { get; init; }

    private static void ValidatePort(int port, string parameterName)
    {
        if (port is < 0 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static string ValidateToken(string? token, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(token)
            || token.Length > 4_096
            || token.Any(character => character > 127 || char.IsWhiteSpace(character)))
        {
            throw new ArgumentException("Worker token is invalid.", parameterName);
        }

        return token;
    }

    private static string ResolveDirectory(string? path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Project root is required.", parameterName);
        }

        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Project root '{fullPath}' was not found.");
        }

        return fullPath;
    }
}

/// <summary>
/// Optional seams used by deterministic tests and future provider replacement.
/// Null values select the safe managed default for that boundary.
/// </summary>
public sealed class ManagedWorkerCompositionDependencies
{
    public IModelProvider? ModelProvider { get; init; }

    public IVoiceProvider? VoiceProvider { get; init; }

    public OmniVoiceProcessController? OmniVoiceProcess { get; init; }

    public HttpClient? VoiceHttpClient { get; init; }

    public ISpeechRecognizer? SpeechRecognizer { get; init; }

    public IVtubeStudioClient? VtubeStudioClient { get; init; }

    public IObservationProvider? ObservationProvider { get; init; }

    public IManagedRuntimeApplier? RuntimeApplier { get; init; }

    public RuntimeLeaseCoordinator? SharedLeases { get; init; }

    public HttpClient? ObservationHttpClient { get; init; }
}

public sealed class ManagedWorkerComposition : IAsyncDisposable
{
    private readonly List<object> _owned = [];
    private int _disposed;

    private ManagedWorkerComposition(
        RuntimeLeaseCoordinator leases,
        ManagedRuntimeSettingsService settings,
        WorkerV2StepService agentSteps,
        WorkerV2Server agentServer,
        ManagedWorkerV1RequestRouter deviceRouter,
        WorkerV1Server deviceServer,
        IVoiceProvider? voiceProvider,
        ISpeechRecognizer speechRecognizer,
        IVtubeStudioClient vtsClient,
        IObservationProvider observationProvider,
        OmniVoiceProcessController? omniVoiceProcess)
    {
        Leases = leases;
        SharedLeases = leases;
        Settings = settings;
        AgentSteps = agentSteps;
        AgentServer = agentServer;
        DeviceRouter = deviceRouter;
        DeviceServer = deviceServer;
        VoiceProvider = voiceProvider;
        SpeechRecognizer = speechRecognizer;
        VtubeStudioClient = vtsClient;
        ObservationProvider = observationProvider;
        OmniVoiceProcess = omniVoiceProcess;
    }

    public RuntimeLeaseCoordinator Leases { get; }

    /// <summary>Alias used by diagnostics to make shared ownership explicit.</summary>
    public RuntimeLeaseCoordinator SharedLeases { get; }

    public ManagedRuntimeSettingsService Settings { get; }

    public WorkerV2StepService AgentSteps { get; }

    public WorkerV2Server AgentServer { get; }

    public ManagedWorkerV1RequestRouter DeviceRouter { get; }

    public WorkerV1Server DeviceServer { get; }

    public IVoiceProvider? VoiceProvider { get; }

    public ISpeechRecognizer SpeechRecognizer { get; }

    public IVtubeStudioClient VtubeStudioClient { get; }

    public IObservationProvider ObservationProvider { get; }

    public OmniVoiceProcessController? OmniVoiceProcess { get; }

    public static ManagedWorkerComposition Create(
        ManagedWorkerCompositionOptions options,
        ManagedWorkerCompositionDependencies? dependencies = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        dependencies ??= new ManagedWorkerCompositionDependencies();

        var owned = new List<object>();
        try
        {
            if (dependencies.OmniVoiceProcess is not null)
            {
                // The composition owns provider lifecycle even when a test or
                // host supplies a custom process controller.
                owned.Add(dependencies.OmniVoiceProcess);
            }

            var leases = dependencies.SharedLeases ?? Own(owned, new RuntimeLeaseCoordinator());
            var settings = new ManagedRuntimeSettingsService(
                InitialSettings(options.AgentProviderBootstrap),
                dependencies.RuntimeApplier);

            var modelProvider = dependencies.ModelProvider;
            if (modelProvider is null)
            {
                var modelHttpClient = Own(owned, new HttpClient());
                modelProvider = new OpenAiCompatibleModelProvider(modelHttpClient);
            }

            var agentOptions = new WorkerV2Options(
                options.AgentPort,
                options.AgentWorkerToken,
                options.AgentProviderBootstrap.Endpoint,
                options.AgentProviderBootstrap.Model,
                options.AgentProviderBootstrap.Credential);
            var agentSteps = new WorkerV2StepService(modelProvider, agentOptions, leases);
            var agentServer = new WorkerV2Server(options.AgentWorkerToken, agentSteps);

            var voiceProvider = dependencies.VoiceProvider;
            var omniVoiceProcess = dependencies.OmniVoiceProcess;
            if (voiceProvider is null)
            {
                var voiceEndpoint = ResolveVoiceEndpoint(options);
                var processOptions = TryBuildOmniVoiceProcessOptions(options.ProjectRoot, voiceEndpoint);
                if (omniVoiceProcess is null && processOptions is not null)
                {
                    omniVoiceProcess = Own(owned, new OmniVoiceProcessController(processOptions));
                }

                if (omniVoiceProcess is not null)
                {
                    var voiceHttpClient = dependencies.VoiceHttpClient
                        ?? Own(owned, new HttpClient());
                    voiceProvider = Own(owned, new OmniVoiceHttpProvider(
                        voiceHttpClient,
                        new OmniVoiceProviderOptions(voiceEndpoint),
                        omniVoiceProcess));
                }
            }

            var speechRecognizer = dependencies.SpeechRecognizer
                ?? Own(owned, new UnavailableSpeechRecognizer());
            var observationProvider = dependencies.ObservationProvider;
            if (observationProvider is null)
            {
                var observationHttpClient = dependencies.ObservationHttpClient
                    ?? Own(owned, new HttpClient());
                // The agent provider bootstrap is deliberately not reused here:
                // v1 has no credential channel. An empty key yields a bounded
                // missing_api_key skip without making a network request.
                observationProvider = Own(owned, new GeminiVisionProvider(
                    observationHttpClient,
                    new GeminiVisionOptions(string.Empty),
                    leases));
            }

            var vtsClient = dependencies.VtubeStudioClient;
            if (vtsClient is null)
            {
                var vtsEndpoint = options.VtubeStudioEndpoint
                    ?? new Uri("ws://127.0.0.1:8001", UriKind.Absolute);
                vtsClient = Own(owned, new VtubeStudioClient(
                    new VtubeStudioClientOptions(vtsEndpoint)
                    {
                        TokenPath = FileVtubeStudioTokenStore.ResolveDefaultPath(),
                    },
                    leases: leases));
            }

            var privateRouter = new PrivateRuntimeRequestRouter(
                new UnavailablePrivateRuntimeRequestHandler());
            var runtimeStatusHandler = new ManagedRuntimeStatusRequestHandler(settings);
            privateRouter = privateRouter
                .WithRouteHandler("GET", "/status", runtimeStatusHandler)
                .WithRouteHandler("GET", "/models", runtimeStatusHandler);
            var vtsRuntimeHandler = new VtubeStudioRuntimeRequestHandler(vtsClient);
            privateRouter = privateRouter
                .WithRouteHandler("GET", "/vts/mouse-look", vtsRuntimeHandler)
                .WithRouteHandler("POST", "/vts/mouse-look", vtsRuntimeHandler)
                .WithRouteHandler("GET", "/vts/overlay", vtsRuntimeHandler)
                .WithRouteHandler("POST", "/vts/overlay", vtsRuntimeHandler)
                .WithRouteHandler("GET", "/vts/status", vtsRuntimeHandler)
                .WithRouteHandler("POST", "/vts/connect", vtsRuntimeHandler)
                .WithRouteHandler("GET", "/vts/config", vtsRuntimeHandler)
                .WithRouteHandler("POST", "/vts/config", vtsRuntimeHandler)
                .WithRouteHandler("POST", "/vts/activity", vtsRuntimeHandler)
                .WithRouteHandler("POST", "/vts/action", vtsRuntimeHandler)
                .WithRouteHandler("POST", "/vts/speaking", vtsRuntimeHandler);
            var observationControlHandler = new ObservationControlRequestHandler(
                settings,
                voiceProvider is not null);
            privateRouter = privateRouter
                .WithRouteHandler("POST", "/observation/preflight", observationControlHandler)
                .WithRouteHandler("GET", "/observation/vision-config", observationControlHandler)
                .WithRouteHandler("POST", "/observation/react", observationControlHandler)
                .WithRouteHandler("POST", "/observation/cancel", observationControlHandler)
                .WithRouteHandler("POST", "/observation/ack", observationControlHandler)
                .WithRouteHandler("POST", "/observation/start", observationControlHandler)
                .WithRouteHandler("POST", "/observation/stop", observationControlHandler)
                .WithRouteHandler("GET", "/observation/status", observationControlHandler);
            if (voiceProvider is not null)
            {
                var voiceHandler = new VoiceRuntimeRequestHandler(voiceProvider, settings);
                privateRouter = privateRouter
                    .WithRouteHandler("GET", "/voice/status", voiceHandler)
                    .WithRouteHandler("POST", "/voice/latency", voiceHandler)
                    .WithRouteHandler("POST", "/voice/tts", voiceHandler)
                    .WithRouteHandler("POST", "/voice/warmup", voiceHandler)
                    .WithRouteHandler("POST", "/voice/release", voiceHandler);
            }
            else
            {
                var unavailableVoiceHandler = new UnavailableVoiceRuntimeRequestHandler();
                privateRouter = privateRouter
                    .WithRouteHandler("GET", "/voice/status", unavailableVoiceHandler)
                    .WithRouteHandler("POST", "/voice/latency", unavailableVoiceHandler)
                    .WithRouteHandler("POST", "/voice/tts", unavailableVoiceHandler)
                    .WithRouteHandler("POST", "/voice/warmup", unavailableVoiceHandler)
                    .WithRouteHandler("POST", "/voice/release", unavailableVoiceHandler);
            }

            var deviceRouter = new ManagedWorkerV1RequestRouter(
                settings,
                privateRouter,
                voiceProvider is null ? null : new VoiceStreamCoordinator(voiceProvider, leases),
                speechRecognizer,
                vtsClient,
                observationProvider);
            var deviceServer = new WorkerV1Server(options.DeviceWorkerToken, deviceRouter);

            var composition = new ManagedWorkerComposition(
                leases,
                settings,
                agentSteps,
                agentServer,
                deviceRouter,
                deviceServer,
                voiceProvider,
                speechRecognizer,
                vtsClient,
                observationProvider,
                omniVoiceProcess);
            composition._owned.AddRange(owned);
            return composition;
        }
        catch
        {
            DisposeOwned(owned);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        for (var index = _owned.Count - 1; index >= 0; index--)
        {
            await DisposeOneAsync(_owned[index]).ConfigureAwait(false);
        }

        _owned.Clear();
    }

    private static RuntimeSettingsSnapshot InitialSettings(ProviderBootstrapData bootstrap) => new(
        bootstrap.Backend,
        bootstrap.Model,
        bootstrap.Endpoint,
        "Default",
        false);

    private static Uri ResolveVoiceEndpoint(ManagedWorkerCompositionOptions options)
    {
        if (options.VoiceEndpoint is not null)
        {
            return options.VoiceEndpoint;
        }

        var host = Environment.GetEnvironmentVariable("RU_VOICE_HOST");
        if (string.IsNullOrWhiteSpace(host) || !string.Equals(host.Trim(), "127.0.0.1", StringComparison.Ordinal))
        {
            host = "127.0.0.1";
        }

        var port = 5_052;
        var rawPort = Environment.GetEnvironmentVariable("RU_VOICE_PORT");
        if (int.TryParse(rawPort, out var parsedPort) && parsedPort is >= 1_024 and <= 65_535)
        {
            port = parsedPort;
        }

        return new UriBuilder(Uri.UriSchemeHttp, host, port).Uri;
    }

    private static OmniVoiceProcessOptions? TryBuildOmniVoiceProcessOptions(
        string projectRoot,
        Uri voiceEndpoint)
    {
        var python = Path.Combine(projectRoot, "omnivoice_env", "Scripts", "python.exe");
        var script = Path.Combine(projectRoot, "src", "ru_runtime", "voice", "omnivoice_server.py");
        if (!File.Exists(python) || !File.Exists(script))
        {
            return null;
        }

        try
        {
            return new OmniVoiceProcessOptions(projectRoot, python, script)
            {
                Host = "127.0.0.1",
                Port = voiceEndpoint.Port,
            };
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static T Own<T>(ICollection<object> owned, T value)
    {
        owned.Add(value!);
        return value;
    }

    private static void DisposeOwned(IEnumerable<object> owned)
    {
        foreach (var value in owned.Reverse())
        {
            try
            {
                switch (value)
                {
                    case IAsyncDisposable asyncDisposable:
                        asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        break;
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
            catch
            {
                // Preserve the original composition failure and fail closed.
            }
        }
    }

    private static async ValueTask DisposeOneAsync(object value)
    {
        switch (value)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    private sealed class UnavailableSpeechRecognizer : ISpeechRecognizer
    {
        public Task<string> RecognizeAsync(
            SpeechRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new SpeechProviderException("speech_unavailable", retryable: true);
        }
    }
}
