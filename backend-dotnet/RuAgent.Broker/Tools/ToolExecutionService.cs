using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Configuration;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Skills;
using RuAgent.Broker.Tools.Files;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Broker.Tools.Terminal;
using RuAgent.Broker.Tools.Web;
using RuAgent.Broker.Tools.Help;
using RuAgent.Broker.Tools.LanguageServers;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Tools;

public sealed partial class ToolExecutionService : IDisposable, IAsyncDisposable
{
    // Dispatch coordination: V2 tool.execute is gated in BrokerExecutionRuntime before dispatch:
    //  - tool.evaluate (allow) → authorization_token (60s) → tool.execute
    //  - tool.evaluate (ask) → approval_id (30m) → approval.respond → authorization_token (60s) → tool.execute
    // This service's ExecuteAsync is the v1 terminal/file path (scope grant + optional approval). It never bypasses
    // the V2 authorization check; the runtime consumes the authorization_token BEFORE invoking the tool handler.
    // V2 approvals never invoke adapters via approval.respond; only tool.execute after token consumption invokes adapters.
    private static readonly Regex StableCodePattern = new("^[a-z][a-z0-9_]{0,63}$", RegexOptions.Compiled);
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly BrokerSession _session;
    private readonly ToolCatalog _catalog;
    private readonly ToolPolicyEngine _policy = new();
    private readonly ApprovalTokenService _approvalTokens;
    private readonly PendingApprovalStore _approvals = new();
    private readonly WorkspacePathGuard _workspace;
    private readonly FileListAdapter _fileList;
    private readonly FileGlobAdapter _fileGlob;
    private readonly FileSearchAdapter _fileSearch;
    private readonly FileReadAdapter _fileRead;
    private readonly FileWriteAdapter _fileWrite;
    private readonly FileDeleteAdapter _fileDelete;
    private readonly FileRestoreAdapter _fileRestore;
    // One store per service: recovery ids stay opaque even when several
    // runtimes live in one process.
    private readonly InMemoryRecoveryStore _recoveryStore = new();
    private readonly CanonicalPathResolver _mutationResolver;
    private readonly RootToolSet _defaultTools;
    private readonly ConcurrentDictionary<string, RootToolSet> _toolSetsByRoot = new(StringComparer.OrdinalIgnoreCase);
    private readonly TerminalAdapter _terminal;
    private readonly BackgroundTaskRegistry _backgroundTasks;
    private readonly HelpDocsTool _helpDocs;
    private readonly Func<bool> _pythonAvailable;
    private readonly Func<string, bool> _skillCapabilityAvailable;
    private readonly WebOptions _webOptions;
    private readonly WebSearchAdapter? _webSearch;
    private readonly WebFetchAdapter? _webFetch;
    private readonly HttpClient? _ownedWebHttpClient;
    private readonly ConcurrentDictionary<string, LanguageServerHost> _languageServers = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, string> _languageServerEnvironment;

    public ToolExecutionService(
        BrokerSession session,
        string workspaceRoot,
        WebOptions? webOptions = null,
        HttpClient? webHttpClient = null,
        ApprovalTokenService? approvalTokens = null,
        Func<bool>? pythonAvailable = null,
        string? terminalSpoolRoot = null,
        TerminalConcurrencyOptions? terminalConcurrencyOptions = null,
        Func<string, bool>? skillCapabilityAvailable = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _session = session;
        var terminalLimits = terminalConcurrencyOptions
            ?? TerminalConcurrencyOptions.FromEnvironment(Environment.GetEnvironmentVariable);
        _backgroundTasks = new BackgroundTaskRegistry(terminalLimits.MaxConcurrentTasks);
        _workspace = new WorkspacePathGuard(workspaceRoot);
        _languageServerEnvironment = new AgentCommandEnvironment().ChildEnvironment;
        _fileList = new FileListAdapter(_workspace.Root);
        _fileGlob = new FileGlobAdapter(_workspace.Root);
        _fileSearch = new FileSearchAdapter(_workspace.Root);
        _fileRead = new FileReadAdapter(_workspace.Root);
        _fileWrite = new FileWriteAdapter(_workspace.Root);
        _fileDelete = new FileDeleteAdapter(_workspace.Root, _recoveryStore);
        _fileRestore = new FileRestoreAdapter(_workspace.Root, _recoveryStore);
        _mutationResolver = new CanonicalPathResolver(_workspace.Root);
        _spools = new TerminalOutputSpool(terminalSpoolRoot);
        _helpDocs = HelpDocsTool.CreateDefault();
        _approvalTokens = approvalTokens ?? new ApprovalTokenService();
        _terminal = new TerminalAdapter(
            _workspace,
            _approvalTokens,
            intentResolver: new CommandIntentResolver(
                _workspace.Root));
        _defaultTools = new RootToolSet
        {
            List = _fileList,
            Glob = _fileGlob,
            Search = _fileSearch,
            Read = _fileRead,
            Write = _fileWrite,
            Delete = _fileDelete,
            Restore = _fileRestore,
            MutationResolver = _mutationResolver,
            Terminal = _terminal,
            CanonicalRoot = _workspace.Root,
            Edit = new FileEditAdapter(_workspace.Root),
        };
        _webOptions = webOptions ?? new WebOptions();
        if (_webOptions.Enabled)
        {
            var webClient = webHttpClient ?? CreateWebHttpClient();
            if (webHttpClient is null)
            {
                // An injected client stays owned by its caller; a client this
                // service created itself dies with the service.
                _ownedWebHttpClient = webClient;
            }

            var webPolicy = new WebPolicy();
            var sanitizer = new WebContentSanitizer();
            _webFetch = new WebFetchAdapter(webClient, webPolicy, sanitizer, _webOptions.MaxResponseBytes);
            if (_webOptions.SearchEndpoint is not null)
            {
                _webSearch = new WebSearchAdapter(webClient, webPolicy, sanitizer, _webOptions.SearchEndpoint, _webOptions.MaxResponseBytes, WebOptions.DefaultFallbackEndpoints, _webOptions.SearchMode);
            }
        }

        _catalog = new ToolCatalog(BuildExecutors(), _webOptions);
        _pythonAvailable = pythonAvailable ?? (() => PythonAvailability.IsAvailable);
        _skillCapabilityAvailable = skillCapabilityAvailable ?? (capability =>
            string.Equals(capability, "python", StringComparison.OrdinalIgnoreCase) ? _pythonAvailable() : SkillCapabilityAvailability.IsAvailable(capability));
    }

    internal int CachedRootToolSetCount => _toolSetsByRoot.Count;

    internal HttpClient? OwnedWebHttpClientForTest => _ownedWebHttpClient;

    /// <summary>Cancels owned terminal tasks and disposes the owned web client.</summary>
    public void Dispose()
    {
        lock (_terminalLifetimeGate) _stopping = true;
        _backgroundTasks.CancelAll();
        _ownedWebHttpClient?.Dispose();
        foreach (var host in _languageServers.Values) host.Dispose();
        _languageServers.Clear();
    }

    private void RemoveSessionState(string sessionId)
    {
        _backgroundTasks.RemoveSession(sessionId);
        _approvals.RemoveSession(sessionId);
        // A deleted session must not leave its plan behind: the store is keyed by
        // session and nothing else would ever clear it.
        _todos.Remove(sessionId);
        // Retained terminal output dies with the session for the same reason: a
        // spool id must never resolve to another session's text, and a spilled
        // spool file must never outlive it.
        _spools.RemoveSession(sessionId);
        // App grants for computer use end with the Agent session (ADR 0031).
        _computer?.RemoveSession(sessionId);
    }

    /// <summary>
    /// Drops spilled spool files from a previous process in this service's
    /// configured spool root. Spool ids are per-process, so a restart makes
    /// every older file unreachable; a second runtime's root must stay intact.
    /// </summary>
    public void CleanStaleSpoolFiles() => _spools.CleanStaleRoot();

    public void ClearPendingApprovals() => _approvals.Clear();

    public IReadOnlyList<AgentToolCapability> ListCapabilities() =>
        _catalog.Registry.ListCapabilities();

    private IReadOnlyDictionary<string, ToolExecutor> BuildExecutors() =>
        BuildExecutorsCore();

    private IReadOnlyDictionary<string, ToolExecutor> BuildExecutorsCore()
    {
        var executors = new Dictionary<string, ToolExecutor>(StringComparer.Ordinal)
        {
            ["run_code"] = (_, _, _) => ValueTask.FromResult(ToolExecutionOutcome.Failed("program_runtime_required", "Programs require an authenticated run and durable journal.")),
            ["read"] = ExecuteReadAsync,
            ["list"] = ExecuteListAsync,
            ["help"] = ExecuteHelpAsync,
            ["todo_write"] = ExecuteTodoWriteAsync,
            ["skill"] = ExecuteSkillBodyAsync,
            ["grep"] = async (context, arguments, token) => ToolExecutionOutcome.Succeeded(
                await ResolveTools(context, TargetPathOf(arguments)).Search.ExecuteAsync(
                    TranslateGrepArguments(arguments),
                    token).ConfigureAwait(false),
                ToolVerificationStates.NotRequired,
                "Workspace search completed."),
            ["glob"] = async (context, arguments, token) => ToolExecutionOutcome.Succeeded(
                await Task.Run(
                    () => ResolveTools(context, TargetPathOf(arguments)).Glob.Execute(
                        TranslateGlobArguments(arguments), token, Math.Clamp(context.DeadlineMs, 1, 300_000)),
                    token).ConfigureAwait(false),
                ToolVerificationStates.NotRequired,
                "Workspace glob completed."),
            ["apply_patch"] = ExecutePatchAsync,
            ["read_file"] = ExecuteLineReadAsync,
            ["write_file"] = (context, arguments, _) => Succeeded(ExecuteFileWrite(context, arguments), "Workspace file written."),
            ["edit_file"] = ExecuteMultiEditAsync,
            ["file_delete"] = (context, arguments, _) => Succeeded(ExecuteDelete(context, arguments), "Workspace file deleted."),
            ["file_restore"] = (context, arguments, _) => ExecuteRestore(context, arguments),
            ["powershell"] = ExecuteTerminalShellAsync,
            ["run_process"] = ExecuteTerminalRunAsync,
            ["terminal_read"] = (context, arguments, _) => ValueTask.FromResult(ExecuteTerminalReadOutput(context, arguments)),
            ["terminal_stop"] = (context, arguments, _) => ValueTask.FromResult(ExecuteTerminalKill(context, arguments)),
            ["web_search"] = ExecuteWebSearchAsync,
            ["web_fetch"] = ExecuteWebFetchAsync,
            ["mcp"] = ExecuteMcpAsync,
            ["code_intelligence"] = ExecuteCodeIntelligenceAsync,
            ["computer"] = ExecuteComputerAsync,
        };

        // Native orchestration consumes exact authorization through Runtime callbacks.
        // Ordinary/legacy dispatch must never substitute an execution adapter for it.
        foreach (var tool in RuAgent.Protocol.Subagents.SubagentToolDefinitions.Get())
            executors[tool.ToolId] = (_, _, _) => ValueTask.FromResult(ToolExecutionOutcome.Failed(
                "subagent_native_control_required", "Use the run-bound orchestration gateway."));
        return executors;
    }

    private static ToolResult Receipt(
        AgentEnvelope request,
        ToolExecutionOutcome outcome,
        bool adapterInvoked) => new(
        request.RequestId,
        request.SessionId,
        outcome.Status,
        outcome.Output,
        outcome.ErrorCode,
        outcome.SafeSummary,
        outcome.DurationMs,
        outcome.VerificationState,
        outcome.EndpointRole,
        outcome.EndpointHash,
        adapterInvoked,
        // Only a success carries an image; a failure never returns file bytes.
        outcome.Status == ToolResultStatuses.Succeeded ? outcome.Image : null);

    private static ToolResult Result(
        AgentEnvelope request,
        string status,
        JsonObject output,
        string? errorCode,
        string summary,
        Stopwatch stopwatch,
        string verificationState = ToolVerificationStates.NotRequired,
        string? endpointRole = null,
        string? endpointHash = null,
        bool adapterInvoked = false) => new(
        request.RequestId,
        request.SessionId,
        status,
        output,
        errorCode,
        summary,
        checked((int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds)),
        verificationState,
        endpointRole,
        endpointHash,
        adapterInvoked);

    private static ValueTask<ToolExecutionOutcome> Succeeded(JsonObject output, string summary) =>
        ValueTask.FromResult(ToolExecutionOutcome.Succeeded(
            output,
            ToolVerificationStates.NotRequired,
            summary));

    private static string VerificationFailure(ToolBinding binding) =>
        binding.VerificationRequired
            ? ToolVerificationStates.Failed
            : ToolVerificationStates.NotRequired;

    /// <summary>
    /// What the model can do about a failed file call, in the same shape as
    /// the edit-match hint above: the stable code stays, the summary tells it
    /// how to repair the call, and no file content travels.
    /// </summary>
    private static string IoRepairHint(string? code) => code switch
    {
        "is_directory" => "target is a directory. Use list for its entries and choose an existing file path for read_file/edit_file; do not remove it unless the user requested deletion.",
        "not_a_directory" => "path is not an existing directory. list and glob require a directory; grep accepts either a file or directory. To read a file, use read_file or read. For a directory, correct the path or list its existing parent; do not retry the same path.",
        "file_not_found" => "file does not exist: list its parent directory to check the path. Create it with an advertised write_file or apply_patch add-file diff. An edit cannot create files.",
        "read_line_too_long" => "A line exceeds the line-read output limit. Use read with byte paging and its returned cursor instead.",
        "read_line_scan_limit" => "The file exceeds the 4 MiB line snapshot limit. Use read with bounded byte paging instead.",
        "read_hash_timeout" => "The bounded file read timed out. Do not repeat unchanged; inspect the path and retry once only if the file is now accessible.",
        "file_exists" => "file already exists: read it, then use the advertised file tool for a partial change, or retry write_file with the same path and content and overwrite=true for deliberate replacement.",
        "io_error" => "The filesystem operation failed. Do not repeat an unchanged mutation. Inspect the current target and parent directory with read/list before deciding whether a retry is safe; if it persists, report the failure.",
        _ => "Tool input or output failed.",
    };

    private static string StableCode(string? candidate, string fallback) =>
        candidate is not null && StableCodePattern.IsMatch(candidate)
            ? candidate
            : fallback;

    private static string ArgumentRepairHint(ToolBinding binding, JsonObject arguments) =>
        ToolArgumentDiagnostics.Find(binding.Manifest.Id, binding.Manifest.Version,
            System.Text.Json.JsonSerializer.SerializeToElement(arguments))?.Hint ?? "Tool arguments were rejected.";

    /// <summary>
    /// Bounded, redacted adapter-failure diagnostic for the broker stderr log.
    /// Only the request id, tool id, exception type, and a stable-code-shaped
    /// message travel; payloads, arguments, and free-form messages never do.
    /// </summary>
    private static void LogAdapterFailure(AgentEnvelope request, Exception error)
    {
        try
        {
            var toolId = request.Payload["tool_id"] is JsonValue value
                && value.TryGetValue<string>(out var id)
                    ? id
                    : "-";
            var code = StableCodePattern.IsMatch(error.Message) ? error.Message : "-";
            Console.Error.WriteLine(
                $"ru.broker tool_adapter_failed request_id={BoundDiagnostic(request.RequestId)} tool_id={BoundDiagnostic(toolId)} exception={error.GetType().Name} code={code}");
        }
        catch
        {
            // A diagnostic must never change the outcome of a tool call.
        }
    }

    private static string BoundDiagnostic(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "-";
        }

        var length = Math.Min(value.Length, 128);
        var builder = new StringBuilder(length);
        for (var index = 0; index < length; index++)
        {
            var character = value[index];
            builder.Append(char.IsControl(character) ? '_' : character);
        }

        return builder.ToString();
    }

    private sealed class ToolExecutionException : Exception
    {
        public ToolExecutionException(string code) : base(code) => Code = code;
        public string Code { get; }
    }
}
