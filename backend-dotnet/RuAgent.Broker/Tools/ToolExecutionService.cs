using System.Diagnostics;
using System.Text.Json.Nodes;
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
    private readonly BrokerSession _session;
    private readonly ToolRegistry _registry;
    private readonly SkillRegistry _skills;
    private readonly SkillOrchestrator _skillOrchestrator;
    private readonly ToolPolicyEngine _policy;
    private readonly WorkspacePathGuard _workspace;
    private readonly FileTools _files;
    private readonly AppTools _apps = new();
    private readonly SystemTools _system = new();
    private readonly TerminalAdapter _terminal;
    private readonly NotesAdapter _notes;

    public ToolExecutionService(BrokerSession session, string workspaceRoot)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _registry = new ToolRegistry();
        _ = new ToolCatalog(_registry);
        _policy = new ToolPolicyEngine();
        _workspace = new WorkspacePathGuard(workspaceRoot);
        _files = new FileTools(_workspace);
        _notes = new NotesAdapter(_workspace);
        _terminal = new TerminalAdapter(_workspace, new ProcessTreeRunner(), new ShellRuntimeResolver());
        _skills = new SkillRegistry();
        LoadSkills(workspaceRoot);
        _skillOrchestrator = new SkillOrchestrator(_registry, _policy, this);
    }

    public IReadOnlyList<ToolManifest> ListTools() => _registry.List();

    public IReadOnlyList<SkillManifest> ListSkills() => _skills.List();

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
            var manifest = _registry.Get(toolId, toolVersion);
            var decision = _policy.Evaluate(
                new PolicyGrant(
                    request.SessionId,
                    _session.Scopes,
                    _session.Descriptor.ExpiresAt),
                manifest);
            if (!decision.Allowed)
            {
                return Result(request, ToolResultStatuses.Denied, new JsonObject(), decision.Code, "Tool scope is not granted.", started);
            }

            if (decision.RequiresApproval)
            {
                return Result(request, ToolResultStatuses.ApprovalRequired, new JsonObject(), "approval_required", "Approval is required.", started);
            }

            var output = await DispatchAsync(toolId, arguments, request.DeadlineMs, cancellationToken).ConfigureAwait(false);
            return Result(request, ToolResultStatuses.Succeeded, output, null, "Tool completed.", started);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result(request, ToolResultStatuses.Cancelled, new JsonObject(), "cancelled", "Tool cancelled.", started);
        }
        catch (ToolExecutionException error)
        {
            return Result(request, ToolResultStatuses.Failed, new JsonObject(), error.Code, "Tool request was rejected.", started);
        }
        catch (Exception)
        {
            return Result(request, ToolResultStatuses.Failed, new JsonObject(), "tool_failed", "Tool failed.", started);
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
            var skillVersion = ReadOptionalString(request.Payload, "skill_version") ?? "1.0.0";
            var input = request.Payload["input"] is JsonObject inputObject
                ? inputObject
                : throw new ToolExecutionException("invalid_skill_input");
            if (!_session.SkillIds.Contains(skillId))
            {
                return Result(request, ToolResultStatuses.Denied, new JsonObject(), "skill_scope_missing", "Skill is not granted.", started);
            }

            var skill = _skills.Get(skillId, skillVersion);
            var run = await _skillOrchestrator.RunAsync(
                request.SessionId,
                skill,
                new PolicyGrant(request.SessionId, _session.Scopes, _session.Descriptor.ExpiresAt),
                input,
                cancellationToken).ConfigureAwait(false);
            var status = run.State switch
            {
                SkillRunState.Completed => ToolResultStatuses.Succeeded,
                SkillRunState.ApprovalRequired => ToolResultStatuses.ApprovalRequired,
                SkillRunState.Cancelled => ToolResultStatuses.Cancelled,
                _ => ToolResultStatuses.Failed,
            };
            return Result(
                request,
                status,
                new JsonObject
                {
                    ["skill_id"] = run.SkillId,
                    ["step_index"] = run.StepIndex,
                },
                status == ToolResultStatuses.Succeeded ? null : status,
                run.SafeSummary,
                started);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result(request, ToolResultStatuses.Cancelled, new JsonObject(), "cancelled", "Skill cancelled.", started);
        }
        catch (KeyNotFoundException)
        {
            return Result(request, ToolResultStatuses.Denied, new JsonObject(), "skill_not_found", "Skill is not registered.", started);
        }
        catch (ToolExecutionException error)
        {
            return Result(request, ToolResultStatuses.Failed, new JsonObject(), error.Code, "Skill request was rejected.", started);
        }
    }

    private async Task<JsonObject> DispatchAsync(
        string toolId,
        JsonObject arguments,
        int deadlineMs,
        CancellationToken cancellationToken)
    {
        return toolId switch
        {
            "system.time" => _system.Time(),
            "system.status" => _system.Status(),
            "app.list" => _apps.List(),
            "app.open" => _apps.Open(ReadString(arguments, "target")),
            "files.list" => _files.List(ReadOptionalString(arguments, "path") ?? "."),
            "files.read" => _files.Read(ReadString(arguments, "path")),
            "files.write" => _files.Write(
                ReadString(arguments, "path"),
                ReadString(arguments, "content"),
                arguments["overwrite"]?.GetValue<bool>() ?? false),
            "terminal.run" => await RunTerminalAsync(arguments, deadlineMs, cancellationToken).ConfigureAwait(false),
            "notes.create" => _notes.Create(
                ReadString(arguments, "path"),
                ReadString(arguments, "title"),
                ReadString(arguments, "content"),
                approved: false),
            _ => throw new ToolExecutionException("tool_not_implemented"),
        };
    }

    private void LoadSkills(string workspaceRoot)
    {
        var skillRoot = Path.Combine(workspaceRoot, "skills");
        if (!Directory.Exists(skillRoot))
        {
            return;
        }

        var loader = new SkillPackageLoader();
        foreach (var package in Directory.EnumerateDirectories(skillRoot))
        {
            _skills.Register(loader.Load(package), _registry);
        }
    }

    private async Task<JsonObject> RunTerminalAsync(
        JsonObject arguments,
        int deadlineMs,
        CancellationToken cancellationToken)
    {
        var result = await _terminal.RunAsync(
            new TerminalRunRequest(
                ReadOptionalString(arguments, "shell") ?? "powershell",
                ReadOptionalString(arguments, "working_directory") ?? ".",
                ReadString(arguments, "executable"),
                ReadArguments(arguments["arguments"]),
                Math.Min(deadlineMs, 120_000),
                arguments["network"]?.GetValue<bool>() ?? false),
            cancellationToken).ConfigureAwait(false);
        return new JsonObject
        {
            ["status"] = result.Status,
            ["exit_code"] = result.ExitCode,
            ["duration_ms"] = result.DurationMs,
            ["stdout"] = result.Stdout,
            ["stderr"] = result.Stderr,
            ["output_truncated"] = result.OutputTruncated,
            ["working_directory"] = _workspace.ToRelative(result.WorkingDirectory),
        };
    }

    private static ToolResult Result(
        AgentEnvelope request,
        string status,
        JsonObject output,
        string? errorCode,
        string summary,
        Stopwatch stopwatch) => new(
        request.RequestId,
        request.SessionId,
        status,
        output,
        errorCode,
        summary,
        (int)stopwatch.Elapsed.TotalMilliseconds);

    private static string ReadString(JsonObject payload, string key) =>
        payload[key] is JsonValue value
        && value.TryGetValue<string>(out var result)
        && !string.IsNullOrWhiteSpace(result)
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
