using Microsoft.Extensions.Logging;
using RuAgent.Broker.Configuration;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools;
using RuBackend.Business;

namespace RuBackend.Hosting;

/// <summary>
/// The Host's in-process execution authority: one descriptor-free Broker
/// session, one tool execution service and one execution runtime per Host
/// process. The runtime is composed lazily — a Host that never executes a tool
/// never creates the tool service or cleans the spool root — while everything
/// that can fail from configuration (the terminal capacity, session root, web
/// options and workspace root) is resolved when the authority is constructed,
/// so a managed Host fails startup with a bounded reason instead of failing
/// every tool call.
/// </summary>
internal sealed class InProcessExecutionAuthority : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// The session root the Broker child used: <c>&lt;log root&gt;/agent/session</c>.
    /// The in-process session is descriptor-free, so nothing is written there;
    /// the root is kept for parity with the child topology and as a startup
    /// writability check of the resolved log root.
    /// </summary>
    internal const string SessionRootRelativePath = "agent/session";

    private readonly Lazy<BrokerExecutionRuntime> _runtime;
    private ToolExecutionService? _tools;
    private BrokerExecutionRuntime? _composedRuntime;
    private int _disposed;
    private readonly RuAgent.Broker.Tools.Mcp.McpServerHost? _mcpServers;
    private readonly object _lifetimeGate = new();

    internal InProcessExecutionAuthority(
        DataPaths paths,
        ILogger logger,
        string? spoolRoot = null,
        Func<string, string?>? readEnvironment = null,
        RuAgent.Broker.Tools.Mcp.McpServerHost? mcpServers = null)
    {
        _mcpServers = mcpServers;
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        TerminalConcurrency = TerminalConcurrencyOptions.FromEnvironment(
            readEnvironment ?? Environment.GetEnvironmentVariable);
        SessionRoot = ResolveSessionRoot(paths.LogRoot);
        WorkspaceRoot = ResolveWorkspaceRoot(
            Environment.GetEnvironmentVariable("RU_WORKSPACE_ROOT"),
            Environment.GetEnvironmentVariable("RU_PROJECT_ROOT"),
            Environment.CurrentDirectory);
        WebOptions = BuildWebOptions(Environment.GetEnvironmentVariable, logger);
        SpoolRoot = string.IsNullOrWhiteSpace(spoolRoot) ? DefaultSpoolRoot() : Path.GetFullPath(spoolRoot);
        _runtime = new Lazy<BrokerExecutionRuntime>(
            ComposeRuntime,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal string SessionRoot { get; }
    internal string WorkspaceRoot { get; }
    internal WebOptions? WebOptions { get; }
    internal TerminalConcurrencyOptions TerminalConcurrency { get; }
    internal string SpoolRoot { get; }

    /// <summary>
    /// The one runtime, still uncomposed. The gateway factory takes this lazy so
    /// constructing the factory (which happens at host startup) never composes
    /// the runtime; the first tool call does.
    /// </summary>
    internal Lazy<BrokerExecutionRuntime> Runtime => _runtime;

    /// <summary>
    /// The spool root the Broker child used by default:
    /// <c>%LOCALAPPDATA%\Ru\spool</c>.
    /// </summary>
    internal static string DefaultSpoolRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ru",
        "spool");

    /// <summary>
    /// Builds the session, tool service and runtime. The v1 service and the v2
    /// runtime share one approval-token service, exactly like the child host:
    /// tokens issued through either path must be consumable exactly once.
    /// </summary>
    private BrokerExecutionRuntime ComposeRuntime()
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var approvalTokens = new ApprovalTokenService();
            var session = BrokerSession.CreateForHost(
                SessionRoot,
                // The wire session expired after one hour because the Broker child
                // exited at expiry and the managed host stopped with it. In-process
                // the authority is bounded by the Host process, not a timestamp: the
                // session expiry gates v1/v2 policy grants, so a finite window would
                // deny every tool call once it elapsed.
                DateTimeOffset.MaxValue);
            var tools = new ToolExecutionService(
                session,
                WorkspaceRoot,
                WebOptions,
                approvalTokens: approvalTokens,
                terminalSpoolRoot: SpoolRoot,
                terminalConcurrencyOptions: TerminalConcurrency);
            // The child cleaned its spool root once at startup; the in-process
            // authority does the same on first composition, scoped to its root.
            tools.CleanStaleSpoolFiles();
            if (_mcpServers is not null) tools.UseMcpServers(_mcpServers);
            _tools = tools;
            _composedRuntime = new BrokerExecutionRuntime(
                session,
                tools,
                approvalTokenService: approvalTokens,
                advertisedCapabilities: session.AdvertisedCapabilities);
            return _composedRuntime;
        }
    }

    public void Dispose()
    {
        ToolExecutionService? tools;
        lock (_lifetimeGate)
        {
            if (_disposed != 0) return;
            _disposed = 1;
            tools = _tools;
        }

        _composedRuntime?.Dispose();
        tools?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        ToolExecutionService? tools;
        lock (_lifetimeGate)
        {
            if (_disposed != 0) return;
            _disposed = 1;
            _composedRuntime?.Dispose();
            tools = _tools;
        }
        if (tools is not null) await tools.DisposeAsync().ConfigureAwait(false);
        if (_mcpServers is not null) await _mcpServers.DisposeAsync().ConfigureAwait(false);
    }

    private static string ResolveSessionRoot(string logRoot)
    {
        var sessionRoot = Path.GetFullPath(Path.Combine(logRoot, SessionRootRelativePath));
        Directory.CreateDirectory(sessionRoot);
        return sessionRoot;
    }

    /// <summary>
    /// The workspace-root fallback the Broker child used, kept identical:
    /// <c>RU_WORKSPACE_ROOT</c> → <c>RU_PROJECT_ROOT</c> → walk up from the
    /// current directory for a repository checkout → current directory.
    /// </summary>
    internal static string ResolveWorkspaceRoot(
        string? workspaceRootEnvironment,
        string? projectRootEnvironment,
        string currentDirectory)
    {
        var configured = !string.IsNullOrWhiteSpace(workspaceRootEnvironment)
            ? workspaceRootEnvironment
            : projectRootEnvironment;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var full = Path.GetFullPath(configured);
            if (Directory.Exists(full))
            {
                return full;
            }
        }

        var current = new DirectoryInfo(currentDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "CONTEXT.md"))
                && Directory.Exists(Path.Combine(current.FullName, "backend-dotnet")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return currentDirectory;
    }

    /// <summary>
    /// Parses the web-tool environment exactly like the Broker child's
    /// <c>BuildWebOptions</c>: unset or non-<c>true</c> enabled disables web, an
    /// invalid endpoint or response bound disables web, and an endpoint the
    /// <see cref="WebOptions"/> validation refuses disables web. Fail closed: a
    /// bad value never widens what the authority may fetch.
    /// </summary>
    internal static WebOptions? BuildWebOptions(Func<string, string?> readEnvironment, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(readEnvironment);
        ArgumentNullException.ThrowIfNull(logger);
        var enabledRaw = readEnvironment("RU_AGENT_BROKER_WEB_ENABLED");
        if (!string.Equals(enabledRaw, "true", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        Uri? searchEndpoint = null;
        var endpointRaw = readEnvironment("RU_AGENT_BROKER_WEB_SEARCH_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(endpointRaw))
        {
            if (!Uri.TryCreate(endpointRaw, UriKind.Absolute, out searchEndpoint))
            {
                logger.LogWarning("in-process web disabled: search endpoint invalid");
                return null;
            }
        }

        var maxResponseBytes = WebOptions.DefaultMaxResponseBytes;
        var maxRaw = readEnvironment("RU_AGENT_BROKER_WEB_MAX_RESPONSE_BYTES");
        if (!string.IsNullOrWhiteSpace(maxRaw))
        {
            if (!long.TryParse(maxRaw, out var parsed)
                || parsed is < 1 or > WebOptions.MaxConfiguredResponseBytes)
            {
                logger.LogWarning("in-process web disabled: max response bytes invalid");
                return null;
            }

            maxResponseBytes = (int)parsed;
        }

        try
        {
            return new WebOptions(
                enabled: true,
                searchEndpoint: searchEndpoint,
                maxResponseBytes: maxResponseBytes);
        }
        catch (ArgumentException error)
        {
            logger.LogWarning("in-process web disabled: {Reason}", error.Message);
            return null;
        }
    }
}
