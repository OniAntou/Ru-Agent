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

    /// <summary>
    /// Selects the adapter root for one files.* call. Lease-rooted targets
    /// behave exactly as before. Outside targets execute against the longest
    /// matching session-granted directory; without a grant the adapters keep
    /// their own <c>outside_workspace</c> denial. Malformed inputs fall back
    /// to the lease toolset so adapters keep their native errors.
    /// files.edit/notes resolve via the input path; files.restore peeks the
    /// stored recovery record's canonical path before dispatch.
    /// </summary>

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
        return _toolSetsByRoot.GetOrAdd(
            root,
            static (resolvedRoot, approvalTokens) => RootToolSet.Create(resolvedRoot, approvalTokens),
            _approvalTokens);
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
        public required FileEditAdapter Edit { get; init; }

        public static RootToolSet Create(string canonicalRoot, ApprovalTokenService approvalTokens)
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
                    new ShellRuntimeResolver(),
                    approvalTokens),
                CanonicalRoot = canonicalRoot,
            };
        }
    }

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
}
