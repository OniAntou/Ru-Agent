using System.Net;
using System.Net.Http;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;

namespace RuAgent.Runtime.Worker;

/// <summary>
/// Explicit object graph for the managed Worker: one agent v2 model-step server
/// that receives the one-shot provider bootstrap.
/// </summary>
public sealed record ManagedWorkerCompositionOptions
{
    public ManagedWorkerCompositionOptions(
        int agentPort,
        string agentWorkerToken,
        ProviderBootstrapData agentProviderBootstrap,
        string projectRoot,
        string? providerBootstrapRoot = null)
    {
        ValidatePort(agentPort, nameof(agentPort));
        AgentWorkerToken = ValidateToken(agentWorkerToken, nameof(agentWorkerToken));
        AgentProviderBootstrap = agentProviderBootstrap ?? throw new ArgumentNullException(nameof(agentProviderBootstrap));
        ProjectRoot = ResolveDirectory(projectRoot, nameof(projectRoot));
        ProviderBootstrapRoot = providerBootstrapRoot is null
            ? null
            : ResolveDirectory(providerBootstrapRoot, nameof(providerBootstrapRoot));
    }

    public int AgentPort { get; }

    public string AgentWorkerToken { get; }

    public ProviderBootstrapData AgentProviderBootstrap { get; }

    public string ProjectRoot { get; }

    public string? ProviderBootstrapRoot { get; }

    public string? LogRoot { get; init; }

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

    public IModelProviderResolver? ModelProviderResolver { get; init; }
}

public sealed class ManagedWorkerComposition : IAsyncDisposable
{
    private readonly List<object> _owned = [];
    private int _disposed;

    private ManagedWorkerComposition(
        WorkerV2StepService agentSteps,
        WorkerV2Server agentServer)
    {
        AgentSteps = agentSteps;
        AgentServer = agentServer;
    }

    public WorkerV2StepService AgentSteps { get; }

    public WorkerV2Server AgentServer { get; }

    public static ManagedWorkerComposition Create(
        ManagedWorkerCompositionOptions options,
        ManagedWorkerCompositionDependencies? dependencies = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        dependencies ??= new ManagedWorkerCompositionDependencies();

        var owned = new List<object>();
        try
        {
            IModelProviderResolver providerResolver;
            if (dependencies.ModelProviderResolver is not null)
            {
                providerResolver = dependencies.ModelProviderResolver;
            }
            else if (dependencies.ModelProvider is not null)
            {
                var single = dependencies.ModelProvider;
                providerResolver = new ModelProviderResolver(new Dictionary<string, IModelProvider>
                {
                    ["chat_completions"] = single,
                    ["openai_responses"] = single,
                    ["anthropic_messages"] = single,
                });
            }
            else
            {
                // HttpClient.Timeout would cancel the response stream itself once
                // the whole request/response exceeds it, cutting a healthy long SSE
                // generation at 100 seconds. The step token governs the request:
                // the Host cancels a silent step, and the caller cancels the run.
                var modelHttpClient = Own(owned, new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
                providerResolver = new ModelProviderResolver(new Dictionary<string, IModelProvider>
                {
                    ["chat_completions"] = new OpenAiCompatibleModelProvider(modelHttpClient),
                    ["openai_responses"] = new OpenAiResponsesModelProvider(modelHttpClient),
                    ["anthropic_messages"] = new AnthropicMessagesModelProvider(modelHttpClient),
                });
            }

            var agentOptions = new WorkerV2Options(
                options.AgentPort,
                options.AgentWorkerToken,
                options.AgentProviderBootstrap.Endpoint,
                options.AgentProviderBootstrap.Model,
                options.AgentProviderBootstrap.Credential,
                options.ProviderBootstrapRoot);
            var agentSteps = new WorkerV2StepService(providerResolver, agentOptions);
            var agentServer = new WorkerV2Server(options.AgentWorkerToken, agentSteps);

            var composition = new ManagedWorkerComposition(agentSteps, agentServer);
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
}
