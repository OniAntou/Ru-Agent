using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Configuration;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Skills;
using RuAgent.Broker.Tools.Desktop;
using RuAgent.Broker.Tools.Files;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Broker.Tools.Terminal;
using RuAgent.Broker.Tools.Web;
using RuAgent.Broker.Tools.Help;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Tools;

public sealed partial class ToolExecutionService : IToolInvoker
{
    // Dispatch coordination: V2 tool.execute (files.* via policy snapshot) is gated in AgentConnection before dispatch:
    //  - tool.evaluate (allow) → decision_token → tool.prepare → preparation_token → tool.execute
    //  - tool.evaluate (ask) → approval_id (30m) → approval.respond → authorization_token (60s) → tool.execute
    // This service's ExecuteAsync is the v1 terminal/file path (scope grant + optional approval). It never bypasses
    // the V2 authorization check; AgentConnection consumes the authorization_token BEFORE invoking the tool handler.
    // V2 approvals never invoke adapters via approval.respond; only tool.execute after token consumption invokes adapters.
    private static readonly Regex StableCodePattern = new("^[a-z][a-z0-9_]{0,63}$", RegexOptions.Compiled);
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly BrokerSession _session;
    private readonly ToolCatalog _catalog;
    private readonly ToolPolicyEngine _policy = new();
    private readonly ApprovalTokenService _approvalTokens;
    private readonly PendingApprovalStore _approvals = new();
    private readonly WorkspacePathGuard _workspace;
    private readonly FileTools _files;
    private readonly FileListAdapter _fileList;
    private readonly FileGlobAdapter _fileGlob;
    private readonly FileSearchAdapter _fileSearch;
    private readonly FileReadAdapter _fileRead;
    private readonly FileWriteAdapter _fileWrite;
    private readonly FileDeleteAdapter _fileDelete;
    private readonly FileRestoreAdapter _fileRestore;
    private readonly CanonicalPathResolver _mutationResolver;
    private readonly RootToolSet _defaultTools;
    private readonly ConcurrentDictionary<string, RootToolSet> _toolSetsByRoot = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FileTools> _fileToolsByRoot = new(StringComparer.OrdinalIgnoreCase);
    private readonly TerminalAdapter _terminal;
    private readonly HelpDocsTool _helpDocs;
    private readonly SkillRegistry _skills;
    private readonly SkillOrchestrator _skillOrchestrator;
    private readonly WebOptions _webOptions;
    private readonly WebSearchAdapter? _webSearch;
    private readonly WebFetchAdapter? _webFetch;

    public ToolExecutionService(
        BrokerSession session,
        string workspaceRoot,
        IEnumerable<SkillManifest>? builtinSkills = null,
        IEnumerable<string>? skillPackageRoots = null,
        WebOptions? webOptions = null,
        HttpClient? webHttpClient = null,
        ApprovalTokenService? approvalTokens = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _workspace = new WorkspacePathGuard(workspaceRoot);
        _files = new FileTools(_workspace);
        _fileList = new FileListAdapter(_workspace.Root);
        _fileGlob = new FileGlobAdapter(_workspace.Root);
        _fileSearch = new FileSearchAdapter(_workspace.Root);
        _fileRead = new FileReadAdapter(_workspace.Root);
        _fileWrite = new FileWriteAdapter(_workspace.Root);
        _fileDelete = new FileDeleteAdapter(_workspace.Root);
        _fileRestore = new FileRestoreAdapter(_workspace.Root);
        _mutationResolver = new CanonicalPathResolver(_workspace.Root);
        _helpDocs = HelpDocsTool.CreateDefault();
        _approvalTokens = approvalTokens ?? new ApprovalTokenService();
        _terminal = new TerminalAdapter(
            _workspace,
            new ShellRuntimeResolver(),
            _approvalTokens);
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
            var webPolicy = new WebPolicy();
            var sanitizer = new WebContentSanitizer();
            _webFetch = new WebFetchAdapter(webClient, webPolicy, sanitizer, _webOptions.MaxResponseBytes);
            if (_webOptions.SearchEndpoint is not null)
            {
                _webSearch = new WebSearchAdapter(webClient, webPolicy, sanitizer, _webOptions.SearchEndpoint, _webOptions.MaxResponseBytes);
            }
        }

        _catalog = new ToolCatalog(BuildExecutors(), _webOptions);
        _skills = new SkillRegistry();
        foreach (var skill in builtinSkills ?? Array.Empty<SkillManifest>())
        {
            _skills.Register(skill, _catalog.Registry.Manifests);
        }

        if (skillPackageRoots is not null)
        {
            var loader = new SkillPackageLoader();
            foreach (var packageRoot in skillPackageRoots)
            {
                if (string.IsNullOrWhiteSpace(packageRoot))
                {
                    continue;
                }

                var root = Path.GetFullPath(packageRoot);
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (var directory in Directory.EnumerateDirectories(root))
                {
                    _skills.Register(loader.Load(directory), _catalog.Registry.Manifests);
                }
            }
        }

        _skillOrchestrator = new SkillOrchestrator(_catalog.Registry.Manifests, _policy, this);
    }

    public void RemoveSession(string sessionId) => _approvals.RemoveSession(sessionId);

    public void ClearPendingApprovals() => _approvals.Clear();

    private IReadOnlyDictionary<string, ToolExecutor> BuildExecutors() =>
        BuildExecutorsCore();

    private IReadOnlyDictionary<string, ToolExecutor> BuildExecutorsCore()
    {
        var executors = new Dictionary<string, ToolExecutor>(StringComparer.Ordinal)
        {
            ["help.docs"] = (_, arguments, cancellationToken) => Succeeded(
                _helpDocs.Execute(arguments, cancellationToken),
                "Bundled Ru documentation searched."),
            ["files.list"] = (context, arguments, _) => Succeeded(
                ResolveTools(context, TargetPathOf(arguments)).List.Execute(arguments),
                "Workspace entries listed."),
            ["files.glob"] = (context, arguments, _) => Succeeded(
                ResolveTools(context, TargetPathOf(arguments)).Glob.Execute(arguments),
                "Workspace glob completed."),
            ["files.search"] = (context, arguments, _) => Succeeded(
                ResolveTools(context, TargetPathOf(arguments)).Search.Execute(arguments),
                "Workspace search completed."),
            ["files.read"] = (context, arguments, _) => Succeeded(
                ResolveTools(context, TargetPathOf(arguments)).Read.Execute(arguments),
                "Workspace file read."),
            ["files.edit"] = (context, arguments, _) => ExecuteFileEdit(context, arguments),
            ["files.write"] = (context, arguments, _) => Succeeded(
                ExecuteFileWrite(context, arguments),
                "Workspace file written."),
            ["files.delete"] = (context, arguments, _) => Succeeded(
                ExecuteDelete(context, arguments),
                "Workspace file deleted."),
            ["files.restore"] = (context, arguments, _) => ExecuteRestore(context, arguments),
            ["terminal.run"] = ExecuteTerminalRunAsync,
            ["terminal.shell"] = ExecuteTerminalShellAsync,
        };

        if (_webFetch is not null)
        {
            executors["web.fetch"] = ExecuteWebFetchAsync;
        }

        if (_webSearch is not null)
        {
            executors["web.search"] = ExecuteWebSearchAsync;
        }

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
        adapterInvoked);

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

    private static string StableCode(string? candidate, string fallback) =>
        candidate is not null && StableCodePattern.IsMatch(candidate)
            ? candidate
            : fallback;

    private sealed class ToolExecutionException : Exception
    {
        public ToolExecutionException(string code) : base(code) => Code = code;
        public string Code { get; }
    }
}
