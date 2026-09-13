using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Policy.Commands;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools.Paths;

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
    private readonly PowerShellScriptAnalyzer _powerShell;

    public PolicyContextFactory(BrokerRunLease lease, CommandAnalyzerRegistry? commandAnalyzers = null)
    {
        ArgumentNullException.ThrowIfNull(lease);
        _canonicalRoot = lease.CanonicalRoot;
        _sessionId = lease.SessionId;
        _commandAnalyzers = commandAnalyzers ?? new CommandAnalyzerRegistry();
        _powerShell = new PowerShellScriptAnalyzer(_commandAnalyzers);
    }

    public PolicyContext Create(string toolId, string toolVersion, JsonObject arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentNullException.ThrowIfNull(arguments);

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
        var networkDeclared = IsTrue(arguments, "network_intent") || IsTrue(arguments, "network");
        int? timeoutMs = null;
        if (arguments.ContainsKey("timeout_ms"))
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
                terminalAnalysis = _powerShell.Analyze(script);
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
        }
        else
        {
            var op = toolId switch
            {
                "read" => OptionalText(arguments, "op")
                    ?? throw new PolicyContextException(
                        "malformed_call",
                        hint: "read requires op: \"list\", \"read\", or \"help\"."),
                "write" => OptionalText(arguments, "op")
                    ?? throw new PolicyContextException(
                        "malformed_call",
                        hint: "write requires op: \"write\", \"delete\", or \"restore\"."),
                "edit" => "edit",
                "grep" => "grep",
                "glob" => "glob",
                "web_search" => "web_search",
                "web_fetch" => "web_fetch",
                _ => throw new PolicyContextException(
                    "malformed_call",
                    hint: "unknown tool id; use read, grep, glob, write, edit, bash, web_search, or web_fetch."),
            };
            if ((toolId == "read" && op is not ("list" or "read" or "help"))
                || (toolId == "write" && op is not ("write" or "delete" or "restore")))
            {
                throw new PolicyContextException(
                    "malformed_call",
                    hint: "read op must be list|read|help and write op must be write|delete|restore.");
            }

            if (IsTargetFreeOp(toolId, op))
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
            ToolOp: toolId is "read" or "write" ? ReadOpOf(toolId, arguments) : null,
            BashMode: toolId == "bash" ? OptionalText(arguments, "mode") : null);
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
        "read" => ReadOpClass(OptionalText(arguments, "op")),
        "grep" => PolicyOperationClasses.Search,
        "glob" => PolicyOperationClasses.Glob,
        "write" => WriteOpClass(OptionalText(arguments, "op")),
        "edit" => PolicyOperationClasses.Edit,
        "bash" => PolicyOperationClasses.Terminal,
        "web_search" or "web_fetch" => PolicyOperationClasses.Read,
        _ => PolicyOperationClasses.Mutation,
    };

    private static string ReadOpClass(string? op) => op switch
    {
        "read" => PolicyOperationClasses.Read,
        "list" => PolicyOperationClasses.List,
        "help" => PolicyOperationClasses.Read,
        _ => PolicyOperationClasses.Read,
    };

    private static string WriteOpClass(string? op) => op switch
    {
        "write" => PolicyOperationClasses.Write,
        "delete" => PolicyOperationClasses.Delete,
        "restore" => PolicyOperationClasses.Restore,
        _ => PolicyOperationClasses.Write,
    };

    private static bool IsTargetFreeTool(string toolId) => false;

    private static bool IsTargetFreeOp(string toolId, string op) =>
        (toolId == "read" && op is "help")
        || toolId is "web_search" or "web_fetch";

    private static string? ReadOpOf(string toolId, JsonObject arguments) =>
        toolId is "read" or "write" ? OptionalText(arguments, "op") : null;

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
