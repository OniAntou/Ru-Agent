using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Policy.Commands;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Broker.Policy;

/// <summary>
/// Builds a <see cref="PolicyContext"/> from trusted resolution. The workspace root comes only from
/// the v2 run lease — never from RU_WORKSPACE_ROOT, the broker current directory, or model input.
/// </summary>
public sealed class PolicyContextFactory
{
    private readonly string _canonicalRoot;
    private readonly string _sessionId;
    private readonly CommandAnalyzerRegistry _commandAnalyzers;

    public PolicyContextFactory(BrokerRunLease lease, CommandAnalyzerRegistry? commandAnalyzers = null)
    {
        ArgumentNullException.ThrowIfNull(lease);
        _canonicalRoot = lease.CanonicalRoot;
        _sessionId = lease.SessionId;
        _commandAnalyzers = commandAnalyzers ?? new CommandAnalyzerRegistry();
    }

    public PolicyContext Create(string toolId, string toolVersion, JsonObject arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentNullException.ThrowIfNull(arguments);
        if (SubagentContractValidator.IsControlTool(toolId)
            && (toolVersion != "1.0.0" || !SubagentContractValidator.Validate(toolId,
                System.Text.Json.JsonSerializer.SerializeToElement(arguments)).IsValid))
            throw new PolicyContextException("invalid_subagent_arguments");

        if (arguments.ContainsKey("sandbox"))
        {
            throw new PolicyContextException("malformed_call",
                hint: "sandbox is no longer supported; remove it from the call.");
        }

        // Preimages are Broker-captured at execution time and must never enter
        // the model proposal; `regex` is the retired name of `is_regex`.
        if (arguments.ContainsKey("expected_sha256") || arguments.ContainsKey("regex"))
        {
            throw new PolicyContextException(
                "invalid_arguments",
                hint: "expected_sha256 and regex are captured by the host; remove them from the call.");
        }

        var resolver = new CanonicalPathResolver(_canonicalRoot);
        var operationClass = OperationClassFor(toolId, arguments);
        var background = IsTrue(arguments, "background");
        if (background && !string.Equals(operationClass, PolicyOperationClasses.Terminal, StringComparison.Ordinal))
        {
            throw new PolicyContextException(
                "malformed_call",
                hint: "background is only valid with bash mode \"run\" or \"shell\".");
        }

        var networkDeclared = IsTrue(arguments, "network_intent") || IsTrue(arguments, "network");
        int? timeoutMs = null;
        if (!SubagentContractValidator.IsControlTool(toolId) && arguments.ContainsKey("timeout_ms"))
        {
            if (arguments["timeout_ms"] is not JsonValue timeoutValue
                || !timeoutValue.TryGetValue<int>(out var parsedTimeout)
                || parsedTimeout < 1)
            {
                throw new PolicyContextException(
                    "malformed_call",
                    hint: "timeout_ms must be a positive integer number of milliseconds.");
            }

            timeoutMs = parsedTimeout;
        }

        ResolvedPath? target = null;
        ResolvedPath? terminalWorkingDirectory = null;
        string? canonicalCwd = null;
        var argv = PolicyContext.EmptyArgv;
        string? resolvedExecutable = null;
        CommandAnalysis? terminalAnalysis = null;

        if (operationClass == PolicyOperationClasses.Terminal)
        {
            var mode = OptionalText(arguments, "mode") ?? "run";
            string? shellScript = null;
            if (mode is not ("run" or "shell"))
            {
                throw new PolicyContextException(
                    "malformed_call",
                    hint: "bash mode must be \"run\" (executable plus arguments) or \"shell\" (script).");
            }

            var workingDirectory = OptionalText(arguments, "working_directory") ?? ".";
            try
            {
                var cwd = resolver.Resolve(workingDirectory);
                if (!Path.IsPathRooted(workingDirectory) && cwd.IsOutsideWorkspace)
                {
                    throw new PolicyContextException("invalid_arguments");
                }

                terminalWorkingDirectory = cwd;
                canonicalCwd = cwd.CanonicalPath;
            }
            catch (ArgumentException error)
            {
                throw new PolicyContextException("invalid_cwd", error);
            }

            if (string.Equals(mode, "run", StringComparison.Ordinal))
            {
                resolvedExecutable = OptionalText(arguments, "executable")
                    ?? throw new PolicyContextException(
                        "malformed_call",
                        hint: "bash mode \"run\" requires executable (and optional string arguments); use mode \"shell\" with a script for pipelines.");
            }
            else
            {
                var script = OptionalText(arguments, "script")
                    ?? throw new PolicyContextException(
                        "malformed_call",
                        hint: "bash mode \"shell\" requires script as a single string.");
                _ = OptionalText(arguments, "shell");
                // Shell text is bound verbatim; the installed shell owns syntax validation.
                shellScript = script;
            }

            if (arguments["arguments"] is JsonArray argvNode)
            {
                if (argvNode.Count > RuAgent.Protocol.TerminalLimits.MaxArguments)
                {
                    throw new PolicyContextException(
                        "malformed_call",
                        hint: "arguments has more entries than the terminal allows; reduce the argument vector.");
                }

                var list = new List<string>(argvNode.Count);
                foreach (var item in argvNode)
                {
                    if (item is not JsonValue value || !value.TryGetValue<string>(out var element))
                    {
                        throw new PolicyContextException(
                            "malformed_call",
                            hint: "arguments must be an array of strings, one entry per argument.");
                    }

                    if (element.Length > RuAgent.Protocol.TerminalLimits.MaxArgumentChars
                        || element.Contains('\0'))
                    {
                        throw new PolicyContextException(
                            "malformed_call",
                            hint: "one argument is too long or contains a null character.");
                    }

                    list.Add(element);
                }

                argv = list;
            }
            else if (arguments["arguments"] is not null || arguments.ContainsKey("arguments"))
            {
                throw new PolicyContextException(
                    "malformed_call",
                    hint: "arguments must be an array of strings, one entry per argument.");
            }

            if (string.Equals(mode, "run", StringComparison.Ordinal))
            {
                terminalAnalysis = _commandAnalyzers.Analyze(resolvedExecutable, argv);
            }
            else
            {
                // Match the Host's remembered shell approval. Policy runs before
                // executable resolution: keep the selected runtime and its exact
                // invocation, rather than an empty executable/argv that can never
                // match the user's approved script. Execution still resolves and
                // validates the executable through the terminal adapter.
                resolvedExecutable = OptionalText(arguments, "shell") ?? "powershell";
                argv = ["-NoProfile", "-NonInteractive", "-Command", shellScript!];
            }
        }
        else
        {
            string? op;
            switch (toolId)
            {
                case "read":
                    op = null;
                    break;
                case "list":
                    op = "list";
                    break;
                case "help":
                    op = "help";
                    break;
                case "todo_write":
                    // The task list has no path, no scope beyond the session, and no
                    // side effect outside the process: the policy only has to let it
                    // through, so its "op" is its own tool id and it is target-free.
                    op = "todo_write";
                    break;
                case "write":
                    var writeOp = OptionalText(arguments, "op")
                        ?? throw new PolicyContextException(
                            "malformed_call",
                            hint: "write requires op: \"write\", \"delete\", or \"restore\".");
                    if (writeOp is not ("write" or "delete" or "restore"))
                    {
                        throw new PolicyContextException(
                            "malformed_call",
                            hint: "write op must be write|delete|restore.");
                    }

                    op = writeOp;
                    break;
                case "edit":
                    op = "edit";
                    break;
                case "bash":
                    // Only the spool read and task kill reach this branch:
                    // run/shell classify as Terminal above. Anything else
                    // claiming Read is rejected.
                    if (IsSpoolRead(arguments))
                    {
                        op = "read_output";
                    }
                    else if (IsTaskKill(arguments))
                    {
                        op = "kill";
                    }
                    else
                    {
                        throw new PolicyContextException(
                            "malformed_call",
                            hint: "bash mode must be \"run\" (executable plus arguments), \"shell\" (script), \"read_output\" (execution_id), or \"kill\" (execution_id).");
                    }

                    break;
                case "grep":
                    op = "grep";
                    break;
                case "glob":
                    op = "glob";
                    break;
                case "web_search":
                    op = "web_search";
                    break;
                case "web_fetch":
                    op = "web_fetch";
                    break;
                case "mcp":
                    // An MCP call is keyed by server and tool, not a path. Server and
                    // tool travel as executable and argv so an "Always allow" rule can
                    // remember exactly that pair; the arguments stay in the intent hash.
                    op = OptionalText(arguments, "op");
                    if (op is not ("list_servers" or "list_tools" or "call"))
                    {
                        throw new PolicyContextException(
                            "malformed_call",
                            hint: "mcp op must be list_servers, list_tools or call.");
                    }

                    if (op != "list_servers")
                    {
                        var server = OptionalText(arguments, "server")
                            ?? throw new PolicyContextException("malformed_call", hint: "mcp list_tools and call require server.");
                        resolvedExecutable = "mcp:" + server;
                        if (op == "call")
                        {
                            argv = [OptionalText(arguments, "tool")
                                ?? throw new PolicyContextException("malformed_call", hint: "mcp call requires tool.")];
                        }
                    }

                    break;                case var control when SubagentContractValidator.IsControlTool(control):
                    op = control;
                    break;
                default:
                    throw new PolicyContextException(
                        "malformed_call",
                        hint: "unknown tool id; use read, list, help, todo_write, grep, glob, write, edit, bash, web_search, or web_fetch.");
            }

            if (IsTargetFreeOp(toolId, op!))
            {
                // help + web ops carry no workspace target; their identity is the
                // tool id + op + full argument set captured in the intent hash.
            }
            else if (string.Equals(op, "restore", StringComparison.Ordinal))
            {
                // The runtime restore call is keyed by recovery_id, while the
                // policy test/evaluation seam may still supply a path so that
                // protected/outside-workspace checks remain deterministic.  A
                // restore intent must provide one of these two bindings.
                var recoveryId = OptionalText(arguments, "recovery_id");
                var pathInput = OptionalText(arguments, "path");
                if (recoveryId is null && pathInput is null)
                {
                    throw new PolicyContextException(
                        "malformed_call",
                        hint: "write op \"restore\" requires recovery_id or path.");
                }

                if (pathInput is not null)
                {
                    target = ResolveNonTerminalTarget(pathInput);
                }
            }
            else
            {
                var pathInput = OptionalText(arguments, "path")
                    ?? (operationClass is PolicyOperationClasses.List
                        or PolicyOperationClasses.Glob
                        or PolicyOperationClasses.Search
                        ? "."
                        : null);
                if (pathInput is null)
                {
                    throw new PolicyContextException(
                        "malformed_call",
                        hint: "this call requires path (a workspace-relative path or an exact absolute path supplied by the user). Use \".\" for the workspace root.");
                }

                try
                {
                    target = ResolveNonTerminalTarget(pathInput);
                }
                catch (ArgumentException error) when (error.Message != "invalid_arguments")
                {
                    throw new PolicyContextException("invalid_path", error);
                }
            }
        }

        return new PolicyContext(
            ToolId: toolId,
            ToolVersion: toolVersion,
            CanonicalWorkspaceRoot: _canonicalRoot,
            Target: target,
            RelativePathInsideWorkspace: target is { IsInsideWorkspace: true } inside
                ? ToRelative(_canonicalRoot, inside.CanonicalPath)
                : null,
            ResolvedExecutable: resolvedExecutable,
            Argv: argv,
            CanonicalCwd: canonicalCwd,
            OperationClass: operationClass,
            NetworkIntent: networkDeclared ? PolicyNetworkIntents.Declared : PolicyNetworkIntents.None,
            NetworkDeclared: networkDeclared,
            IntentHash: ComputeIntentHash(
                toolId,
                toolVersion,
                target?.CanonicalIntentHash,
                resolvedExecutable,
                argv,
                canonicalCwd,
                operationClass,
                arguments),
            TimeoutMs: timeoutMs,
            SessionId: _sessionId,
            WorkingDirectory: terminalWorkingDirectory,
            TerminalAnalysis: terminalAnalysis,
            ToolOp: toolId is "read" or "write" or "mcp" ? ReadOpOf(toolId, arguments) : null,
            BashMode: toolId == "bash" ? OptionalText(arguments, "mode") : null,
            Background: background);
    }

    private ResolvedPath ResolveNonTerminalTarget(string pathInput)
    {
        var resolver = new CanonicalPathResolver(_canonicalRoot);
        try
        {
            var resolved = resolver.Resolve(pathInput);
            // A relative path that resolves outside the workspace is a proposal
            // escape attempt: the model must address workspace files directly.
            // Absolute targets keep following the outside-workspace approval path.
            if (!Path.IsPathRooted(pathInput) && resolved.IsOutsideWorkspace)
            {
                throw new ArgumentException("invalid_arguments");
            }

            return resolved;
        }
        catch (PolicyContextException)
        {
            throw;
        }
        catch (ArgumentException error) when (error.Message == "invalid_arguments")
        {
            throw new PolicyContextException(
                "invalid_arguments",
                hint: "a relative path must stay inside the workspace; use an absolute path for a directory granted outside it.");
        }
        catch (ArgumentException error)
        {
            throw new PolicyContextException("invalid_path", error);
        }
    }

    private static string OperationClassFor(string toolId, JsonObject arguments) => toolId switch
    {
        "read" => PolicyOperationClasses.Read,
        "list" => PolicyOperationClasses.List,
        "help" => PolicyOperationClasses.Read,
        "todo_write" => PolicyOperationClasses.Read,
        "grep" => PolicyOperationClasses.Search,
        "glob" => PolicyOperationClasses.Glob,
        "write" => WriteOpClass(OptionalText(arguments, "op")),
        "edit" => PolicyOperationClasses.Edit,
        // A spool read or a background-task kill executes nothing: the read
        // pages back output the same session already received and the kill
        // stops a process the same session already started, so both classify
        // as a read and inherit read policy (profile allows, explicit deny
        // rules still match by tool id).
        "bash" => IsSpoolRead(arguments) || IsTaskKill(arguments) ? PolicyOperationClasses.Read : PolicyOperationClasses.Terminal,
        "web_search" or "web_fetch" => PolicyOperationClasses.Read,
        // Listing declared servers reads the user's own file; starting a server or calling
        // a tool runs a program, which plan denies like any mutation.
        "mcp" => OptionalText(arguments, "op") == "list_servers" ? PolicyOperationClasses.Read : PolicyOperationClasses.Mutation,
        _ when SubagentContractValidator.IsControlTool(toolId) => PolicyOperationClasses.Read,
        _ => PolicyOperationClasses.Mutation,
    };

    private static bool IsSpoolRead(JsonObject arguments) =>
        string.Equals(OptionalText(arguments, "mode"), "read_output", StringComparison.Ordinal);

    private static bool IsTaskKill(JsonObject arguments) =>
        string.Equals(OptionalText(arguments, "mode"), "kill", StringComparison.Ordinal);

    private static string WriteOpClass(string? op) => op switch
    {
        "write" => PolicyOperationClasses.Write,
        "delete" => PolicyOperationClasses.Delete,
        "restore" => PolicyOperationClasses.Restore,
        _ => PolicyOperationClasses.Write,
    };

    private static bool IsTargetFreeOp(string toolId, string op) =>
        toolId == "help"
        || toolId == "todo_write"
        || toolId is "web_search" or "web_fetch" or "mcp"
        || SubagentContractValidator.IsControlTool(toolId)
        // A spool read names a session-scoped output id, not a path; a task
        // kill names a session-scoped task id. The execution leg still fails
        // closed on unknown or foreign ids.
        || (string.Equals(toolId, "bash", StringComparison.Ordinal)
            && (string.Equals(op, "read_output", StringComparison.Ordinal)
                || string.Equals(op, "kill", StringComparison.Ordinal)));

    private static string? ReadOpOf(string toolId, JsonObject arguments) =>
        toolId is "read" or "write" or "mcp" ? OptionalText(arguments, "op") : null;

    private static string? OptionalText(JsonObject arguments, string key)
    {
        if (!arguments.ContainsKey(key))
        {
            return null;
        }

        if (arguments[key] is not JsonValue value
            || !value.TryGetValue<string>(out var result)
            || string.IsNullOrWhiteSpace(result)
            || result.Contains('\0'))
        {
            throw new PolicyContextException(
                "malformed_call",
                hint: $"argument \"{key}\" must be a non-empty string.");
        }

        return result;
    }

    private static bool IsTrue(JsonObject arguments, string key)
    {
        if (!arguments.ContainsKey(key))
        {
            return false;
        }

        if (arguments[key] is not JsonValue value
            || !value.TryGetValue<bool>(out var flag))
        {
            throw new PolicyContextException(
                "malformed_call",
                hint: $"argument \"{key}\" must be true or false.");
        }

        return flag;
    }

    private static string ToRelative(string root, string canonicalPath)
    {
        var relative = Path.GetRelativePath(root, canonicalPath);
        return relative == "." ? string.Empty : relative.Replace('\\', '/');
    }

    private static string ComputeIntentHash(
        string toolId,
        string toolVersion,
        string? targetHash,
        string? resolvedExecutable,
        IReadOnlyList<string> argv,
        string? canonicalCwd,
        string operationClass,
        JsonObject arguments)
    {
        var builder = new StringBuilder();
        builder.Append(toolId).Append('|').Append(toolVersion).Append('|')
            .Append(targetHash ?? "-").Append('|')
            .Append(resolvedExecutable ?? "-").Append('|')
            .Append(string.Join(' ', argv)).Append('|')
            .Append(canonicalCwd ?? "-").Append('|')
            .Append(operationClass).Append('|')
            // Bind every model-controlled argument, not only the resolved path. Without
            // this, a token for files.search could be replayed with a different query or
            // a files.read token could be replayed with a different offset/limit.
            .Append(CanonicalArgumentsHash(arguments));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static string CanonicalArgumentsHash(JsonObject arguments)
    {
        var canonical = Canonicalize(arguments).ToJsonString();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static JsonNode Canonicalize(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            var ordered = new JsonObject();
            foreach (var property in obj.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                ordered[property.Key] = property.Value is null ? null : Canonicalize(property.Value);
            }

            return ordered;
        }

        if (node is JsonArray array)
        {
            return new JsonArray(array.Select(item => item is null ? null : Canonicalize(item)).ToArray());
        }

        return node.DeepClone();
    }
}
