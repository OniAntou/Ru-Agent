using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Skills;
using RuAgent.Broker.Tools.Desktop;
using RuAgent.Broker.Tools.Notes;
using RuAgent.Broker.Tools.Terminal;
using RuAgent.Protocol;

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

    private readonly BrokerSession _session;
    private readonly ToolCatalog _catalog;
    private readonly ToolPolicyEngine _policy = new();
    private readonly ApprovalTokenService _approvalTokens = new();
    private readonly PendingApprovalStore _approvals = new();
    private readonly WorkspacePathGuard _workspace;
    private readonly FileTools _files;
    private readonly AppTools _apps = new();
    private readonly SystemTools _system = new();
    private readonly Lazy<VolumeTools> _volume;
    private readonly TerminalAdapter _terminal;
    private readonly NotesAdapter _notes;

    public ToolExecutionService(
        BrokerSession session,
        string workspaceRoot,
        IVolumeController? volumeController = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _workspace = new WorkspacePathGuard(workspaceRoot);
        _files = new FileTools(_workspace);
        _notes = new NotesAdapter(_workspace);
        _terminal = new TerminalAdapter(
            _workspace,
            new ProcessTreeRunner(),
            new ShellRuntimeResolver(),
            _approvalTokens);
        _volume = new Lazy<VolumeTools>(
            () => new VolumeTools(volumeController ?? new WindowsCoreAudioVolumeController()));
        _catalog = new ToolCatalog(BuildExecutors());
    }

    public IReadOnlyList<ToolManifest> ListTools() =>
        _catalog.Registry.List().Select(binding => binding.Manifest).ToArray();

    public IReadOnlyList<AgentToolCapability> ListCapabilities() =>
        _catalog.Registry.ListCapabilities();

    public IReadOnlyList<AgentToolCapability> ListCapabilities(CapabilityProfile profile) =>
        _catalog.Registry.ListCapabilities(profile);

    public IReadOnlyList<SkillManifest> ListSkills() => Array.Empty<SkillManifest>();

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

            if (decision.RequiresApproval)
            {
                var expiresAt = DateTimeOffset.UtcNow.AddSeconds(60);
                var argumentsHash = PendingApprovalStore.ComputeArgumentsHash(toolId, toolVersion, arguments);
                var terminalApprovalToken = toolId == "terminal.shell"
                    ? _approvalTokens.Issue(
                        request.RequestId,
                        request.SessionId,
                        toolId,
                        toolVersion,
                        argumentsHash,
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
                approved: false,
                approvalToken: null,
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

    public Task<ToolResult> ExecuteSkillAsync(
        AgentEnvelope request,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        return Task.FromResult(Result(
                request,
                ToolResultStatuses.Denied,
                new JsonObject(),
                "skills_disabled",
                "Skills are disabled.",
                started));
    }

    private IReadOnlyDictionary<string, ToolExecutor> BuildExecutors() =>
        new Dictionary<string, ToolExecutor>(StringComparer.Ordinal)
        {
            ["system.time"] = (_, _, _) => Succeeded(_system.Time(), "System time read."),
            ["system.status"] = (_, _, _) => Succeeded(_system.Status(), "System status read."),
            ["system.volume"] = ExecuteVolumeAsync,
            ["app.list"] = (_, _, _) => Succeeded(_apps.List(), "Application list read."),
            ["files.list"] = (_, arguments, _) => Succeeded(
                _files.List(ReadOptionalString(arguments, "path") ?? "."),
                "Workspace entries listed."),
            ["files.read"] = (_, arguments, _) => Succeeded(
                _files.Read(ReadString(arguments, "path")),
                "Workspace file read."),
            ["files.edit"] = (_, arguments, _) => ExecuteFileEdit(arguments),
            ["files.write"] = (_, arguments, _) => Succeeded(
                _files.Write(
                    ReadString(arguments, "path"),
                    ReadString(arguments, "content"),
                    arguments["overwrite"]?.GetValue<bool>() ?? false),
                "Workspace file written."),
            ["terminal.run"] = ExecuteTerminalRunAsync,
            ["terminal.shell"] = ExecuteTerminalShellAsync,
            ["notes.create"] = (context, arguments, _) => Succeeded(
                _notes.Create(
                    ReadString(arguments, "path"),
                    ReadString(arguments, "title"),
                    ReadString(arguments, "content"),
                    context.Approved),
                "Workspace note created."),
        };

    private async Task<ToolResult> ExecuteBindingAsync(
        AgentEnvelope receiptRequest,
        AgentEnvelope contextRequest,
        ToolBinding binding,
        JsonObject arguments,
        bool approved,
        string? approvalToken,
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
            sessionScopes);
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

    private ValueTask<ToolExecutionOutcome> ExecuteFileEdit(JsonObject arguments)
    {
        try
        {
            return Succeeded(
                _files.Edit(
                    ReadString(arguments, "path"),
                    ReadOptionalString(arguments, "old_string"),
                    ReadOptionalString(arguments, "new_string"),
                    arguments["expected_occurrences"] is JsonValue value
                        && value.TryGetValue<int>(out var occurrences)
                            ? occurrences
                            : 1),
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

    private async ValueTask<ToolExecutionOutcome> ExecuteTerminalRunAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var grant = new PolicyGrant(
            context.SessionId,
            context.SessionScopes ?? _session.Scopes,
            _session.Descriptor.ExpiresAt);
        var result = await _terminal.RunAsync(
            grant,
            new TerminalRunRequest(
                ReadOptionalString(arguments, "shell") ?? "powershell",
                ReadOptionalString(arguments, "working_directory") ?? ".",
                ReadString(arguments, "executable"),
                ReadArguments(arguments["arguments"]),
                Math.Min(context.DeadlineMs, 120_000),
                arguments["network"]?.GetValue<bool>() ?? false),
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

        var argumentsHash = PendingApprovalStore.ComputeArgumentsHash(
            "terminal.shell",
            "1.0.0",
            arguments);
        var result = await _terminal.RunApprovedShellAsync(
            new TerminalShellRequest(
                ReadOptionalString(arguments, "shell") ?? "powershell",
                ReadOptionalString(arguments, "working_directory") ?? ".",
                ReadString(arguments, "script"),
                Math.Min(context.DeadlineMs, 120_000),
                context.ApprovalToken ?? string.Empty,
                context.RequestId,
                context.SessionId,
                    argumentsHash),
            confineWorkspace,
            cancellationToken).ConfigureAwait(false);
        return ProcessOutcome(result, confineWorkspace);
    }

    private ToolExecutionOutcome ProcessOutcome(ProcessRunResult result, bool confineWorkspace)
    {
        var output = new JsonObject
        {
            ["exit_code"] = result.ExitCode,
            ["duration_ms"] = result.DurationMs,
            ["stdout"] = result.Stdout,
            ["stderr"] = result.Stderr,
            ["output_truncated"] = result.OutputTruncated,
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

            return File.ReadAllText(fullPath, Encoding.UTF8);
        }
        catch
        {
            return null;
        }
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

    private sealed class ToolExecutionException : Exception
    {
        public ToolExecutionException(string code) : base(code) => Code = code;
        public string Code { get; }
    }
}
