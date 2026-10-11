using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Configuration;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools.Desktop;
using RuAgent.Broker.Tools.Files;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Broker.Tools.Terminal;
using RuAgent.Broker.Tools.Web;
using RuAgent.Broker.Tools.Help;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using BrokerRunLease = RuAgent.Broker.Runtime.BrokerRunLease;

namespace RuAgent.Broker.Tools;

public sealed partial class ToolExecutionService
{
    private async ValueTask<ToolExecutionOutcome> ExecuteWriteOpAsync(ToolExecutionContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var op = ReadOptionalString(arguments, "op") ?? "write";
        return op switch
        {
            "write" => ToolExecutionOutcome.Succeeded(
                ExecuteFileWrite(context, arguments),
                ToolVerificationStates.NotRequired,
                "Workspace file written."),
            "delete" => ToolExecutionOutcome.Succeeded(
                ExecuteDelete(context, arguments),
                ToolVerificationStates.NotRequired,
                "Workspace file deleted."),
            "restore" => await ExecuteRestore(context, arguments).ConfigureAwait(false),
            _ => throw new ArgumentException("unknown_write_op"),
        };
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
            return ToolsForRoot(selectedRoot, shouldCache);
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

        return ToolsForRoot(lease.CanonicalRoot);
    }

    private RootToolSet ToolsForRoot(string canonicalRoot, bool cache = true)
    {
        var root = Path.GetFullPath(canonicalRoot);
        if (!cache)
        {
            return RootToolSet.Create(
                root,
                _approvalTokens,
                _recoveryStore);
        }

        return _toolSetsByRoot.GetOrAdd(
            root,
            resolvedRoot => RootToolSet.Create(
                resolvedRoot,
                _approvalTokens,
                _recoveryStore));
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
            InMemoryRecoveryStore recoveryStore)
        {
            return new RootToolSet
            {
                List = new FileListAdapter(canonicalRoot),
                Glob = new FileGlobAdapter(canonicalRoot),
                Search = new FileSearchAdapter(canonicalRoot),
                Read = new FileReadAdapter(canonicalRoot),
                Write = new FileWriteAdapter(canonicalRoot),
                Delete = new FileDeleteAdapter(canonicalRoot, recoveryStore),
                Restore = new FileRestoreAdapter(canonicalRoot, recoveryStore),
                Edit = new FileEditAdapter(canonicalRoot),
                MutationResolver = new CanonicalPathResolver(canonicalRoot),
                Terminal = new TerminalAdapter(
                    new WorkspacePathGuard(canonicalRoot),
                    approvalTokens,
                    intentResolver: new CommandIntentResolver(
                        canonicalRoot)),
                CanonicalRoot = canonicalRoot,
            };
        }
    }

    private ValueTask<ToolExecutionOutcome> ExecuteFileEdit(ToolExecutionContext context, JsonObject arguments)
    {
        var tools = ResolveTools(context, TargetPathOf(arguments));
        // EditMatchException is deliberately not caught here: the executor catch in
        // ExecuteBindingAsync turns it into the repair hint handed back to the model.
        return Succeeded(
            tools.Edit.Execute(
                WithInternalExpectedHash(tools.MutationResolver, arguments)),
            "Workspace file edited.");
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
}
