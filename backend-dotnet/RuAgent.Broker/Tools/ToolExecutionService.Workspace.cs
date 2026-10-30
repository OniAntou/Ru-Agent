using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Configuration;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools.Files;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Broker.Tools.Terminal;
using RuAgent.Broker.Tools.Web;
using RuAgent.Broker.Tools.Help;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using BrokerRunLease = RuAgent.Broker.Runtime.BrokerRunLease;
using RuAgent.Broker.Tools.Todo;
using System.Security.Cryptography;
using System.Text.Json;

namespace RuAgent.Broker.Tools;

public sealed partial class ToolExecutionService
{
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

    private static JsonObject TranslateGrepArguments(JsonObject arguments)
    {
        var translated = new JsonObject();
        foreach (var property in arguments)
        {
            translated[property.Key] = property.Value?.DeepClone();
        }

        return translated;
    }

    private static JsonObject TranslateGlobArguments(JsonObject arguments)
    {
        var translated = new JsonObject();
        foreach (var property in arguments)
        {
            translated[property.Key] = property.Value?.DeepClone();
        }

        return translated;
    }

    private RootToolSet ResolveTools(ToolExecutionContext context, string? pathInput)
    {
        var tools = ToolsFor(context);
        if (string.IsNullOrWhiteSpace(pathInput))
        {
            return tools;
        }

        var grants = _session.DirectoryGrants.GetGrantedDirectories(context.SessionId);
        var profile = ActivePolicyProfile(context);
        try
        {
            var selectedRoot = profile is null
                ? FileToolRootSelector.SelectRoot(
                    tools.MutationResolver,
                    tools.MutationResolver.CanonicalRoot,
                    grants,
                    pathInput)
                : FileToolRootSelector.SelectRoot(
                    tools.MutationResolver,
                    tools.MutationResolver.CanonicalRoot,
                    grants,
                    profile,
                    pathInput);
            if (string.Equals(
                    selectedRoot,
                    tools.MutationResolver.CanonicalRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                return tools;
            }

            var shouldCache = profile is null || !IsFullAccessProfile(profile);
            var opensProtectedPaths = profile is not null
                && AgentPolicyProfileCatalog.OpensProtectedPaths(profile);
            return ToolsForRoot(selectedRoot, opensProtectedPaths, shouldCache);
        }
        catch (ArgumentException)
        {
            return tools;
        }
        catch (UnauthorizedAccessException)
        {
            return tools;
        }
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

    private AgentPolicyProfile? ActivePolicyProfile(ToolExecutionContext context) =>
        context.IsAuthenticatedV2Execution
        && ActiveLease(context.RunId, context.SessionId) is { } lease
            ? AgentPolicyProfileCatalog.Get(lease.Preset)
            : null;

    private BrokerRunLease? ActiveLease(string runId, string sessionId)
    {
        if (!_session.TryGetRunLease(runId, out var lease)
            || lease is null
            || lease.IsExpired(DateTimeOffset.UtcNow)
            || !string.Equals(lease.SessionId, sessionId, StringComparison.Ordinal))
        {
            return null;
        }

        return lease;
    }

    private RootToolSet ToolsFor(ToolExecutionContext context)
    {
        if (!context.IsAuthenticatedV2Execution
            || ActiveLease(context.RunId, context.SessionId) is not { } lease)
        {
            return _defaultTools;
        }

        return ToolsForRoot(
            lease.CanonicalRoot,
            AgentPolicyProfileCatalog.OpensProtectedPaths(AgentPolicyProfileCatalog.Get(lease.Preset)));
    }

    private RootToolSet ToolsForRoot(string canonicalRoot, bool allowProtectedPaths = false, bool cache = true)
    {
        var root = Path.GetFullPath(canonicalRoot);
        if (!cache)
        {
            return RootToolSet.Create(
                root,
                _approvalTokens,
                _recoveryStore,
                allowProtectedPaths);
        }

        // The root alone cannot key a tool set once protected-path handling is
        // preset-derived: the same root serves a different classifier per
        // preset, so the open and fail-closed sets stay apart. The suffix is
        // not a valid path character, so it can never collide with a root.
        var cacheKey = allowProtectedPaths ? root + "\u0000protected-open" : root;
        return _toolSetsByRoot.GetOrAdd(
            cacheKey,
            _ => RootToolSet.Create(
                root,
                _approvalTokens,
                _recoveryStore,
                allowProtectedPaths));
    }

    private static bool IsFullAccessProfile(AgentPolicyProfile profile) =>
        string.Equals(
            profile.Preset,
            AgentPolicyProfileCatalog.FullAccessPresetWireValue,
            StringComparison.Ordinal)
        && profile.DecisionFor(AgentPolicySubject.ExternalStructuredFile) == AgentPolicyDecision.Allow;

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
        public required FileEditAdapter Edit { get; init; }

        public static RootToolSet Create(
            string canonicalRoot,
            ApprovalTokenService approvalTokens,
            InMemoryRecoveryStore recoveryStore,
            bool allowProtectedPaths = false)
        {
            return new RootToolSet
            {
                List = new FileListAdapter(canonicalRoot, allowProtectedPaths),
                Glob = new FileGlobAdapter(canonicalRoot, allowProtectedPaths),
                Search = new FileSearchAdapter(canonicalRoot, allowProtectedPaths: allowProtectedPaths),
                Read = new FileReadAdapter(canonicalRoot, allowProtectedPaths),
                Write = new FileWriteAdapter(canonicalRoot, allowProtectedPaths),
                Delete = new FileDeleteAdapter(canonicalRoot, recoveryStore, allowProtectedPaths: allowProtectedPaths),
                Restore = new FileRestoreAdapter(canonicalRoot, recoveryStore, allowProtectedPaths: allowProtectedPaths),
                Edit = new FileEditAdapter(canonicalRoot, allowProtectedPaths),
                MutationResolver = new CanonicalPathResolver(canonicalRoot, allowProtectedPaths),
                Terminal = new TerminalAdapter(
                    new WorkspacePathGuard(canonicalRoot),
                    approvalTokens,
                    intentResolver: new CommandIntentResolver(
                        canonicalRoot)),
                CanonicalRoot = canonicalRoot,
            };
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

    private RootToolSet ResolveRestoreTools(ToolExecutionContext context, JsonObject arguments)
    {
        if (!_recoveryStore.TryGetCanonicalPath(
                ReadOptionalString(arguments, "recovery_id"),
                out var canonicalPath)
            || string.IsNullOrWhiteSpace(canonicalPath))
        {
            return ToolsFor(context);
        }

        return ResolveTools(context, canonicalPath!);
    }

    private RootToolSet ResolveRootToolSet(ToolExecutionContext context, string? pathInput)
    {
        if (context.IsAuthenticatedV2Execution)
        {
            return ToolsFor(context);
        }

        var grants = _session.DirectoryGrants.GetGrantedDirectories(context.SessionId);
        var baseTools = ToolsFor(context);
        if (string.IsNullOrWhiteSpace(pathInput) || grants.Count == 0)
        {
            return baseTools;
        }

        try
        {
            // Resolve against the same root the policy/approval leg used: the run
            // lease root on v2 executions (never the broker's configured root,
            // which may differ from the lease the user actually approved).
            var selected = FileToolRootSelector.SelectRoot(
                baseTools.MutationResolver,
                baseTools.CanonicalRoot,
                grants,
                pathInput!);
            if (string.Equals(selected, baseTools.MutationResolver.CanonicalRoot, StringComparison.OrdinalIgnoreCase))
            {
                return baseTools;
            }

            return ToolsForRoot(selected);
        }
        catch (ArgumentException)
        {
            return baseTools;
        }
        catch (UnauthorizedAccessException)
        {
            return baseTools;
        }
    }

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
        HardLinkGuard.RequireSingleLink(path);
        using var stream = HardLinkGuard.OpenRead(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
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

    private ValueTask<ToolExecutionOutcome> ExecutePatchAsync(ToolExecutionContext context, JsonObject arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var tools = ToolsFor(context);
        var patch = ReadString(arguments, "patch");
        var files = UnifiedPatchParser.Parse(patch);
        if (files.Count == 0) throw new ArgumentException("patch_empty");
        var hashes = new JsonObject();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (file.Hunks.Count == 0 || file.IsNewFile && file.IsDeletedFile) throw new ArgumentException("invalid_patch_header");
            if (!file.IsNewFile && !file.IsDeletedFile && !string.Equals(file.SourcePath, file.TargetPath, StringComparison.Ordinal))
                throw new ArgumentException("patch_rename_unsupported");
            var target = tools.MutationResolver.Resolve(file.TargetPath);
            if (target.IsOutsideWorkspace) throw new UnauthorizedAccessException("patch_outside_workspace");
            if (target.IsProtected) throw new UnauthorizedAccessException("protected_path");
            if (!paths.Add(target.CanonicalPath)) throw new ArgumentException("patch_duplicate_target");
            if (File.Exists(target.CanonicalPath)) totalBytes += new FileInfo(target.CanonicalPath).Length;
            if (totalBytes > FileMutationJournal.MaxTotalBytes) throw new ArgumentException("patch_preimages_too_large");
            var hash = FileHashService.GetCurrentHash(target.CanonicalPath);
            if (file.IsNewFile && !FileHashService.IsMissing(hash)) throw new IOException("file_exists");
            if (!file.IsNewFile && FileHashService.IsMissing(hash)) throw new IOException("file_not_found");
            if (file.IsDeletedFile)
            {
                var original = StrictUtf8.GetString(ReadBoundedBytes(target.CanonicalPath, FileWriteAdapter.MaxInputBytes));
                if (UnifiedPatchParser.ApplyHunks(original, file.Hunks).Length != 0)
                    throw new ArgumentException("patch_delete_not_empty");
            }
            hashes[file.TargetPath] = hash;
        }
        var adapterArguments = new JsonObject { ["patch"] = patch, ["expected_hashes"] = hashes };
        var profile = ActivePolicyProfile(context);
        var adapter = new FilePatchAdapter(tools.CanonicalRoot, allowProtectedPaths: profile is not null && AgentPolicyProfileCatalog.OpensProtectedPaths(profile));
        var output = FileMutationGuard.WithLock(() =>
        {
            // Resolve every hunk before the first write, then retain the same preimage hashes.
            adapter.Prepare(adapterArguments);
            ct.ThrowIfCancellationRequested();
            return adapter.Execute(adapterArguments);
        });
        return Succeeded(output, "Workspace patch applied.");
    }

    private async ValueTask<ToolExecutionOutcome> ExecuteLineReadAsync(ToolExecutionContext context, JsonObject arguments, CancellationToken ct) =>
        ToolExecutionOutcome.Succeeded(await ResolveTools(context, TargetPathOf(arguments)).Read.ExecuteLinesAsync(arguments, ct)
            .ConfigureAwait(false), ToolVerificationStates.NotRequired, "Workspace file read by line.");

    private ValueTask<ToolExecutionOutcome> ExecuteMultiEditAsync(ToolExecutionContext context, JsonObject arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var tools = ResolveTools(context, TargetPathOf(arguments));
        return Succeeded(tools.Edit.ExecuteMany(WithInternalExpectedHash(tools.MutationResolver, arguments)), "Workspace file edited atomically.");
    }

    private readonly TodoStore _todos = new();

    /// <summary>
    /// Replaces the session's task list with the one the model sent.
    /// </summary>
    /// <remarks>
    /// A rejected list never reaches the store, so a malformed write cannot erase the
    /// plan the user is reading. The summary returned to the model reports counts
    /// rather than the list, because it already sent the list and what it cannot derive
    /// is how much is left.
    /// </remarks>
    private ValueTask<ToolExecutionOutcome> ExecuteTodoWriteAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        if (string.IsNullOrWhiteSpace(context.SessionId))
        {
            return ValueTask.FromResult(ToolExecutionOutcome.Failed(
                "todo_session_required",
                "The task list belongs to a session and this call carried none."));
        }

        var validation = TodoListValidator.Validate(arguments["todos"]);
        if (!validation.IsValid)
        {
            return ValueTask.FromResult(ToolExecutionOutcome.Failed(
                validation.ErrorCode!,
                DescribeRejection(validation.ErrorCode!)));
        }

        _todos.Replace(context.SessionId, validation.Items);

        return ValueTask.FromResult(ToolExecutionOutcome.Succeeded(
            new JsonObject
            {
                ["todos"] = TodoPayload(validation.Items),
                ["summary"] = TodoStore.Describe(validation.Items),
            },
            ToolVerificationStates.NotRequired,
            TodoStore.Describe(validation.Items)));
    }

    /// <summary>
    /// What the model is told when its list is rejected.
    /// </summary>
    /// <remarks>
    /// Each rejection names the rule it broke, so the next attempt can fix it instead of
    /// resending the same list. The code travels beside this as the stable handle.
    /// </remarks>
    private static string DescribeRejection(string errorCode) => errorCode switch
    {
        "todo_list_required" => "A todo_write call needs a todos array.",
        "todo_too_many" => $"A task list holds at most {TodoListValidator.MaximumItems} items.",
        "todo_item_invalid" => "Every task must be an object with content and status.",
        "todo_content_empty" => "Every task needs a non-empty content line.",
        "todo_content_too_long" =>
            $"A task line holds at most {TodoListValidator.MaximumContentLength} characters.",
        "todo_status_unknown" => "Status must be pending, in_progress, or completed.",
        "todo_content_duplicate" => "Two tasks cannot share the same content.",
        "todo_multiple_active" => "At most one task may be in_progress.",
        _ => "The task list was rejected.",
    };

    private static JsonArray TodoPayload(IReadOnlyList<TodoRow> rows) =>
        new(rows.Select(row => (JsonNode)new JsonObject
        {
            ["content"] = row.Content,
            ["status"] = row.Status,
        }).ToArray());

    /// <summary>The task list for a session, for a caller that needs to read it.</summary>
    public IReadOnlyList<TodoRow> ReadTodos(string sessionId) => _todos.Read(sessionId);

    // Completion bookkeeping only. Approval still uses the full authorized intent,
    // including timeout and operation class; this digest can never authorize a call.
    private static void AddVerificationIdentity(JsonObject output, CanonicalCommandIntent intent)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain = "ru-terminal-verification-v1",
            executable = intent.ResolvedExecutable.ToUpperInvariant(),
            arguments = intent.Arguments,
            cwd = Path.TrimEndingDirectorySeparator(intent.CanonicalWorkingDirectory).ToUpperInvariant(),
        });
        output["verification_identity"] = Convert.ToHexString(SHA256.HashData(bytes));
        output["verification_cwd"] = intent.CanonicalWorkingDirectory;
        output["terminal_effect"] = intent.OperationClass == RuAgent.Protocol.TerminalOperationClass.ReadOnly
            ? "read_only" : "unknown";
    }
}
