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
using RuAgent.Broker.Tools.Notes;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Broker.Tools.Terminal;
using RuAgent.Broker.Tools.Web;
using RuAgent.Broker.Tools.Help;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Tools;

public sealed class ToolExecutionService : IToolInvoker
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
    private readonly ApprovalTokenService _approvalTokens = new();
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
    private readonly AppTools _apps = new();
    private readonly SystemTools _system = new();
    private readonly Lazy<VolumeTools> _volume;
    private readonly TerminalAdapter _terminal;
    private readonly NotesAdapter _notes;
    private readonly HelpDocsTool _helpDocs;
    private readonly SkillRegistry _skills;
    private readonly SkillOrchestrator _skillOrchestrator;
    private readonly WebOptions _webOptions;
    private readonly WebSearchAdapter? _webSearch;
    private readonly WebFetchAdapter? _webFetch;

    public ToolExecutionService(
        BrokerSession session,
        string workspaceRoot,
        IVolumeController? volumeController = null,
        IEnumerable<SkillManifest>? builtinSkills = null,
        IEnumerable<string>? skillPackageRoots = null,
        WebOptions? webOptions = null,
        HttpClient? webHttpClient = null)
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
        _notes = new NotesAdapter(_workspace);
        _helpDocs = HelpDocsTool.CreateDefault();
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
            Notes = _notes,
            Edit = new FileEditAdapter(_workspace.Root),
        };
        _volume = new Lazy<VolumeTools>(
            () => new VolumeTools(volumeController ?? new WindowsCoreAudioVolumeController()));
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

    public IReadOnlyList<ToolManifest> ListTools() =>
        _catalog.Registry.List().Select(binding => binding.Manifest).ToArray();

    public IReadOnlyList<AgentToolCapability> ListCapabilities() =>
        _catalog.Registry.ListCapabilities();

    public IReadOnlyList<AgentToolCapability> ListCapabilities(CapabilityProfile profile) =>
        _catalog.Registry.ListCapabilities(profile, EnabledWebScopes());

    public IReadOnlyList<SkillManifest> ListSkills() => _skills.List();

    public IReadOnlyList<SkillManifest> ListSkills(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !_session.Owns(sessionId))
        {
            return Array.Empty<SkillManifest>();
        }

        return _skills.ListGranted(CreateSkillGrant(sessionId));
    }

    public SkillManifest? ResolveSkill(string id, string version)
    {
        try
        {
            return _skills.Resolve(id, version);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    public IReadOnlyList<SkillManifest> ListAllSkills() => _skills.List();

    public async Task<SkillRun> ExecuteSkillAsync(
        string sessionId,
        string skillId,
        string version,
        JsonObject input,
        CancellationToken cancellationToken)
    {
        var runId = $"skill-{Guid.NewGuid():N}";
        if (string.IsNullOrWhiteSpace(sessionId) || !_session.Owns(sessionId))
        {
            return new SkillRun(runId, sessionId, skillId, SkillRunState.Failed, 0, "Skill is not granted.", "skill_not_granted", null, null, runId);
        }

        SkillManifest skill;
        try
        {
            skill = _skills.Resolve(skillId, version);
        }
        catch (KeyNotFoundException)
        {
            return new SkillRun(runId, sessionId, skillId, SkillRunState.Failed, 0, "Skill is not available.", "skill_not_granted", null, null, runId);
        }

        var grant = CreateSkillGrant(sessionId);
        var decision = _policy.EvaluateSkill(grant, skill.Id, skill.RequiredScopes);
        if (!decision.Allowed)
        {
            return new SkillRun(runId, sessionId, skill.Id, SkillRunState.Failed, 0, "Skill is not granted.", decision.Code, null, null, runId);
        }

        return await _skillOrchestrator.RunAsync(
            sessionId,
            skill,
            grant,
            input,
            cancellationToken).ConfigureAwait(false);
    }

    private PolicyGrant CreateSkillGrant(string sessionId) =>
        new(
            sessionId,
            _session.Scopes,
            _session.Descriptor.ExpiresAt,
            _session.SkillIds);

    private IReadOnlySet<string> EnabledWebScopes()
    {
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        if (_webOptions.FetchEnabled)
        {
            scopes.Add(ToolScope.WebFetch);
        }

        if (_webOptions.SearchEnabled)
        {
            scopes.Add(ToolScope.WebSearch);
        }

        return scopes;
    }

    public void RemoveSession(string sessionId) => _approvals.RemoveSession(sessionId);

    public void ClearPendingApprovals() => _approvals.Clear();

    public Task<ToolResult> ExecuteAsync(
        string sessionId,
        ToolManifest manifest,
        JsonObject arguments,
        int deadlineMs,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(arguments);
        return ExecuteAsync(
            new AgentEnvelope(
                AgentEnvelopeCodec.ProtocolVersion,
                $"skill-tool-{Guid.NewGuid():N}",
                AgentMessageTypes.ToolRequest,
                sessionId,
                null,
                deadlineMs,
                new JsonObject
                {
                    ["tool_id"] = manifest.Id,
                    ["tool_version"] = manifest.Version,
                    ["arguments"] = arguments.DeepClone(),
                    ["capability_class"] = "internal",
                }),
            cancellationToken);
    }

    public async Task<ToolResult> ExecuteAsync(
        AgentEnvelope request,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        try
        {
            var toolId = ReadString(request.Payload, "tool_id");
            var toolVersion = ReadString(request.Payload, "tool_version");
            var arguments = request.Payload["arguments"] is JsonObject objectNode
                ? objectNode
                : throw new ToolExecutionException("invalid_arguments");
            var binding = _catalog.Registry.Get(toolId, toolVersion);
            var decision = _policy.Evaluate(
                new PolicyGrant(
                    request.SessionId,
                    _session.Scopes,
                    _session.Descriptor.ExpiresAt),
                binding.Manifest);
            if (!decision.Allowed)
            {
                return Result(
                    request,
                    ToolResultStatuses.Denied,
                    new JsonObject(),
                    decision.Code,
                    "Tool scope is not granted.",
                    started,
                    ToolVerificationStates.NotRequired,
                    adapterInvoked: false);
            }

            try
            {
                ToolSchemaValidator.Validate(binding.Manifest.InputSchema, arguments);
            }
            catch (ToolSchemaException error)
            {
                return Result(
                    request,
                    ToolResultStatuses.Failed,
                    new JsonObject(),
                    error.Code,
                    "Tool arguments were rejected.",
                    started,
                    ToolVerificationStates.NotRequired,
                    adapterInvoked: false);
            }

            // V2 executes arrive with a consumed preparation or authorization token
            // (AgentConnection consumes it before dispatch), so the v1 approval
            // prompt must not re-gate them; approval already happened at evaluate.
            var isV2Authorized = ReadOptionalString(request.Payload, "preparation_token") is not null
                || ReadOptionalString(request.Payload, "authorization_token") is not null;
            if (decision.RequiresApproval && !isV2Authorized)
            {
                var expiresAt = DateTimeOffset.UtcNow.AddSeconds(60);
                var argumentsHash = PendingApprovalStore.ComputeArgumentsHash(toolId, toolVersion, arguments);
                var terminalApprovalToken = toolId is "terminal.shell" or "terminal.run"
                    ? _approvalTokens.Issue(
                        request.RequestId,
                        request.SessionId,
                        toolId,
                        toolVersion,
                        TryComputeTerminalIntentHash(_defaultTools, toolId, arguments) ?? argumentsHash,
                        TimeSpan.FromSeconds(60))
                    : null;
                var approvalId = _approvals.Issue(
                    request,
                    binding.Manifest,
                    arguments,
                    SafeAction(binding.Manifest),
                    expiresAt,
                    terminalApprovalToken);
                return Result(
                    request,
                    ToolResultStatuses.ApprovalRequired,
                    new JsonObject
                    {
                        ["approval_id"] = approvalId,
                        ["tool_id"] = binding.Manifest.Id,
                        ["tool_version"] = binding.Manifest.Version,
                        ["scope"] = string.Join(", ", binding.Manifest.Scopes.OrderBy(value => value, StringComparer.Ordinal)),
                        ["safe_action"] = SafeAction(binding.Manifest),
                        ["expires_at"] = expiresAt.ToString("O"),
                        [ApprovalPreviewFields.Field] = BuildPreview(
                            toolId,
                            arguments,
                            ReadTargetFileText(arguments)),
                    },
                    "approval_required",
                    "Approval is required.",
                    started,
                    ToolVerificationStates.Pending,
                    adapterInvoked: false);
            }

            return await ExecuteBindingAsync(
                receiptRequest: request,
                contextRequest: request,
                binding,
                arguments,
                approved: ReadOptionalString(request.Payload, "authorization_token") is not null
                    || ReadOptionalString(request.Payload, "preparation_token") is not null,
                approvalToken: ReadOptionalString(request.Payload, "authorization_token")
                    ?? ReadOptionalString(request.Payload, "preparation_token"),
                ReadOptionalString(request.Payload, "run_id") ?? request.SessionId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result(
                request,
                ToolResultStatuses.Cancelled,
                new JsonObject(),
                "cancelled",
                "Tool cancelled.",
                started,
                ToolVerificationStates.NotRequired,
                adapterInvoked: false);
        }
        catch (KeyNotFoundException)
        {
            return Result(
                request,
                ToolResultStatuses.Failed,
                new JsonObject(),
                "tool_not_found",
                "Tool is not available.",
                started,
                ToolVerificationStates.NotRequired,
                adapterInvoked: false);
        }
        catch (ToolExecutionException error)
        {
            return Result(
                request,
                ToolResultStatuses.Failed,
                new JsonObject(),
                error.Code,
                "Tool request was rejected.",
                started,
                ToolVerificationStates.NotRequired,
                adapterInvoked: false);
        }
        catch (Exception)
        {
            return Result(
                request,
                ToolResultStatuses.Failed,
                new JsonObject(),
                "tool_failed",
                "Tool failed.",
                started,
                ToolVerificationStates.NotRequired,
                adapterInvoked: false);
        }
    }

    public async Task<ToolResult> RespondToApprovalAsync(
        AgentEnvelope request,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        try
        {
            var approvalId = ReadString(request.Payload, "approval_id");
            var approved = ReadBool(request.Payload, "approved");
            if (!_approvals.TryConsume(
                    approvalId,
                    request.SessionId,
                    approved,
                    DateTimeOffset.UtcNow,
                    out var pending))
            {
                return Result(
                    request,
                    ToolResultStatuses.Denied,
                    new JsonObject(),
                    "approval_stale",
                    "Approval is missing, expired, or already used.",
                    started,
                    ToolVerificationStates.NotRequired,
                    adapterInvoked: false);
            }

            if (!pending!.Approved)
            {
                return Result(
                    request,
                    ToolResultStatuses.Denied,
                    new JsonObject(),
                    "approval_rejected",
                    "Approval was rejected.",
                    started,
                    ToolVerificationStates.NotRequired,
                    adapterInvoked: false);
            }

            var binding = _catalog.Registry.Get(pending.Manifest.Id, pending.Manifest.Version);
            var decision = _policy.Evaluate(
                new PolicyGrant(
                    pending.Request.SessionId,
                    _session.Scopes,
                    _session.Descriptor.ExpiresAt),
                binding.Manifest);
            if (!decision.Allowed)
            {
                return Result(
                    request,
                    ToolResultStatuses.Denied,
                    new JsonObject(),
                    decision.Code,
                    "Tool scope is no longer granted.",
                    started,
                    ToolVerificationStates.NotRequired,
                    adapterInvoked: false);
            }

            return await ExecuteBindingAsync(
                receiptRequest: request,
                contextRequest: pending.Request,
                binding,
                pending.Arguments,
                approved: true,
                pending.TerminalApprovalToken,
                ReadOptionalString(pending.Request.Payload, "run_id") ?? pending.Request.SessionId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result(
                request,
                ToolResultStatuses.Cancelled,
                new JsonObject(),
                "cancelled",
                "Approved action cancelled.",
                started,
                ToolVerificationStates.NotRequired,
                adapterInvoked: false);
        }
        catch (ToolExecutionException error)
        {
            return Result(
                request,
                ToolResultStatuses.Failed,
                new JsonObject(),
                error.Code,
                "Approved action was rejected.",
                started,
                ToolVerificationStates.NotRequired,
                adapterInvoked: false);
        }
        catch (Exception)
        {
            return Result(
                request,
                ToolResultStatuses.Failed,
                new JsonObject(),
                "tool_failed",
                "Approved action failed.",
                started,
                ToolVerificationStates.NotRequired,
                adapterInvoked: false);
        }
    }

    public async Task<ToolResult> ExecuteSkillAsync(
        AgentEnvelope request,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        try
        {
            var skillId = ReadString(request.Payload, "skill_id");
            var version = ReadOptionalString(request.Payload, "version") ?? "1.0.0";
            var input = request.Payload["input"] is JsonObject objectNode
                ? objectNode
                : throw new ToolExecutionException("schema_invalid");
            var run = await ExecuteSkillAsync(
                request.SessionId,
                skillId,
                version,
                input,
                cancellationToken).ConfigureAwait(false);
            var status = run.State switch
            {
                SkillRunState.Completed => ToolResultStatuses.Succeeded,
                SkillRunState.Cancelled => ToolResultStatuses.Cancelled,
                SkillRunState.ApprovalRequired => ToolResultStatuses.ApprovalRequired,
                _ => ToolResultStatuses.Failed,
            };
            return Result(
                request,
                status,
                run.Output?.DeepClone().AsObject() ?? new JsonObject(),
                run.ErrorCode,
                run.SafeSummary,
                started,
                ToolVerificationStates.NotRequired,
                adapterInvoked: run.StepReceipts.Any(step => step.ToolId is not null));
        }
        catch (ToolExecutionException error)
        {
            return Result(request, ToolResultStatuses.Denied, new JsonObject(), error.Code, "Skill request was rejected.", started);
        }
        catch (Exception)
        {
            return Result(request, ToolResultStatuses.Failed, new JsonObject(), "skill_failed", "Skill failed.", started);
        }
    }

    private IReadOnlyDictionary<string, ToolExecutor> BuildExecutors() =>
        BuildExecutorsCore();

    private IReadOnlyDictionary<string, ToolExecutor> BuildExecutorsCore()
    {
        var executors = new Dictionary<string, ToolExecutor>(StringComparer.Ordinal)
        {
            ["system.time"] = (_, _, _) => Succeeded(_system.Time(), "System time read."),
            ["system.status"] = (_, _, _) => Succeeded(_system.Status(), "System status read."),
            ["help.docs"] = (_, arguments, cancellationToken) => Succeeded(
                _helpDocs.Execute(arguments, cancellationToken),
                "Bundled Ru documentation searched."),
            ["system.volume"] = ExecuteVolumeAsync,
            ["app.list"] = (_, _, _) => Succeeded(_apps.List(), "Application list read."),
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
            ["notes.create"] = (context, arguments, _) => ExecuteNotesCreate(context, arguments),
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

    private JsonObject ExecuteFileWrite(ToolExecutionContext context, JsonObject arguments)
    {
        var tools = ResolveTools(context, TargetPathOf(arguments));
        return tools.Write.Execute(WithInternalExpectedHash(tools.MutationResolver, arguments));
    }

    private JsonObject ExecuteDelete(ToolExecutionContext context, JsonObject arguments)
    {
        var tools = ResolveTools(context, TargetPathOf(arguments));
        return tools.Delete.Execute(WithInternalExpectedHash(tools.MutationResolver, arguments));
    }

    private static string? TargetPathOf(JsonObject arguments)
    {
        if (arguments["path"] is JsonValue value
            && value.TryGetValue<string>(out var path)
            && !string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        return null;
    }

    /// <summary>
    /// Selects the adapter root for one files.* call. Lease-rooted targets
    /// behave exactly as before. Outside targets execute against the longest
    /// matching session-granted directory; without a grant the adapters keep
    /// their own <c>outside_workspace</c> denial. Malformed inputs fall back
    /// to the lease toolset so adapters keep their native errors.
    /// files.edit/notes resolve via the input path; files.restore peeks the
    /// stored recovery record's canonical path before dispatch.
    /// </summary>
    private RootToolSet ResolveTools(ToolExecutionContext context, string? pathInput)
    {
        var tools = ToolsFor(context);
        if (string.IsNullOrWhiteSpace(pathInput))
        {
            return tools;
        }

        var grants = _session.DirectoryGrants.GetGrantedDirectories(context.SessionId);
        if (grants.Count == 0)
        {
            return tools;
        }

        string selectedRoot;
        try
        {
            selectedRoot = Files.FileToolRootSelector.SelectRoot(
                tools.MutationResolver,
                tools.MutationResolver.CanonicalRoot,
                grants,
                pathInput);
        }
        catch (ArgumentException)
        {
            return tools;
        }

        if (string.Equals(selectedRoot, tools.MutationResolver.CanonicalRoot, StringComparison.OrdinalIgnoreCase))
        {
            return tools;
        }

        return ToolsForRoot(selectedRoot);
    }

    private static JsonObject WithInternalExpectedHash(CanonicalPathResolver resolver, JsonObject arguments)
    {
        if (arguments["path"] is not JsonValue value || !value.TryGetValue<string>(out var pathInput))
        {
            return arguments;
        }

        string canonicalPath;
        try
        {
            canonicalPath = resolver.Resolve(pathInput).CanonicalPath;
        }
        catch (ArgumentException)
        {
            return arguments;
        }

        var enriched = (JsonObject)arguments.DeepClone();
        enriched["expected_sha256"] = FileHashService.GetCurrentHash(canonicalPath);
        return enriched;
 }

    private RootToolSet ToolsFor(ToolExecutionContext context)
    {
        if (!_session.TryGetRunLease(context.RunId, out var lease) || lease is null)
        {
            return _defaultTools;
        }

        return ToolsForRoot(lease.CanonicalRoot);
    }

    private RootToolSet ToolsForRoot(string canonicalRoot)
    {
        var root = Path.GetFullPath(canonicalRoot);
        return _toolSetsByRoot.GetOrAdd(root, static (_, resolvedRoot) => RootToolSet.Create(resolvedRoot), root);
    }

    private FileTools FileToolsFor(string canonicalRoot)
    {
        var root = Path.GetFullPath(canonicalRoot);
        return _fileToolsByRoot.GetOrAdd(root, static (_, resolvedRoot) => new FileTools(new WorkspacePathGuard(resolvedRoot)), root);
    }

    private sealed class RootToolSet
    {
        public required FileListAdapter List { get; init; }
        public required FileGlobAdapter Glob { get; init; }
        public required FileSearchAdapter Search { get; init; }
        public required FileReadAdapter Read { get; init; }
        public required FileWriteAdapter Write { get; init; }
        public required FileDeleteAdapter Delete { get; init; }
        public required FileRestoreAdapter Restore { get; init; }
        public required CanonicalPathResolver MutationResolver { get; init; }
        public required TerminalAdapter Terminal { get; init; }
        public required string CanonicalRoot { get; init; }
        public required NotesAdapter Notes { get; init; }
        public required FileEditAdapter Edit { get; init; }

        public static RootToolSet Create(string canonicalRoot)
        {
            return new RootToolSet
            {
                List = new FileListAdapter(canonicalRoot),
                Glob = new FileGlobAdapter(canonicalRoot),
                Search = new FileSearchAdapter(canonicalRoot),
                Read = new FileReadAdapter(canonicalRoot),
                Write = new FileWriteAdapter(canonicalRoot),
                Delete = new FileDeleteAdapter(canonicalRoot),
                Restore = new FileRestoreAdapter(canonicalRoot),
                Edit = new FileEditAdapter(canonicalRoot),
                MutationResolver = new CanonicalPathResolver(canonicalRoot),
                Terminal = new TerminalAdapter(
                    new WorkspacePathGuard(canonicalRoot),
                    new ShellRuntimeResolver()),
                CanonicalRoot = canonicalRoot,
                Notes = new NotesAdapter(new WorkspacePathGuard(canonicalRoot)),
            };
        }
    }

    private static HttpClient CreateWebHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
        };
        return new HttpClient(handler, disposeHandler: true);
    }

    private async Task<ToolResult> ExecuteBindingAsync(
        AgentEnvelope receiptRequest,
        AgentEnvelope contextRequest,
        ToolBinding binding,
        JsonObject arguments,
        bool approved,
        string? approvalToken,
        string runId,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        try
        {
            ToolSchemaValidator.Validate(binding.Manifest.InputSchema, arguments);
        }
        catch (ToolSchemaException error)
        {
            return Result(
                receiptRequest,
                ToolResultStatuses.Failed,
                new JsonObject(),
                error.Code,
                "Tool arguments were rejected.",
                started,
                ToolVerificationStates.NotRequired,
                adapterInvoked: false);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Result(
                receiptRequest,
                ToolResultStatuses.Cancelled,
                new JsonObject(),
                "cancelled",
                "Tool cancelled.",
                started,
                ToolVerificationStates.NotRequired,
                adapterInvoked: false);
        }

        var sessionScopes = _session.Scopes;
        var context = new ToolExecutionContext(
            contextRequest.RequestId,
            contextRequest.SessionId,
            contextRequest.CorrelationId,
            ReadCapabilityClass(contextRequest.Payload),
            contextRequest.DeadlineMs,
            approved,
            approvalToken,
            !sessionScopes.Contains(ToolScope.TerminalNetwork),
            sessionScopes,
            runId);
        ToolExecutionOutcome outcome;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Math.Clamp(context.DeadlineMs, 1, 300_000));
        try
        {
            outcome = await binding.Executor(context, arguments, deadline.Token).ConfigureAwait(false);
            outcome = Normalize(binding, outcome);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = new ToolExecutionOutcome(
                ToolResultStatuses.Cancelled,
                new JsonObject(),
                "process_cancelled",
                "Tool cancelled.",
                ToolVerificationStates.NotRequired);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            outcome = new ToolExecutionOutcome(
                ToolResultStatuses.TimedOut,
                new JsonObject(),
                "process_timed_out",
                "Tool timed out.",
                ToolVerificationStates.NotRequired);
        }
        catch (VolumeToolException error)
        {
            outcome = ToolExecutionOutcome.Failed(error.Code, error.SafeSummary);
        }
        catch (ToolSchemaException error)
        {
            outcome = ToolExecutionOutcome.Failed(error.Code, "Tool output was rejected.", VerificationFailure(binding));
        }
        catch (UnauthorizedAccessException error)
        {
            outcome = ToolExecutionOutcome.Failed(
                StableCode(error.Message, "access_denied"),
                "Tool access was denied.",
                VerificationFailure(binding));
        }
        catch (ArgumentException error)
        {
            outcome = ToolExecutionOutcome.Failed(
                StableCode(error.Message, "invalid_arguments"),
                "Tool arguments were rejected.",
                VerificationFailure(binding));
        }
        catch (IOException error)
        {
            outcome = ToolExecutionOutcome.Failed(
                StableCode(error.Message, "io_error"),
                "Tool input or output failed.",
                VerificationFailure(binding));
        }
        catch (InvalidDataException error)
        {
            outcome = ToolExecutionOutcome.Failed(
                StableCode(error.Message, "invalid_data"),
                "Tool response data was rejected.",
                VerificationFailure(binding));
        }
        catch (Exception)
        {
            outcome = ToolExecutionOutcome.Failed(
                "adapter_error",
                "Tool adapter failed.",
                VerificationFailure(binding));
        }

        outcome = outcome with
        {
            DurationMs = checked((int)Math.Min(int.MaxValue, started.ElapsedMilliseconds)),
        };
        return Receipt(receiptRequest, outcome, adapterInvoked: true);
    }

    private ValueTask<ToolExecutionOutcome> ExecuteVolumeAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(_volume.Value.Execute(arguments));

    private async ValueTask<ToolExecutionOutcome> ExecuteWebSearchAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (_webSearch is null)
        {
            throw new UnauthorizedAccessException("web_disabled");
        }

        var citations = await _webSearch.SearchAsync(
            ReadString(arguments, "query"),
            cancellationToken).ConfigureAwait(false);
        var output = new JsonObject
        {
            ["untrusted"] = true,
            ["citations"] = new JsonArray(citations.Select(ToJson).Cast<JsonNode?>().ToArray()),
        };
        return ToolExecutionOutcome.Succeeded(
            output,
            ToolVerificationStates.NotRequired,
            "Web search completed.");
    }

    private async ValueTask<ToolExecutionOutcome> ExecuteWebFetchAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (_webFetch is null)
        {
            throw new UnauthorizedAccessException("web_disabled");
        }

        var rawUri = ReadString(arguments, "url");
        if (!Uri.TryCreate(rawUri, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("web_uri_invalid");
        }

        var fetched = await _webFetch.FetchAsync(uri, cancellationToken).ConfigureAwait(false);
        return ToolExecutionOutcome.Succeeded(
            new JsonObject
            {
                ["untrusted"] = true,
                ["source"] = ToJson(fetched.Source),
                ["content"] = fetched.SanitizedText,
                ["content_type"] = fetched.ContentType,
            },
            ToolVerificationStates.NotRequired,
            "Web page fetched.");
    }

    private static JsonObject ToJson(SourceCitation citation) => new()
    {
        ["title"] = citation.Title.Length <= 256 ? citation.Title : citation.Title[..256],
        ["url"] = citation.Url.ToString(),
        ["snippet"] = citation.Snippet is null
            ? null
            : citation.Snippet.Length <= 512 ? citation.Snippet : citation.Snippet[..512],
    };

    private ValueTask<ToolExecutionOutcome> ExecuteFileEdit(ToolExecutionContext context, JsonObject arguments)
    {
        try
        {
            var tools = ResolveRootToolSet(context, TargetPathOf(arguments));
            int occurrences = 1;
            if (arguments["expected_occurrences"] is JsonValue value
                && value.TryGetValue<int>(out var parsed))
            {
                occurrences = parsed;
            }

            return Succeeded(
                tools.Edit.Execute(
                    WithInternalExpectedHash(tools.MutationResolver, arguments)),
                "Workspace file edited.");
        }
        catch (FileEditException error)
        {
            return ValueTask.FromResult(new ToolExecutionOutcome(
                ToolResultStatuses.Failed,
                error.Details ?? new JsonObject(),
                error.Code,
                "Workspace file was not edited.",
                ToolVerificationStates.NotRequired));
        }
    }

    private ValueTask<ToolExecutionOutcome> ExecuteRestore(ToolExecutionContext context, JsonObject arguments)
    {
        var tools = ResolveRestoreTools(context, arguments);
        return ValueTask.FromResult(ToolExecutionOutcome.Succeeded(
            tools.Restore.Execute(arguments),
            ToolVerificationStates.NotRequired,
            "Workspace file restored."));
    }

    private ValueTask<ToolExecutionOutcome> ExecuteNotesCreate(ToolExecutionContext context, JsonObject arguments)
    {
        var tools = ResolveRootToolSet(context, TargetPathOf(arguments));
        var inputPath = ReadString(arguments, "path");
        // NotesAdapter only accepts relative paths inside its workspace
        // guard; convert absolute grant-targeted paths so notes.create can
        // write inside a session-scoped directory grant.
        string relativePath = inputPath;
        if (Path.IsPathRooted(inputPath))
        {
            try
            {
                relativePath = Path.GetRelativePath(tools.MutationResolver.CanonicalRoot, inputPath);
            }
            catch
            {
                relativePath = inputPath;
            }
        }

        var output = tools.Notes.Create(
            relativePath,
            ReadString(arguments, "title"),
            ReadString(arguments, "content"),
            context.Approved);
        return ValueTask.FromResult(ToolExecutionOutcome.Succeeded(
            output,
            ToolVerificationStates.NotRequired,
            "Workspace note created."));
    }

    private FileTools ResolveFileTools(ToolExecutionContext context, string? pathInput)
    {
        var grants = _session.DirectoryGrants.GetGrantedDirectories(context.SessionId);
        if (string.IsNullOrWhiteSpace(pathInput) || grants.Count == 0)
        {
            return _files;
        }

        try
        {
            var selected = FileToolRootSelector.SelectRoot(
                _mutationResolver,
                _mutationResolver.CanonicalRoot,
                grants,
                pathInput);
            if (string.Equals(selected, _mutationResolver.CanonicalRoot, StringComparison.OrdinalIgnoreCase))
            {
                return _files;
            }

            return FileToolsFor(selected);
        }
        catch (ArgumentException)
        {
            return _files;
        }
        catch (UnauthorizedAccessException)
        {
            return _files;
        }
    }

    private RootToolSet ResolveRestoreTools(ToolExecutionContext context, JsonObject arguments)
    {
        if (!FileRestoreAdapter.TryGetCanonicalPath(
                ReadOptionalString(arguments, "recovery_id"),
                out var canonicalPath)
            || string.IsNullOrWhiteSpace(canonicalPath))
        {
            return ToolsFor(context);
        }

        return ResolveRootToolSet(context, canonicalPath!);
    }

    private NotesAdapter ResolveNotes(ToolExecutionContext context, string? pathInput)
    {
        var tools = ResolveRootToolSet(context, pathInput);
        return tools.Notes;
    }

    private RootToolSet ResolveRootToolSet(ToolExecutionContext context, string? pathInput)
    {
        var grants = _session.DirectoryGrants.GetGrantedDirectories(context.SessionId);
        if (string.IsNullOrWhiteSpace(pathInput) || grants.Count == 0)
        {
            return ToolsFor(context);
        }

        try
        {
            var selected = FileToolRootSelector.SelectRoot(
                _mutationResolver,
                _mutationResolver.CanonicalRoot,
                grants,
                pathInput!);
            if (string.Equals(selected, _mutationResolver.CanonicalRoot, StringComparison.OrdinalIgnoreCase))
            {
                return ToolsFor(context);
            }

            return ToolsForRoot(selected);
        }
        catch (ArgumentException)
        {
            return ToolsFor(context);
        }
        catch (UnauthorizedAccessException)
        {
            return ToolsFor(context);
        }
    }

    private async ValueTask<ToolExecutionOutcome> ExecuteTerminalRunAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var grant = new PolicyGrant(
            context.SessionId,
            context.SessionScopes ?? _session.Scopes,
            _session.Descriptor.ExpiresAt);
        var result = await ToolsFor(context).Terminal.RunAsync(
            grant,
            new TerminalRunRequest(
                ReadOptionalString(arguments, "shell") ?? "powershell",
                ReadOptionalString(arguments, "working_directory") ?? ".",
                ReadString(arguments, "executable"),
                ReadArguments(arguments["arguments"]),
                ReadOptionalInt(arguments, "timeout_ms") ?? TerminalLimits.DefaultTimeoutMs,
                IsTrue(arguments, "network_intent") || IsTrue(arguments, "network"),
                context.ApprovalToken ?? string.Empty,
                context.RequestId,
                context.SessionId,
                IsTrue(arguments, "read_only"),
                ReadWriteSet(arguments)),
            context.ConfinementRequired,
            cancellationToken).ConfigureAwait(false);
        return ProcessOutcome(result, context.ConfinementRequired);
    }

    private async ValueTask<ToolExecutionOutcome> ExecuteTerminalShellAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var confineWorkspace = context.ConfinementRequired;
        if (confineWorkspace && context.ApprovalToken is null)
        {
            throw new UnauthorizedAccessException("terminal_shell_approval_invalid");
        }

        var shellRequest = new TerminalShellRequest(
            ReadOptionalString(arguments, "shell") ?? "powershell",
            ReadOptionalString(arguments, "working_directory") ?? ".",
            ReadString(arguments, "script"),
            ReadOptionalInt(arguments, "timeout_ms") ?? TerminalLimits.DefaultTimeoutMs,
            context.ApprovalToken ?? string.Empty,
            context.RequestId,
            context.SessionId,
            TryComputeTerminalIntentHash(ToolsFor(context), "terminal.shell", arguments)
                ?? PendingApprovalStore.ComputeArgumentsHash("terminal.shell", "1.0.0", arguments));
        shellRequest = shellRequest with
        {
            ReadOnly = IsTrue(arguments, "read_only"),
            WriteSet = ReadWriteSet(arguments),
        };
        var result = await _terminal.RunApprovedShellAsync(
            shellRequest,
            confineWorkspace,
            cancellationToken).ConfigureAwait(false);
        return ProcessOutcome(result, confineWorkspace);
    }

    /// <summary>
    /// Resolves the canonical intent hash for a terminal call without executing it.
    /// Approval tokens bind to this hash so any change to the resolved executable,
    /// argv, working directory, operation class, network intent, or timeout between
    /// approval and execution invalidates the authorization.
    /// </summary>
    private string? TryComputeTerminalIntentHash(string toolId, JsonObject arguments) =>
        TryComputeTerminalIntentHash(_defaultTools, toolId, arguments);

    private string? TryComputeTerminalIntentHash(RootToolSet tools, string toolId, JsonObject arguments)
    {
        try
        {
            if (toolId == "terminal.shell")
            {
                return tools.Terminal.CanonicalizeShell(new TerminalShellRequest(
                    ReadOptionalString(arguments, "shell") ?? "powershell",
                    ReadOptionalString(arguments, "working_directory") ?? ".",
                    ReadString(arguments, "script"),
                    ReadOptionalInt(arguments, "timeout_ms") ?? TerminalLimits.DefaultTimeoutMs,
                    string.Empty)).IntentHash;
            }

            return toolId == "terminal.run"
                ? _terminal.CanonicalizeRun(new TerminalRunRequest(
                    "powershell",
                    ReadOptionalString(arguments, "working_directory") ?? ".",
                    ReadString(arguments, "executable"),
                    ReadArguments(arguments["arguments"]),
                    ReadOptionalInt(arguments, "timeout_ms") ?? TerminalLimits.DefaultTimeoutMs,
                    IsTrue(arguments, "network_intent") || IsTrue(arguments, "network"))).IntentHash
                : null;
        }
        catch (Exception error) when (
            error is ArgumentException or UnauthorizedAccessException or KeyNotFoundException
                or ToolExecutionException)
        {
            return null;
        }
    }

    private ToolExecutionOutcome ProcessOutcome(StreamingProcessResult result, bool confineWorkspace)
    {
        var output = new JsonObject
        {
            ["exit_code"] = result.ExitCode,
            ["duration_ms"] = result.DurationMs,
            ["stdout_head"] = result.StdoutHead,
            ["stdout_tail"] = result.StdoutTail,
            ["stderr_head"] = result.StderrHead,
            ["stderr_tail"] = result.StderrTail,
            ["stdout_bytes"] = result.StdoutBytes,
            ["stderr_bytes"] = result.StderrBytes,
            ["stdout_dropped_bytes"] = result.StdoutDroppedBytes,
            ["stderr_dropped_bytes"] = result.StderrDroppedBytes,
            ["frames_emitted"] = result.FramesEmitted,
            ["frames_dropped"] = result.FramesDropped,
            ["working_directory"] = confineWorkspace
                ? _workspace.ToRelative(result.WorkingDirectory)
                : BoundWorkingDirectory(result.WorkingDirectory),
        };
        return result.Status switch
        {
            ToolResultStatuses.Succeeded => ToolExecutionOutcome.Succeeded(
                output,
                ToolVerificationStates.NotRequired,
                "Terminal process completed."),
            ToolResultStatuses.Failed => new ToolExecutionOutcome(
                ToolResultStatuses.Failed,
                output,
                result.ExitCode is null ? "process_start_failed" : "process_exit_nonzero",
                "Terminal process failed.",
                ToolVerificationStates.NotRequired),
            ToolResultStatuses.TimedOut => new ToolExecutionOutcome(
                ToolResultStatuses.TimedOut,
                output,
                "process_timed_out",
                "Terminal process timed out.",
                ToolVerificationStates.NotRequired),
            ToolResultStatuses.Cancelled => new ToolExecutionOutcome(
                ToolResultStatuses.Cancelled,
                output,
                "process_cancelled",
                "Terminal process was cancelled.",
                ToolVerificationStates.NotRequired),
            _ => ToolExecutionOutcome.Failed(
                "invalid_executor_status",
                "Terminal process returned an invalid status.",
                ToolVerificationStates.NotRequired),
        };
    }

    private static string BoundWorkingDirectory(string workingDirectory)
    {
        if (string.IsNullOrEmpty(workingDirectory))
        {
            return string.Empty;
        }

        return workingDirectory.Length > 1024 ? workingDirectory[..1024] : workingDirectory;
    }

    private static ToolExecutionOutcome Normalize(ToolBinding binding, ToolExecutionOutcome outcome)
    {
        if (!ToolResultStatuses.Known.Contains(outcome.Status))
        {
            return ToolExecutionOutcome.Failed("invalid_executor_status", "Tool returned an invalid status.");
        }

        if (!ToolVerificationStates.Known.Contains(outcome.VerificationState))
        {
            return ToolExecutionOutcome.Failed("invalid_verification_state", "Tool verification was invalid.");
        }

        if (outcome.Output.ContainsKey("status"))
        {
            return ToolExecutionOutcome.Failed("conflicting_output_status", "Tool returned a conflicting status.");
        }

        if (binding.VerificationRequired
            && outcome.Status == ToolResultStatuses.Succeeded
            && outcome.VerificationState != ToolVerificationStates.Verified)
        {
            return ToolExecutionOutcome.Failed("verification_required", "Tool success was not verified.");
        }

        if (outcome.SafeSummary.Length > 4096)
        {
            return ToolExecutionOutcome.Failed("invalid_safe_summary", "Tool summary was invalid.");
        }

        try
        {
            if (outcome.Status == ToolResultStatuses.Succeeded || outcome.Output.Count > 0)
            {
                ToolSchemaValidator.Validate(binding.Manifest.OutputSchema, outcome.Output, "invalid_tool_output");
            }
        }
        catch (ToolSchemaException error)
        {
            return ToolExecutionOutcome.Failed(error.Code, "Tool output was rejected.", VerificationFailure(binding));
        }

        var outputBytes = Encoding.UTF8.GetByteCount(outcome.Output.ToJsonString());
        if (outputBytes > binding.Manifest.MaxOutputBytes)
        {
            return ToolExecutionOutcome.Failed("output_too_large", "Tool output exceeded its limit.", VerificationFailure(binding));
        }

        return outcome;
    }

    private static string SafeAction(ToolManifest manifest) => manifest.Id switch
    {
        "terminal.shell" => "Run a terminal action in the workspace.",
        "system.volume" => "Change the local system volume.",
        "notes.create" => "Create a note in the workspace.",
        "files.edit" => "Chỉnh sửa file.",
        _ => "Run an external or destructive Agent action.",
    };

    internal static string BuildPreview(string toolId, JsonObject arguments, string? fileText = null)
    {
        try
        {
            if (arguments is null)
            {
                return string.Empty;
            }

            var preview = toolId switch
            {
                "files.write" => ReadOptionalString(arguments, "content") ?? string.Empty,
                "files.edit" => EditPreview(arguments, fileText),
                "terminal.shell" => ReadOptionalString(arguments, "script") ?? string.Empty,
                "terminal.run" => TerminalRunPreview(arguments),
                _ => string.Empty,
            };
            return preview.Length <= ApprovalPreviewFields.MaxLength
                ? preview
                : preview[..ApprovalPreviewFields.MaxLength];
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string TerminalRunPreview(JsonObject arguments)
    {
        var executable = ReadOptionalString(arguments, "executable");
        if (string.IsNullOrWhiteSpace(executable))
        {
            return string.Empty;
        }

        var parts = new List<string> { executable };
        if (arguments["arguments"] is JsonArray list)
        {
            foreach (var item in list)
            {
                if (item is { } node && node.GetValueKind() == System.Text.Json.JsonValueKind.String)
                {
                    parts.Add(node.GetValue<string>());
                }
            }
        }

        return string.Join(' ', parts);
    }

    private static string EditPreview(JsonObject arguments, string? fileText)
    {
        var oldString = ReadOptionalString(arguments, "old_string") ?? string.Empty;
        var newString = ReadOptionalString(arguments, "new_string") ?? string.Empty;
        if (oldString.Length == 0 && newString.Length == 0)
        {
            return string.Empty;
        }

        var matchIndex = oldString.Length > 0 && fileText is not null
            ? fileText.IndexOf(oldString, StringComparison.Ordinal)
            : -1;
        if (matchIndex < 0)
        {
            return $"[-{Bound(oldString)}-][+{Bound(newString)}+]";
        }

        var before = fileText![..matchIndex];
        var after = fileText[(matchIndex + oldString.Length)..];
        var core = $"[-{oldString}-][+{newString}+]";
        var budget = ApprovalPreviewFields.MaxLength - core.Length;
        if (budget <= 0)
        {
            return core;
        }

        var afterCount = Math.Min(after.Length, budget / 2);
        var beforeCount = Math.Min(before.Length, budget - afterCount);
        afterCount = Math.Min(after.Length, budget - beforeCount);
        return $"{before[^beforeCount..]}{core}{after[..afterCount]}";
    }

    private static string Bound(string value) =>
        value.Length <= ApprovalPreviewFields.MaxLength / 2 ? value : value[..(ApprovalPreviewFields.MaxLength / 2)];

    private string? ReadTargetFileText(JsonObject arguments)
    {
        try
        {
            var relativePath = ReadOptionalString(arguments, "path");
            if (relativePath is null)
            {
                return null;
            }

            var fullPath = _workspace.Resolve(relativePath, allowMissing: true);
            if (!File.Exists(fullPath))
            {
                return null;
            }

            var info = new FileInfo(fullPath);
            if (info.Length > 4 * 1024 * 1024)
            {
                return null;
            }

            return StrictUtf8.GetString(ReadBoundedBytes(fullPath, FileWriteAdapter.MaxInputBytes));
        }
        catch
        {
            return null;
        }
    }

    private static byte[] ReadBoundedBytes(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
        using var output = new MemoryStream(Math.Min(maxBytes, 81920));
        var buffer = new byte[Math.Min(maxBytes, 81920)];
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            if (output.Length > maxBytes - read)
            {
                throw new ArgumentException("content_too_large");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
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

    private static string ReadCapabilityClass(JsonObject payload)
    {
        if (!payload.TryGetPropertyValue("capability_class", out var node) || node is null)
        {
            return "internal";
        }

        return node is JsonValue value
            && value.TryGetValue<string>(out var result)
            && !string.IsNullOrWhiteSpace(result)
            && result.Length <= 64
                ? result
                : throw new ToolExecutionException("invalid_capability_class");
    }

    private static string ReadString(JsonObject payload, string key) =>
        payload[key] is JsonValue value
        && value.TryGetValue<string>(out var result)
        && !string.IsNullOrWhiteSpace(result)
            ? result
            : throw new ToolExecutionException("invalid_arguments");

    private static bool ReadBool(JsonObject payload, string key) =>
        payload[key] is JsonValue value
        && value.TryGetValue<bool>(out var result)
            ? result
            : throw new ToolExecutionException("invalid_arguments");

    private static int? ReadOptionalInt(JsonObject payload, string key)
    {
        if (payload[key] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var result))
        {
            return result;
        }

        if (value.TryGetValue<long>(out var longResult) && longResult is >= int.MinValue and <= int.MaxValue)
        {
            return checked((int)longResult);
        }

        return null;
    }

    private static bool IsTrue(JsonObject payload, string key) =>
        payload[key] is JsonValue value
        && value.TryGetValue<bool>(out var flag)
        && flag;

    private static string? ReadOptionalString(JsonObject payload, string key) =>
        payload[key] is JsonValue value && value.TryGetValue<string>(out var result) && !string.IsNullOrWhiteSpace(result)
            ? result
            : null;

    private static IReadOnlyList<string> ReadArguments(JsonNode? node)
    {
        if (node is not JsonArray values || values.Count > 64)
        {
            throw new ToolExecutionException("invalid_arguments");
        }

        var result = new List<string>(values.Count);
        foreach (var value in values)
        {
            if (value is not JsonValue item || !item.TryGetValue<string>(out var text) || text.Length > 1024)
            {
                throw new ToolExecutionException("invalid_arguments");
            }

            result.Add(text);
        }

        return result;
    }

    private static IReadOnlyList<string> ReadWriteSet(JsonObject payload)
    {
        if (payload["write_set"] is null)
        {
            return Array.Empty<string>();
        }

        if (payload["write_set"] is not JsonArray values)
        {
            throw new ToolExecutionException("invalid_terminal_write_set");
        }

        if (values.Count > 256)
        {
            throw new ToolExecutionException("invalid_terminal_write_set");
        }

        var result = new List<string>(values.Count);
        foreach (var value in values)
        {
            if (value is not JsonValue item
                || !item.TryGetValue<string>(out var path)
                || string.IsNullOrWhiteSpace(path)
                || path.Length > 4096)
            {
                throw new ToolExecutionException("invalid_terminal_write_set");
            }

            result.Add(path);
        }

        try
        {
            WorkspaceWriteSet.Validate(IsTrue(payload, "read_only"), result);
        }
        catch (AgentProtocolException exception)
        {
            throw new ToolExecutionException(exception.Code);
        }

        return result;
    }

    private sealed class ToolExecutionException : Exception
    {
        public ToolExecutionException(string code) : base(code) => Code = code;
        public string Code { get; }
    }
}
