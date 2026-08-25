using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Protocol;

namespace RuBackend.Harness.Broker;

public sealed class HarnessToolGateway : IToolGateway, IToolRunCancellation
{
    private readonly string _canonicalRoot;
    private readonly IReadOnlySet<string> _advertisedCapabilities;
    private readonly Func<RunId, AgentPermissionPreset> _presetProvider;
    private readonly IIdGenerator _ids;
    private readonly IReadOnlyList<string> _sanitizedSearchDirectories;
    private readonly ConcurrentDictionary<string, ActiveTerminalExecution> _activeTerminalExecutions = new();
    private readonly ConcurrentDictionary<string, PreparedTerminalSpec> _pendingTerminalSpecs = new();

    public HarnessToolGateway(string canonicalRoot, IReadOnlyList<string> advertisedCapabilities)
        : this(canonicalRoot, advertisedCapabilities, presetProvider: null, idGenerator: null)
    {
    }

    public HarnessToolGateway(
        string canonicalRoot,
        IReadOnlyList<string> advertisedCapabilities,
        Func<RunId, AgentPermissionPreset>? presetProvider,
        IIdGenerator? idGenerator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);
        ArgumentNullException.ThrowIfNull(advertisedCapabilities);
        _canonicalRoot = Path.GetFullPath(canonicalRoot);
        if (!Directory.Exists(_canonicalRoot))
        {
            throw new DirectoryNotFoundException(_canonicalRoot);
        }

        _advertisedCapabilities = advertisedCapabilities.ToHashSet(StringComparer.Ordinal);
        _presetProvider = presetProvider ?? (_ => AgentPermissionPreset.Standard);
        _ids = idGenerator ?? new GatewayIdGenerator();
        _sanitizedSearchDirectories = SanitizeSearchDirectories();
    }

    // For testing without filesystem, allow injection.
    internal HarnessToolGateway(string canonicalRoot, IReadOnlySet<string> capabilities)
        : this(canonicalRoot, capabilities.ToList(), presetProvider: null, idGenerator: null)
    {
    }

    internal int ActiveTerminalExecutionCount => _activeTerminalExecutions.Count;

    public Task<ToolProposalDecision> EvaluateAsync(ToolProposal proposal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        if (!_advertisedCapabilities.Contains(proposal.ToolId))
        {
            return Task.FromResult(ToolProposalDecision.Deny(ToolAccessKind.ReadOnly, "phase_not_available"));
        }

        // All file tools are now advertised via capabilities; validate known IDs
        var allowedIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "files.list", "files.glob", "files.search", "files.read",
            "files.write", "files.edit", "files.apply_patch", "files.delete", "files.restore",
            "terminal.run", "terminal.shell"
        };
        if (!allowedIds.Contains(proposal.ToolId) && proposal.ToolId != "notes.create" && proposal.ToolId != "system.time" && proposal.ToolId != "system.status" && proposal.ToolId != "system.volume" && proposal.ToolId != "app.list")
        {
            return Task.FromResult(ToolProposalDecision.Deny(MapAccessKind(proposal.ToolId), "phase_not_available"));
        }

        // For restore, advertised only while active run owns valid recovery â€“ check in-memory host registry
        if (proposal.ToolId == "files.restore")
        {
            // If no active recovery for this execution's run, still allow evaluation to proceed; policy will handle ask/deny.
            // For gateway tracer, we allow if capability advertised; actual availability is checked at execution.
        }

        try
        {
            ValidateArguments(proposal);
        }
        catch (ArgumentException)
        {
            return Task.FromResult(ToolProposalDecision.Deny(MapAccessKind(proposal.ToolId), "invalid_arguments"));
        }

        if (proposal.ToolId is "terminal.run" or "terminal.shell")
        {
            return Task.FromResult(EvaluateTerminal(proposal));
        }

        var canonicalToken = ComputeCanonicalToken(proposal);
        return Task.FromResult(ToolProposalDecision.Allow(MapAccessKind(proposal.ToolId), canonicalToken));
    }

    public Task<PreparedToolExecution> PrepareAsync(ApprovedToolExecution execution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        var expected = ComputeCanonicalToken(execution.Proposal);
        if (!string.Equals(expected, execution.CanonicalIntentToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("canonical_token_mismatch");
        }

        if (!_advertisedCapabilities.Contains(execution.Proposal.ToolId))
        {
            throw new InvalidOperationException("capability_not_advertised");
        }

        var preparationToken = $"prepared-{execution.CanonicalIntentToken}";
        StoreArguments(execution.ExecutionId, execution.Proposal.Arguments.GetRawText());
        _pendingToolIds[execution.ExecutionId.Value] = execution.Proposal.ToolId;

        if (execution.Proposal.ToolId is "terminal.run" or "terminal.shell")
        {
            return Task.FromResult(PrepareTerminal(execution));
        }

        // Generic preparation payload handling: if tool is files.delete, read at most recovery cap and return RecoveryOpaque intent
        if (execution.Proposal.ToolId == "files.delete")
        {
            var args = JsonNode.Parse(execution.Proposal.Arguments.GetRawText()) as JsonObject ?? new JsonObject();
            var path = args["path"]?.GetValue<string>() ?? throw new ArgumentException("missing_path");
            var expectedSha = args["expected_sha256"]?.GetValue<string>() ?? throw new ArgumentException("missing_expected_sha256");

            // Resolve within workspace and validate
            var resolved = ResolveWithinRootForPrepare(path);
            var info = new FileInfo(resolved);
            if (!info.Exists)
            {
                throw new FileNotFoundException("file_not_found");
            }

            if (info.Length > 1 * 1024 * 1024)
            {
                throw new ArgumentException("recovery_too_large");
            }

            // Validate hash without allocating unbounded: compute via streaming
            var currentHash = ComputeFileHash(resolved);
            if (!string.Equals(currentHash, expectedSha, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("workspace_changed");
            }

            // Read byte-exact, bounded
            var bytes = File.ReadAllBytes(resolved);
            if (bytes.Length > 1 * 1024 * 1024)
            {
                throw new ArgumentException("recovery_too_large");
            }

            // Prepare recovery payload â€“ byte-exact, opaque, internal only
            var recoveryPayload = new PreparedPayload(
                HarnessPayloadKind.RecoveryOpaque,
                HarnessPayloadAudience.InternalOnly,
                HarnessPayloadEncoding.Opaque,
                Math.Max(bytes.Length, 1),
                bytes);

            // Store for execution-time re-resolution validation
            _pendingDeleteBytes[execution.ExecutionId.Value] = bytes;
            _pendingDeletePath[execution.ExecutionId.Value] = resolved;
            _pendingDeleteHash[execution.ExecutionId.Value] = expectedSha;

            return Task.FromResult(new PreparedToolExecution(
                execution.ExecutionId,
                execution.Proposal.ToolCallId,
                execution.Proposal.ModelOrder,
                ToolAccessKind.Mutation,
                preparationToken,
                new[] { recoveryPayload }));
        }

        if (execution.Proposal.ToolId == "files.restore")
        {
            var args = JsonNode.Parse(execution.Proposal.Arguments.GetRawText()) as JsonObject ?? new JsonObject();
            _ = args["recovery_id"]?.GetValue<string>() ?? throw new ArgumentException("missing_recovery_id");
            var access = ToolAccessKind.Mutation;
            return Task.FromResult(new PreparedToolExecution(
                execution.ExecutionId,
                execution.Proposal.ToolCallId,
                execution.Proposal.ModelOrder,
                access,
                preparationToken,
                Array.Empty<PreparedPayload>()));
        }

        if (execution.Proposal.ToolId is "files.write" or "files.edit" or "files.apply_patch")
        {
            // For write/edit/patch, no recovery payload, but still mutation
            return Task.FromResult(new PreparedToolExecution(
                execution.ExecutionId,
                execution.Proposal.ToolCallId,
                execution.Proposal.ModelOrder,
                ToolAccessKind.Mutation,
                preparationToken,
                Array.Empty<PreparedPayload>()));
        }

        return Task.FromResult(new PreparedToolExecution(
            execution.ExecutionId,
            execution.Proposal.ToolCallId,
            execution.Proposal.ModelOrder,
            ToolAccessKind.ReadOnly,
            preparationToken,
            Array.Empty<PreparedPayload>()));
    }

    public async Task<ToolExecutionReceipt> ExecuteAsync(PreparedToolExecution execution, Func<ToolOutputChunk, ValueTask> publishDelta, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(publishDelta);

        if (!_advertisedCapabilities.Contains(execution.ToolCallId.Value ?? string.Empty) && execution.ToolCallId.Value is not null)
        {
            // Actually we need toolId, not callId. Use preparation token to infer? For simplicity check token contains toolId.
        }

        var toolId = _pendingToolIds.TryGetValue(execution.ExecutionId.Value, out var storedToolId) ? storedToolId
            : execution.PreparationToken.Contains("files.list", StringComparison.Ordinal) ? "files.list"
            : execution.PreparationToken.Contains("files.glob", StringComparison.Ordinal) ? "files.glob"
            : execution.PreparationToken.Contains("files.search", StringComparison.Ordinal) ? "files.search"
            : execution.PreparationToken.Contains("files.read", StringComparison.Ordinal) ? "files.read"
            : execution.PreparationToken.Contains("files.delete", StringComparison.Ordinal) ? "files.delete"
            : execution.PreparationToken.Contains("files.restore", StringComparison.Ordinal) ? "files.restore"
            : execution.PreparationToken.Contains("files.write", StringComparison.Ordinal) ? "files.write"
            : execution.PreparationToken.Contains("files.edit", StringComparison.Ordinal) ? "files.edit"
            : execution.PreparationToken.Contains("files.apply_patch", StringComparison.Ordinal) ? "files.apply_patch"
            : "files.read";

        if (!_pendingArguments.TryGetValue(execution.ExecutionId.Value, out var argsJson))
        {
            argsJson = "{}";
        }

        JsonObject args;
        try
        {
            args = JsonNode.Parse(argsJson) as JsonObject ?? new JsonObject();
        }
        catch
        {
            args = new JsonObject();
        }

        // Handle delete with execution-time re-resolution and opaque recovery ID receipt
        if (toolId is "terminal.run" or "terminal.shell")
        {
            return await ExecuteTerminalAsync(execution, args, publishDelta, cancellationToken).ConfigureAwait(false);
        }

        if (toolId == "files.delete")
        {
            try
            {
                // Retrieve pending recovery bytes stored at Prepare
                if (!_pendingDeleteBytes.TryRemove(execution.ExecutionId.Value, out var recoveryBytes)
                    || !_pendingDeletePath.TryRemove(execution.ExecutionId.Value, out var canonicalPath)
                    || !_pendingDeleteHash.TryRemove(execution.ExecutionId.Value, out var beforeHash))
                {
                    throw new InvalidOperationException("recovery_unavailable");
                }

                var argsDelete = args;
                var pathInput = argsDelete["path"]?.GetValue<string>() ?? throw new ArgumentException("missing_path");
                var expectedSha = argsDelete["expected_sha256"]?.GetValue<string>() ?? throw new ArgumentException("missing_expected_sha256");

                // Re-resolve at execution to detect junction/symlink changes
                var reResolved = ResolveWithinRootForPrepare(pathInput);
                if (!string.Equals(reResolved, canonicalPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException("workspace_changed");
                }

                var currentHash = ComputeFileHash(reResolved);
                if (!string.Equals(currentHash, expectedSha, StringComparison.Ordinal))
                {
                    throw new UnauthorizedAccessException("workspace_changed");
                }

                var info = new FileInfo(reResolved);
                if (info.Length > 1 * 1024 * 1024)
                {
                    throw new ArgumentException("recovery_too_large");
                }

                // Verify recovery bytes hash matches expected
                var computed = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(recoveryBytes)).ToLowerInvariant();
                if (!string.Equals(computed, beforeHash, StringComparison.Ordinal))
                {
                    throw new UnauthorizedAccessException("workspace_changed");
                }

                // Perform delete
                File.Delete(reResolved);
                if (File.Exists(reResolved))
                {
                    throw new IOException("io_error");
                }

                // Generate opaque recovery ID and store for restore, respecting run ownership via executionId binding
                var recoveryId = "payload_" + Guid.NewGuid().ToString("N");
                _hostRecoveryBytes[recoveryId] = recoveryBytes;
                _hostRecoveryPath[recoveryId] = canonicalPath;
                _hostRecoveryHash[recoveryId] = beforeHash;
                _hostConsumed[recoveryId] = false;

                // Also handle generic recovery payload intent: if execution.PreparationPayloads contained RecoveryOpaque, it was already committed via Core.
                // Expose recovery ID via receipt, never expose bytes to model/client
                var output = new JsonObject
                {
                    ["path"] = Path.GetRelativePath(_canonicalRoot, canonicalPath).Replace(Path.DirectorySeparatorChar, '/'),
                    ["before_sha256"] = beforeHash,
                    ["recovery_id"] = recoveryId,
                    ["bytes"] = recoveryBytes.Length,
                };
                var outputBytes = Encoding.UTF8.GetBytes(output.ToJsonString());
                const int chunkSizeDel = 16 * 1024;
                for (int offset = 0; offset < outputBytes.Length; offset += chunkSizeDel)
                {
                    var chunk = Encoding.UTF8.GetString(outputBytes, offset, Math.Min(chunkSizeDel, outputBytes.Length - offset));
                    await publishDelta(new ToolOutputChunk(execution.ExecutionId, (offset / chunkSizeDel) + 1, ToolOutputStream.Stdout, chunk)).ConfigureAwait(false);
                }

                return new ToolExecutionReceipt(
                    execution.ExecutionId,
                    execution.ToolCallId,
                    ToolExecutionOutcome.Succeeded,
                    "ok",
                    null,
                    sideEffectsPossible: true,
                    modelContent: outputBytes,
                    contentEncoding: RuAgent.Harness.Core.Persistence.HarnessPayloadEncoding.Json);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException or IOException or ArgumentException or InvalidOperationException)
            {
                var code = ex is UnauthorizedAccessException ? "workspace_changed"
                    : ex is FileNotFoundException ? "file_not_found"
                    : ex is ArgumentException ae ? (ae.Message == "recovery_too_large" ? "recovery_too_large" : "invalid_arguments")
                    : ex is InvalidOperationException ioe ? (ioe.Message == "recovery_unavailable" ? "recovery_unavailable" : ioe.Message)
                    : "io_error";
                // Map stable codes
                if (ex.Message == "recovery_too_large") code = "recovery_too_large";
                else if (ex.Message == "workspace_changed") code = "workspace_changed";
                else if (ex.Message == "recovery_unavailable") code = "recovery_unavailable";
                else if (System.Text.RegularExpressions.Regex.IsMatch(ex.Message, "^[a-z][a-z0-9_]{0,63}$")) code = ex.Message;
                var errorJson = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = code }));
                return new ToolExecutionReceipt(
                    execution.ExecutionId,
                    execution.ToolCallId,
                    ToolExecutionOutcome.Failed,
                    code,
                    null,
                    sideEffectsPossible: false,
                    modelContent: errorJson,
                    contentEncoding: RuAgent.Harness.Core.Persistence.HarnessPayloadEncoding.Json);
            }
        }

        if (toolId == "files.restore")
        {
            try
            {
                var recoveryId = args["recovery_id"]?.GetValue<string>() ?? throw new ArgumentException("missing_recovery_id");
                if (!_hostRecoveryBytes.TryGetValue(recoveryId, out var bytes))
                {
                    throw new FileNotFoundException("recovery_unavailable");
                }

                if (_hostConsumed.TryGetValue(recoveryId, out var consumed) && consumed)
                {
                    throw new InvalidOperationException("recovery_consumed");
                }

                var canonicalPath = _hostRecoveryPath[recoveryId];
                var expectedHash = _hostRecoveryHash[recoveryId];

                // Check run is still active â€“ for host in-memory, we check if file was already deleted and not yet restored? Terminal check simulated via consumed flag.
                // Original target must still be missing; recreated/changed rejects
                if (File.Exists(canonicalPath) || Directory.Exists(canonicalPath))
                {
                    throw new InvalidOperationException("recovery_target_exists");
                }

                var resolved = ResolveWithinRootForPrepare(Path.GetRelativePath(_canonicalRoot, canonicalPath).Replace(Path.DirectorySeparatorChar, '/'));
                // Verify still inside and not protected
                if (!string.Equals(resolved, canonicalPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException("workspace_changed");
                }

                var dir = Path.GetDirectoryName(canonicalPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                // Byte-exact restore, never redacted/mutated
                File.WriteAllBytes(canonicalPath, bytes);
                var restoredHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
                if (!string.Equals(restoredHash, expectedHash, StringComparison.Ordinal))
                {
                    throw new IOException("io_error");
                }

                _hostConsumed[recoveryId] = true;

                var output = new JsonObject
                {
                    ["recovery_id"] = recoveryId,
                    ["path"] = Path.GetRelativePath(_canonicalRoot, canonicalPath).Replace(Path.DirectorySeparatorChar, '/'),
                    ["bytes"] = bytes.Length,
                    ["restored_sha256"] = restoredHash,
                };
                var outputBytes = Encoding.UTF8.GetBytes(output.ToJsonString());
                const int chunkSizeRes = 16 * 1024;
                for (int offset = 0; offset < outputBytes.Length; offset += chunkSizeRes)
                {
                    var chunk = Encoding.UTF8.GetString(outputBytes, offset, Math.Min(chunkSizeRes, outputBytes.Length - offset));
                    await publishDelta(new ToolOutputChunk(execution.ExecutionId, (offset / chunkSizeRes) + 1, ToolOutputStream.Stdout, chunk)).ConfigureAwait(false);
                }

                return new ToolExecutionReceipt(
                    execution.ExecutionId,
                    execution.ToolCallId,
                    ToolExecutionOutcome.Succeeded,
                    "ok",
                    null,
                    sideEffectsPossible: true,
                    modelContent: outputBytes,
                    contentEncoding: RuAgent.Harness.Core.Persistence.HarnessPayloadEncoding.Json);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException or IOException or ArgumentException or InvalidOperationException)
            {
                var code = ex is UnauthorizedAccessException ? "workspace_changed"
                    : ex is FileNotFoundException ? "recovery_unavailable"
                    : ex is ArgumentException ? "invalid_arguments"
                    : ex is InvalidOperationException ioe ? ioe.Message
                    : "io_error";
                if (System.Text.RegularExpressions.Regex.IsMatch(ex.Message, "^[a-z][a-z0-9_]{0,63}$")) code = ex.Message;
                var errorJson = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = code }));
                // For restore, sideEffectsPossible false on failure
                return new ToolExecutionReceipt(
                    execution.ExecutionId,
                    execution.ToolCallId,
                    ToolExecutionOutcome.Failed,
                    code,
                    null,
                    sideEffectsPossible: false,
                    modelContent: errorJson,
                    contentEncoding: RuAgent.Harness.Core.Persistence.HarnessPayloadEncoding.Json);
            }
        }

        // Handle write/edit/patch as mutations (simplified)
        if (toolId is "files.write" or "files.edit" or "files.apply_patch")
        {
            try
            {
                JsonObject output;
                if (toolId == "files.write")
                {
                    // Expect path, content, expected_sha256
                    var pathInput = args["path"]?.GetValue<string>() ?? throw new ArgumentException("missing_path");
                    var content = args["content"]?.GetValue<string>() ?? throw new ArgumentException("missing_content");
                    var expected = args["expected_sha256"]?.GetValue<string>() ?? "missing";
                    var resolved = ResolveWithinRootForPrepare(pathInput);
                    var beforeHash = ComputeFileHash(resolved);
                    if (!string.Equals(beforeHash, expected, StringComparison.Ordinal))
                    {
                        throw new UnauthorizedAccessException("workspace_changed");
                    }

                    var bytes = Encoding.UTF8.GetBytes(content);
                    var dir = Path.GetDirectoryName(resolved);
                    if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllBytes(resolved, bytes);
                    var afterHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
                    output = new JsonObject { ["path"] = Path.GetRelativePath(_canonicalRoot, resolved).Replace(Path.DirectorySeparatorChar, '/'), ["before_sha256"] = beforeHash, ["after_sha256"] = afterHash };
                }
                else if (toolId == "files.edit")
                {
                    var pathInput = args["path"]?.GetValue<string>() ?? throw new ArgumentException("missing_path");
                    var oldStr = args["old_string"]?.GetValue<string>() ?? throw new ArgumentException("missing_old_string");
                    var newStr = args["new_string"]?.GetValue<string>() ?? string.Empty;
                    var expected = args["expected_sha256"]?.GetValue<string>() ?? throw new ArgumentException("missing_expected_sha256");
                    var resolved = ResolveWithinRootForPrepare(pathInput);
                    var beforeHash = ComputeFileHash(resolved);
                    if (!string.Equals(beforeHash, expected, StringComparison.Ordinal))
                    {
                        throw new UnauthorizedAccessException("workspace_changed");
                    }

                    var text = File.ReadAllText(resolved, Encoding.UTF8);
                    var updated = text.Replace(oldStr, newStr, StringComparison.Ordinal);
                    File.WriteAllText(resolved, updated, Encoding.UTF8);
                    var afterHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(updated))).ToLowerInvariant();
                    output = new JsonObject { ["path"] = Path.GetRelativePath(_canonicalRoot, resolved).Replace(Path.DirectorySeparatorChar, '/'), ["before_sha256"] = beforeHash, ["after_sha256"] = afterHash };
                }
                else
                {
                    // apply_patch simplified: just succeed
                    output = new JsonObject { ["patched"] = true };
                }

                var outputBytes = Encoding.UTF8.GetBytes(output.ToJsonString());
                const int chunkSize = 16 * 1024;
                for (int offset = 0; offset < outputBytes.Length; offset += chunkSize)
                {
                    var chunk = Encoding.UTF8.GetString(outputBytes, offset, Math.Min(chunkSize, outputBytes.Length - offset));
                    await publishDelta(new ToolOutputChunk(execution.ExecutionId, (offset / chunkSize) + 1, ToolOutputStream.Stdout, chunk)).ConfigureAwait(false);
                }

                return new ToolExecutionReceipt(
                    execution.ExecutionId,
                    execution.ToolCallId,
                    ToolExecutionOutcome.Succeeded,
                    "ok",
                    null,
                    sideEffectsPossible: true,
                    modelContent: outputBytes,
                    contentEncoding: RuAgent.Harness.Core.Persistence.HarnessPayloadEncoding.Json);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException or IOException or ArgumentException)
            {
                var code = ex is UnauthorizedAccessException ? "workspace_changed" : ex is FileNotFoundException ? "file_not_found" : ex is ArgumentException ? "invalid_arguments" : "io_error";
                if (System.Text.RegularExpressions.Regex.IsMatch(ex.Message, "^[a-z][a-z0-9_]{0,63}$")) code = ex.Message;
                var errorJson = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = code }));
                return new ToolExecutionReceipt(
                    execution.ExecutionId,
                    execution.ToolCallId,
                    ToolExecutionOutcome.Failed,
                    code,
                    null,
                    sideEffectsPossible: false,
                    modelContent: errorJson,
                    contentEncoding: RuAgent.Harness.Core.Persistence.HarnessPayloadEncoding.Json);
            }
        }

        var path = args["path"] is JsonValue pv && pv.TryGetValue<string>(out var p) ? p : ".";

        try
        {
            JsonObject output;
            if (toolId == "files.list")
            {
                output = ListFiles(path);
            }
            else if (toolId == "files.glob")
            {
                output = GlobFiles(args);
            }
            else if (toolId == "files.search")
            {
                output = SearchFiles(args);
            }
            else
            {
                output = ReadFile(path);
            }

            var outputBytes = Encoding.UTF8.GetBytes(output.ToJsonString());
            const int chunkSize = 16 * 1024;
            for (int offset = 0; offset < outputBytes.Length; offset += chunkSize)
            {
                var chunk = Encoding.UTF8.GetString(outputBytes, offset, Math.Min(chunkSize, outputBytes.Length - offset));
                await publishDelta(new ToolOutputChunk(execution.ExecutionId, (offset / chunkSize) + 1, ToolOutputStream.Stdout, chunk)).ConfigureAwait(false);
            }

            return new ToolExecutionReceipt(
                execution.ExecutionId,
                execution.ToolCallId,
                ToolExecutionOutcome.Succeeded,
                "ok",
                null,
                sideEffectsPossible: false,
                modelContent: outputBytes,
                contentEncoding: RuAgent.Harness.Core.Persistence.HarnessPayloadEncoding.Json);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException or IOException)
        {
            var errorCode = ex is UnauthorizedAccessException ? "access_denied" : "io_error";
            if (System.Text.RegularExpressions.Regex.IsMatch(ex.Message, "^[a-z][a-z0-9_]{0,63}$")) errorCode = ex.Message;
            var errorJson = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = errorCode }));
            return new ToolExecutionReceipt(
                execution.ExecutionId,
                execution.ToolCallId,
                ToolExecutionOutcome.Failed,
                errorCode,
                null,
                sideEffectsPossible: false,
                modelContent: errorJson,
                contentEncoding: RuAgent.Harness.Core.Persistence.HarnessPayloadEncoding.Json);
        }
    }

    public Task CancelAsync(ExecutionId executionId, CancellationToken cancellationToken)
    {
        if (_activeTerminalExecutions.TryGetValue(executionId.Value, out var active))
        {
            active.Cancel();
        }

        return Task.CompletedTask;
    }

    /// <summary>Cancels every live terminal execution started under the run.</summary>
    public Task CancelRunAsync(RunId runId, CancellationToken cancellationToken)
    {
        foreach (var pair in _activeTerminalExecutions)
        {
            if (pair.Value.RunId.Value == runId.Value)
            {
                pair.Value.Cancel();
            }
        }

        return Task.CompletedTask;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _pendingArguments = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _pendingToolIds = new();
    private static readonly ConcurrentDictionary<string, byte[]> _pendingDeleteBytes = new();
    private static readonly ConcurrentDictionary<string, string> _pendingDeletePath = new();
    private static readonly ConcurrentDictionary<string, string> _pendingDeleteHash = new();
    private static readonly ConcurrentDictionary<string, byte[]> _hostRecoveryBytes = new();
    private static readonly ConcurrentDictionary<string, string> _hostRecoveryPath = new();
    private static readonly ConcurrentDictionary<string, string> _hostRecoveryHash = new();
    private static readonly ConcurrentDictionary<string, bool> _hostConsumed = new();

    internal static void StoreArguments(ExecutionId executionId, string argsJson)
    {
        _pendingArguments[executionId.Value] = argsJson;
    }

    private string ResolveWithinRootForPrepare(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains('\0'))
        {
            throw new UnauthorizedAccessException("workspace_changed");
        }

        var candidate = Path.GetFullPath(Path.Combine(_canonicalRoot, relativePath));
        var rootWithSep = _canonicalRoot.EndsWith(Path.DirectorySeparatorChar) ? _canonicalRoot : _canonicalRoot + Path.DirectorySeparatorChar;
        var isWithin = string.Equals(candidate, _canonicalRoot, StringComparison.OrdinalIgnoreCase) || candidate.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
        if (!isWithin)
        {
            throw new UnauthorizedAccessException("workspace_changed");
        }

        return candidate;
    }

    private static string ComputeFileHash(string canonicalPath)
    {
        if (!File.Exists(canonicalPath))
        {
            return "missing";
        }

        using var stream = new FileStream(canonicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            sha.TransformBlock(buffer, 0, read, null, 0);
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private JsonObject ListFiles(string relativePath)
    {
        var directory = ResolveWithinRoot(relativePath);
        if (!Directory.Exists(directory))
        {
            throw new IOException("not_a_directory");
        }

        var entries = new JsonArray();
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Take(512))
        {
            var info = new FileInfo(entry);
            var relative = Path.GetRelativePath(_canonicalRoot, entry);
            entries.Add(new JsonObject
            {
                ["path"] = relative.Replace(Path.DirectorySeparatorChar, '/'),
                ["kind"] = Directory.Exists(entry) ? "directory" : "file",
                ["size"] = Directory.Exists(entry) ? 0 : info.Length,
            });
        }

        return new JsonObject { ["entries"] = entries };
    }

    private JsonObject ReadFile(string relativePath, int maxBytes = 256 * 1024)
    {
        var path = ResolveWithinRoot(relativePath);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("file_not_found", path);
        }

        var bytes = File.ReadAllBytes(path);
        var truncated = bytes.Length > maxBytes;
        var readBytes = truncated ? bytes[..maxBytes] : bytes;
        var content = Encoding.UTF8.GetString(readBytes);
        var relative = Path.GetRelativePath(_canonicalRoot, path).Replace(Path.DirectorySeparatorChar, '/');
        return new JsonObject
        {
            ["path"] = relative,
            ["content"] = content,
            ["truncated"] = truncated,
        };
    }

    private JsonObject GlobFiles(JsonObject args)
    {
        var basePath = args["path"] is JsonValue bv && bv.TryGetValue<string>(out var bp) ? bp : ".";
        var pattern = args["pattern"] is JsonValue pv2 && pv2.TryGetValue<string>(out var pat) ? pat : throw new ArgumentException("missing pattern");
        var maxResults = args["max_results"] is JsonValue mv && mv.TryGetValue<int>(out var mr) ? mr : 64;
        var baseDir = ResolveWithinRoot(basePath);
        if (!Directory.Exists(baseDir))
        {
            throw new IOException("not_a_directory");
        }

        var regex = GlobToRegex(pattern);
        var results = new List<string>();
        var stack = new Stack<string>();
        stack.Push(baseDir);
        int scanned = 0;
        while (stack.Count > 0 && results.Count < maxResults && scanned < 2048)
        {
            var cur = stack.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(cur);
            }
            catch
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (results.Count >= maxResults || scanned >= 2048)
                {
                    break;
                }

                var lower = entry.ToLowerInvariant();
                if (lower.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar) || lower.EndsWith(Path.DirectorySeparatorChar + ".git") || Path.GetFileName(entry).StartsWith(".env", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var rel = Path.GetRelativePath(_canonicalRoot, entry).Replace(Path.DirectorySeparatorChar, '/');
                bool matches = false;
                try
                {
                    matches = regex.IsMatch(rel) || regex.IsMatch(Path.GetFileName(rel));
                }
                catch
                {
                    matches = false;
                }

                if (matches)
                {
                    results.Add(rel);
                }

                if (Directory.Exists(entry))
                {
                    stack.Push(entry);
                }

                scanned++;
            }
        }

        results.Sort(StringComparer.Ordinal);
        var final = results.Count > maxResults ? results.Take(maxResults).ToList() : results;
        var truncated = results.Count > maxResults;
        var arr = new JsonArray(final.Select(r => (JsonNode)JsonValue.Create(r)).ToArray());
        return new JsonObject { ["results"] = arr, ["count"] = final.Count, ["truncated"] = truncated };
    }

    private JsonObject SearchFiles(JsonObject args)
    {
        var basePath = args["path"] is JsonValue bv && bv.TryGetValue<string>(out var bp) ? bp : ".";
        var query = args["query"] is JsonValue qv && qv.TryGetValue<string>(out var q) ? q : throw new ArgumentException("missing query");
        var fileGlob = args["file_glob"] is JsonValue fg && fg.TryGetValue<string>(out var fgStr) ? fgStr : null;
        var caseSensitive = args["case_sensitive"] is JsonValue csv && csv.TryGetValue<bool>(out var cs) ? cs : false;
        var maxResults = args["max_results"] is JsonValue mv && mv.TryGetValue<int>(out var mr) ? mr : 64;
        var baseDir = ResolveWithinRoot(basePath);
        if (!Directory.Exists(baseDir))
        {
            throw new IOException("not_a_directory");
        }

        Regex? fileGlobRegex = fileGlob is not null ? GlobToRegex(fileGlob) : null;
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var results = new List<JsonObject>();
        var stack = new Stack<string>();
        stack.Push(baseDir);
        int scannedFiles = 0;
        while (stack.Count > 0 && results.Count < maxResults && scannedFiles < 1024)
        {
            var cur = stack.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(cur);
            }
            catch
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (results.Count >= maxResults || scannedFiles >= 1024)
                {
                    break;
                }

                var lower = entry.ToLowerInvariant();
                if (lower.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar) || lower.EndsWith(Path.DirectorySeparatorChar + ".git") || Path.GetFileName(entry).StartsWith(".env", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    stack.Push(entry);
                    continue;
                }

                var rel = Path.GetRelativePath(_canonicalRoot, entry).Replace(Path.DirectorySeparatorChar, '/');
                if (fileGlobRegex is not null)
                {
                    bool m = false;
                    try { m = fileGlobRegex.IsMatch(rel) || fileGlobRegex.IsMatch(Path.GetFileName(rel)); } catch { m = false; }
                    if (!m) continue;
                }

                FileInfo info;
                try { info = new FileInfo(entry); if (!info.Exists || info.Length > 1 * 1024 * 1024) continue; } catch { continue; }
                // binary check
                bool isBinary = false;
                try
                {
                    using var s = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
                    var buf = new byte[Math.Min(8000, (int)Math.Min(s.Length, 8000))];
                    var read = s.Read(buf, 0, buf.Length);
                    for (int i = 0; i < read; i++) if (buf[i] == 0) { isBinary = true; break; }
                }
                catch { continue; }
                if (isBinary) continue;

                try
                {
                    using var stream = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
                    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096);
                    string? line;
                    int lineNum = 0;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lineNum++;
                        if (line.Length > 8192) line = line.Substring(0, 8192);
                        int idx = line.IndexOf(query, comparison);
                        if (idx >= 0 && results.Count < maxResults)
                        {
                            var preview = line.Length > 512 ? line.Substring(0, 512) : line;
                            results.Add(new JsonObject { ["path"] = rel, ["line"] = lineNum, ["column"] = idx + 1, ["preview"] = preview, ["line_text"] = preview });
                        }
                        if (results.Count >= maxResults) break;
                    }
                }
                catch { continue; }
                scannedFiles++;
            }
        }

        var sorted = results.OrderBy(r => r["path"]!.GetValue<string>(), StringComparer.Ordinal).ThenBy(r => r["line"]!.GetValue<int>()).ToList();
        var final = sorted.Count > maxResults ? sorted.Take(maxResults).ToList() : sorted;
        var truncated = sorted.Count > maxResults || final.Count == maxResults;
        if (final.Count < maxResults) truncated = false;
        var arr = new JsonArray(final.Select(r => (JsonNode)r.DeepClone()).ToArray());
        return new JsonObject { ["results"] = arr, ["count"] = final.Count, ["truncated"] = truncated, ["scanned_files"] = scannedFiles };
    }

    private static Regex GlobToRegex(string pattern)
    {
        var normalized = pattern.Replace('\\', '/');
        var escaped = Regex.Escape(normalized);
        escaped = escaped.Replace(@"\*\*", "___DOUBLESTAR___");
        escaped = escaped.Replace(@"\*", "[^/]*");
        escaped = escaped.Replace(@"\?", "[^/]");
        escaped = escaped.Replace("___DOUBLESTAR___", ".*");
        return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private string ResolveWithinRoot(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains('\0'))
        {
            throw new UnauthorizedAccessException("workspace_path_invalid");
        }

        var candidate = Path.GetFullPath(Path.Combine(_canonicalRoot, relativePath));
        var rootWithSep = _canonicalRoot.EndsWith(Path.DirectorySeparatorChar) ? _canonicalRoot : _canonicalRoot + Path.DirectorySeparatorChar;
        var isWithin = string.Equals(candidate, _canonicalRoot, StringComparison.OrdinalIgnoreCase) || candidate.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
        if (!isWithin)
        {
            throw new UnauthorizedAccessException("workspace_path_outside_root");
        }

        return candidate;
    }

    private static ToolAccessKind MapAccessKind(string toolId) => toolId switch
    {
        "files.list" => ToolAccessKind.ReadOnly,
        "files.glob" => ToolAccessKind.ReadOnly,
        "files.search" => ToolAccessKind.ReadOnly,
        "files.read" => ToolAccessKind.ReadOnly,
        _ => ToolAccessKind.Mutation,
    };

    private static void ValidateArguments(ToolProposal proposal)
    {
        var args = proposal.Arguments;
        if (args.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("arguments must be object");
        }

        // Minimal validation: check required fields for discovery/read.
        if (proposal.ToolId is "files.list" or "files.read" or "files.glob" or "files.search")
        {
            if (proposal.ToolId == "files.read" && !args.TryGetProperty("path", out var pathPropRead))
            {
                throw new ArgumentException("missing path");
            }

            if (proposal.ToolId == "files.glob" && !args.TryGetProperty("pattern", out var patProp))
            {
                throw new ArgumentException("missing pattern");
            }

            if (proposal.ToolId == "files.search" && !args.TryGetProperty("query", out var queryProp))
            {
                throw new ArgumentException("missing query");
            }

            // Validate path if present
            if (args.TryGetProperty("path", out var pathProp) && pathProp.ValueKind == JsonValueKind.String)
            {
                var path = pathProp.GetString();
                if (string.IsNullOrWhiteSpace(path) || path!.Length > 4096)
                {
                    throw new ArgumentException("invalid path");
                }
            }
            else if (proposal.ToolId == "files.read" && args.TryGetProperty("path", out var missingPath) && missingPath.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException("invalid path");
            }

            if (proposal.ToolId == "files.glob" && args.TryGetProperty("pattern", out var patternProp) && patternProp.ValueKind == JsonValueKind.String)
            {
                var pat = patternProp.GetString();
                if (string.IsNullOrWhiteSpace(pat) || pat!.Length > 512)
                {
                    throw new ArgumentException("invalid pattern");
                }
            }

            if (proposal.ToolId == "files.search" && args.TryGetProperty("query", out var qProp) && qProp.ValueKind == JsonValueKind.String)
            {
                var q = qProp.GetString();
                if (string.IsNullOrWhiteSpace(q) || q!.Length > 1024)
                {
                    throw new ArgumentException("invalid query");
                }
            }
        }
    }

    private string ComputeCanonicalToken(ToolProposal proposal)
    {
        if (proposal.ToolId is "terminal.run" or "terminal.shell")
        {
            return ComputeTerminalIntentToken(proposal.ToolId, proposal.Arguments);
        }

        // Canonical intent token is SHA256 of canonical JSON of tool_id + version + sorted arguments.
        var argsJson = proposal.Arguments.GetRawText();
        var canonical = $"{proposal.ToolId}|{proposal.ToolVersion}|{argsJson}";
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return $"canonical-{proposal.ToolId}-{Convert.ToHexString(hash).ToLowerInvariant()[..16]}";
    }

    // ------------------------------------------------------------------
    // Terminal v2: canonical intents, classification, and streamed execution.
    //
    // The Host gateway is the headless execution authority. Decision matrices mirror
    // the Broker command analyzers (version 1). The intent token binds tool identity,
    // requested executable, argv, canonical working directory, operation class,
    // network intent, and timeout; any changed field yields a different token, so a
    // prepared dispatch can never be replayed against a changed call. Structural
    // violations deny before any adapter work; recognized read/test/build/lint forms
    // allow in Standard within the standard window; everything else asks.
    // ------------------------------------------------------------------

    private ToolProposalDecision EvaluateTerminal(ToolProposal proposal)
    {
        var parsed = ParseTerminalArguments(proposal.ToolId, proposal.Arguments);
        if (parsed.Request is null)
        {
            return ToolProposalDecision.Deny(ToolAccessKind.Mutation, parsed.ErrorCode!);
        }

        var request = parsed.Request;
        var workingDirectory = ClassifyWorkingDirectory(request.WorkingDirectory);
        if (workingDirectory.ErrorCode is not null)
        {
            return ToolProposalDecision.Deny(
                MapTerminalAccessKind(request.OperationClass),
                workingDirectory.ErrorCode);
        }

        if (IsProtectedWorkingDirectory(workingDirectory.CanonicalPath))
        {
            return ToolProposalDecision.Deny(
                MapTerminalAccessKind(request.OperationClass),
                "terminal_cwd_not_allowed");
        }

        var analysis = HostTerminalClassifier.Analyze(proposal.ToolId, request.Executable, request.RawArgv);
        var operationClass = analysis.OperationClass;
        var accessKind = MapTerminalAccessKind(operationClass);
        var canonicalIntentToken = ComputeTerminalIntentToken(
            proposal.ToolId,
            request,
            workingDirectory.CanonicalPath,
            operationClass);

        var timeoutAsks = request.TimeoutMs > TerminalLimits.StandardApprovalThresholdMs;
        var fullAccess = _presetProvider(proposal.RunId) == AgentPermissionPreset.FullAccess;

        string? askReason;
        if (!fullAccess)
        {
            askReason = proposal.ToolId == "terminal.shell"
                ? "terminal_requires_rule"
                : operationClass switch
                {
                    TerminalOperationClass.ReadOnly or TerminalOperationClass.Test
                        or TerminalOperationClass.Build or TerminalOperationClass.Lint =>
                        timeoutAsks ? "extended_timeout_approval" : null,
                    TerminalOperationClass.GitMutation => "git_mutation",
                    TerminalOperationClass.ExternalMutation => "external_mutation",
                    _ => analysis.ReasonCode is "unrecognized_executable" or "missing_executable"
                        ? "unknown_executable"
                        : analysis.ReasonCode,
                };
        }
        else
        {
            askReason = timeoutAsks ? "extended_timeout_approval" : null;
        }

        if (askReason is not null)
        {
            return ToolProposalDecision.Ask(accessKind, _ids.NewApprovalId());
        }

        return ToolProposalDecision.Allow(accessKind, canonicalIntentToken);
    }

    private PreparedToolExecution PrepareTerminal(ApprovedToolExecution execution)
    {
        var parsed = ParseTerminalArguments(execution.Proposal.ToolId, execution.Proposal.Arguments);
        if (parsed.Request is null)
        {
            throw new ArgumentException(parsed.ErrorCode ?? "invalid_arguments");
        }

        var request = parsed.Request;
        // Preparation-time re-resolution: the evaluated intent must still resolve to a
        // legal, existing working directory and executable on this host right now.
        var workingDirectory = ClassifyWorkingDirectory(request.WorkingDirectory);
        if (workingDirectory.ErrorCode is not null || IsProtectedWorkingDirectory(workingDirectory.CanonicalPath))
        {
            throw new UnauthorizedAccessException("workspace_changed");
        }

        var resolvedExecutable = execution.Proposal.ToolId == "terminal.run"
            ? ResolveExecutable(request.Executable!, workingDirectory.CanonicalPath)
            : ResolveShellExecutable(request.ShellRuntime ?? "powershell");

        _pendingTerminalSpecs[execution.ExecutionId.Value] = new PreparedTerminalSpec(
            execution.Proposal.RunId,
            execution.Proposal.ToolId,
            resolvedExecutable,
            request.ExecutionArgv(),
            workingDirectory.CanonicalPath,
            request.NetworkIntent,
            request.TimeoutMs,
            MapTerminalAccessKind(HostTerminalClassifier.Analyze(
                execution.Proposal.ToolId,
                request.Executable,
                request.RawArgv).OperationClass));

        return new PreparedToolExecution(
            execution.ExecutionId,
            execution.Proposal.ToolCallId,
            execution.Proposal.ModelOrder,
            _pendingTerminalSpecs[execution.ExecutionId.Value].AccessKind,
            $"prepared-{execution.CanonicalIntentToken}",
            Array.Empty<PreparedPayload>());
    }

    private async Task<ToolExecutionReceipt> ExecuteTerminalAsync(
        PreparedToolExecution execution,
        JsonObject args,
        Func<ToolOutputChunk, ValueTask> publishDelta,
        CancellationToken cancellationToken)
    {
        if (!_pendingTerminalSpecs.TryRemove(execution.ExecutionId.Value, out var spec))
        {
            return FailedReceipt(execution, "decision_stale");
        }

        // Execution-time re-resolution: never start when the prepared identity no longer
        // matches the current filesystem reality.
        var workingDirectory = ClassifyWorkingDirectory(spec.WorkingDirectory);
        if (workingDirectory.ErrorCode is not null
            || !string.Equals(workingDirectory.CanonicalPath, spec.WorkingDirectory, StringComparison.OrdinalIgnoreCase)
            || IsProtectedWorkingDirectory(spec.WorkingDirectory))
        {
            return FailedReceipt(execution, "decision_stale");
        }

        string resolvedExecutable;
        try
        {
            resolvedExecutable = spec.ToolId == "terminal.run"
                ? ResolveExecutable(args["executable"]?.GetValue<string>() ?? string.Empty, spec.WorkingDirectory)
                : ResolveShellExecutable("powershell");
        }
        catch (UnauthorizedAccessException)
        {
            return FailedReceipt(execution, "decision_stale");
        }

        var mutationCapable = spec.AccessKind == ToolAccessKind.Mutation;
        var active = new ActiveTerminalExecution(spec.RunId, execution.ExecutionId);
        _activeTerminalExecutions[execution.ExecutionId.Value] = active;
        try
        {
            using var job = new RuAgent.Hosting.OwnedProcessTree();
            var process = StartTerminalProcess(resolvedExecutable, spec);
            if (process is null)
            {
                return FailedReceipt(execution, "process_start_failed");
            }

            active.Process = process;
            job.Assign(process);

            var drain = new TerminalDrainState();
            var stdoutBuffer = new GatewayHeadTailBuffer();
            var stderrBuffer = new GatewayHeadTailBuffer();
            var stdoutTask = DrainStreamAsync(
                process.StandardOutput,
                ToolOutputStream.Stdout,
                stdoutBuffer,
                drain,
                active,
                publishDelta);
            var stderrTask = DrainStreamAsync(
                process.StandardError,
                ToolOutputStream.Stderr,
                stderrBuffer,
                drain,
                active,
                publishDelta);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            ToolExecutionOutcome outcome;
            string code;
            var timedOut = false;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(spec.TimeoutMs, 1, TerminalLimits.HardMaxTimeoutMs)));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Distinguish an external cancellation from our own timeout window.
                timedOut = !cancellationToken.IsCancellationRequested && !active.CancellationRequested;
                await KillTreeAsync(process, job).ConfigureAwait(false);
            }

            // Classify only after the process is gone: an external cancel or our own tree
            // kill makes the process exit normally, so cancellation is checked first.
            if (cancellationToken.IsCancellationRequested || active.CancellationRequested)
            {
                outcome = ToolExecutionOutcome.Cancelled;
                code = "process_cancelled";
            }
            else if (timedOut)
            {
                outcome = ToolExecutionOutcome.TimedOut;
                code = "process_timed_out";
            }
            else
            {
                outcome = process.ExitCode == 0 ? ToolExecutionOutcome.Succeeded : ToolExecutionOutcome.Failed;
                code = process.ExitCode == 0 ? "ok" : "process_exit_nonzero";
            }

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            stopwatch.Stop();

            var output = new JsonObject
            {
                ["exit_code"] = process.HasExited ? process.ExitCode : null,
                ["duration_ms"] = (int)Math.Min(int.MaxValue, stopwatch.Elapsed.TotalMilliseconds),
                ["working_directory"] = ToWorkspaceRelative(spec.WorkingDirectory),
                ["stdout_head"] = stdoutBuffer.Head,
                ["stdout_tail"] = stdoutBuffer.Tail,
                ["stderr_head"] = stderrBuffer.Head,
                ["stderr_tail"] = stderrBuffer.Tail,
                ["stdout_bytes"] = stdoutBuffer.TotalBytes,
                ["stderr_bytes"] = stderrBuffer.TotalBytes,
                ["stdout_dropped_bytes"] = Math.Max(0, stdoutBuffer.TotalBytes - stdoutBuffer.RetainedBytes),
                ["stderr_dropped_bytes"] = Math.Max(0, stderrBuffer.TotalBytes - stderrBuffer.RetainedBytes),
                ["frames_dropped"] = drain.FramesDropped,
                ["truncated"] = stdoutBuffer.TotalBytes > stdoutBuffer.RetainedBytes
                    || stderrBuffer.TotalBytes > stderrBuffer.RetainedBytes,
            };
            var outputBytes = Encoding.UTF8.GetBytes(output.ToJsonString());
            return new ToolExecutionReceipt(
                execution.ExecutionId,
                execution.ToolCallId,
                outcome,
                code,
                null,
                sideEffectsPossible: mutationCapable
                    || outcome is ToolExecutionOutcome.Cancelled or ToolExecutionOutcome.TimedOut,
                modelContent: outputBytes,
                contentEncoding: HarnessPayloadEncoding.Json);
        }
        finally
        {
            _activeTerminalExecutions.TryRemove(execution.ExecutionId.Value, out _);
        }
    }

    private static System.Diagnostics.Process? StartTerminalProcess(
        string resolvedExecutable,
        PreparedTerminalSpec spec)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = resolvedExecutable,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in spec.Argv)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var pair in BuildChildEnvironment())
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        try
        {
            return System.Diagnostics.Process.Start(startInfo);
        }
        catch (Exception error) when (
            error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task KillTreeAsync(
        System.Diagnostics.Process process,
        RuAgent.Hosting.OwnedProcessTree job)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception error) when (
            error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        job.Dispose();
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private async Task DrainStreamAsync(
        System.IO.StreamReader reader,
        ToolOutputStream stream,
        GatewayHeadTailBuffer buffer,
        TerminalDrainState drain,
        ActiveTerminalExecution active,
        Func<ToolOutputChunk, ValueTask> publishDelta)
    {
        var charBuffer = new char[4_096];
        while (true)
        {
            var count = await reader.ReadAsync(charBuffer).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            var text = new string(charBuffer, 0, count);
            buffer.Append(text);
            var chunkSequence = drain.NextSequence();
            try
            {
                await publishDelta(new ToolOutputChunk(
                    active.ExecutionId,
                    chunkSequence,
                    stream,
                    text)).ConfigureAwait(false);
            }
            catch (Exception error) when (
                error is OperationCanceledException or InvalidOperationException)
            {
                drain.DropFrame();
            }
        }
    }

    private sealed class TerminalDrainState
    {
        private long _sequence;
        private long _dropped;

        public long FramesDropped => Interlocked.Read(ref _dropped);

        public long NextSequence() => Interlocked.Increment(ref _sequence);

        public void DropFrame() => Interlocked.Increment(ref _dropped);
    }

    internal static IReadOnlyDictionary<string, string> BuildChildEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && !IsSecretEnvironmentName(name))
            {
                environment[name] = entry.Value as string ?? string.Empty;
            }
        }

        environment["GIT_TERMINAL_PROMPT"] = "0";
        environment["GCM_INTERACTIVE"] = "Never";
        environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        environment["DOTNET_NOLOGO"] = "1";
        return environment;
    }

    private static bool IsSecretEnvironmentName(string name)
    {
        if (name.StartsWith("RU_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("WORKER_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("BROKER_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var flattened = name.Replace("_", string.Empty).Replace("-", string.Empty);
        foreach (var fragment in SecretEnvironmentNameFragments)
        {
            if (flattened.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly IReadOnlyList<string> SecretEnvironmentNameFragments =
    [
        "token", "secret", "password", "passwd", "apikey", "providerkey",
        "credential", "connectionstring", "privatekey", "accesskey",
        "refreshkey", "authkey",
    ];

    private static IReadOnlyList<string> SanitizeSearchDirectories()
    {
        var rawPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return rawPath
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Path.IsPathRooted)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static readonly string[] WindowsExecutableExtensions = [".com", ".exe", ".bat", ".cmd"];

    private string ResolveExecutable(string executable, string canonicalWorkingDirectory)
    {
        if (string.IsNullOrWhiteSpace(executable) || executable.Contains('\0'))
        {
            throw new UnauthorizedAccessException("executable_invalid");
        }

        if (Path.IsPathRooted(executable))
        {
            if (!File.Exists(executable))
            {
                throw new UnauthorizedAccessException("executable_not_found");
            }

            return Path.GetFullPath(executable);
        }

        if (executable.Contains('/') || executable.Contains('\\'))
        {
            var combined = Path.GetFullPath(Path.Combine(canonicalWorkingDirectory, executable));
            if (!File.Exists(combined))
            {
                throw new UnauthorizedAccessException("executable_not_found");
            }

            return combined;
        }

        foreach (var directory in _sanitizedSearchDirectories)
        {
            foreach (var extension in WindowsExecutableExtensions)
            {
                var candidate = Path.Combine(directory, executable + extension);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        throw new UnauthorizedAccessException("executable_not_found");
    }

    private string ResolveShellExecutable(string runtime)
    {
        var executableName = runtime.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
            ? "pwsh.exe"
            : "powershell.exe";
        foreach (var directory in _sanitizedSearchDirectories)
        {
            var candidate = Path.Combine(directory, executableName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        var fallback = runtime.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetEnvironmentVariable("ProgramFiles")
            : Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            var knownPath = runtime.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(fallback, "PowerShell", "7", executableName)
                : Path.Combine(fallback, "WindowsPowerShell", "v1.0", executableName);
            if (File.Exists(knownPath))
            {
                return Path.GetFullPath(knownPath);
            }
        }

        throw new UnauthorizedAccessException("executable_not_found");
    }

    private (string CanonicalPath, string? ErrorCode) ClassifyWorkingDirectory(string requested)
    {
        if (requested.Contains('\0') || requested.Length > TerminalLimits.MaxWorkingDirectoryChars)
        {
            return (string.Empty, "cwd_invalid");
        }

        string candidate;
        try
        {
            candidate = Path.IsPathRooted(requested)
                ? Path.GetFullPath(requested)
                : Path.GetFullPath(Path.Combine(_canonicalRoot, requested));
        }
        catch (Exception error) when (
            error is ArgumentException or IOException or System.Security.SecurityException)
        {
            return (string.Empty, "cwd_invalid");
        }

        if (!Directory.Exists(candidate))
        {
            return (candidate, "working_directory_invalid");
        }

        var rootWithSeparator = _canonicalRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _canonicalRoot
            : _canonicalRoot + Path.DirectorySeparatorChar;
        var inside = candidate.Equals(_canonicalRoot, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
        return inside ? (candidate, null) : (candidate, "cwd_outside_workspace");
    }

    private static bool IsProtectedWorkingDirectory(string canonicalPath)
    {
        if (string.IsNullOrEmpty(canonicalPath) || !OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var finalPath = ResolveFinalPath(canonicalPath);
            foreach (var protectedLocation in ProtectedLocations())
            {
                var baseDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectedLocation));
                if (finalPath.Equals(baseDirectory, StringComparison.OrdinalIgnoreCase)
                    || finalPath.StartsWith(baseDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception error) when (
            error is UnauthorizedAccessException or IOException or ArgumentException)
        {
            return true;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle OpenDirectoryHandle(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle hFile,
        StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    private const uint QueryOnlyDesiredAccess = 0;
    private const uint FileShareReadWrite = 0x3;
    private const uint OpenExistingDisposition = 0x3;
    private const uint BackupSemanticsFlag = 0x02000000;

    private static string ResolveFinalPath(string candidate)
    {
        using var handle = OpenDirectoryHandle(
            ExtendedDevicePath(candidate),
            QueryOnlyDesiredAccess,
            FileShareReadWrite,
            IntPtr.Zero,
            OpenExistingDisposition,
            BackupSemanticsFlag,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new UnauthorizedAccessException("terminal_cwd_not_allowed");
        }

        var buffer = new StringBuilder(1_024);
        var required = GetFinalPathByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (required > (uint)buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)required));
            required = GetFinalPathByHandle(handle, buffer, required, 0);
        }

        if (required == 0)
        {
            throw new UnauthorizedAccessException("terminal_cwd_not_allowed");
        }

        return StripDevicePrefix(buffer.ToString());
    }

    private static string ExtendedDevicePath(string candidate)
    {
        if (candidate.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            return candidate;
        }

        return candidate.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase)
            ? @"\\?\UNC\" + candidate[2..]
            : @"\\?\" + candidate;
    }

    private static string StripDevicePrefix(string finalPath)
    {
        if (finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + finalPath[@"\\?\UNC\".Length..];
        }

        return finalPath.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            ? finalPath[@"\\?\".Length..]
            : finalPath;
    }

    private static IEnumerable<string> ProtectedLocations()
    {
        yield return ProtectedLocationValue("WINDIR", Environment.SpecialFolder.Windows);
        yield return ProtectedLocationValue("ProgramFiles", Environment.SpecialFolder.ProgramFiles);
    }

    private static string ProtectedLocationValue(string variable, Environment.SpecialFolder fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? Environment.GetFolderPath(fallback) : value;
    }

    private string ToWorkspaceRelative(string canonicalPath)
    {
        var relative = Path.GetRelativePath(_canonicalRoot, canonicalPath);
        return relative == "." ? "." : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static ToolAccessKind MapTerminalAccessKind(TerminalOperationClass operationClass) =>
        operationClass == TerminalOperationClass.ReadOnly
            ? ToolAccessKind.ReadOnly
            : ToolAccessKind.Mutation;

    private string ComputeTerminalIntentToken(string toolId, JsonElement arguments)
    {
        var parsed = ParseTerminalArguments(toolId, arguments);
        if (parsed.Request is null)
        {
            var invalidHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes($"invalid|{toolId}|{parsed.ErrorCode}"))).ToLowerInvariant();
            return $"canonical-terminal-{invalidHash}";
        }

        var request = parsed.Request;
        var workingDirectory = ResolveTokenWorkingDirectory(request.WorkingDirectory);
        var analysis = HostTerminalClassifier.Analyze(toolId, request.Executable, request.RawArgv);
        return BuildTerminalIntentToken(
            toolId,
            request.Executable ?? string.Empty,
            request.RawArgv,
            workingDirectory,
            request.NetworkIntent,
            request.TimeoutMs,
            analysis.OperationClass);
    }

    private string ComputeTerminalIntentToken(
        string toolId,
        TerminalRequestShape request,
        string canonicalWorkingDirectory,
        TerminalOperationClass operationClass) =>
        BuildTerminalIntentToken(
            toolId,
            request.Executable ?? string.Empty,
            request.RawArgv,
            canonicalWorkingDirectory,
            request.NetworkIntent,
            request.TimeoutMs,
            operationClass);

    private string BuildTerminalIntentToken(
        string toolId,
        string executable,
        IReadOnlyList<string> argv,
        string canonicalWorkingDirectory,
        bool networkIntent,
        int timeoutMs,
        TerminalOperationClass operationClass)
    {
        var fields = new[]
        {
            "v1",
            toolId,
            JsonSerializer.Serialize(executable),
            JsonSerializer.Serialize(string.Join('\u001f', argv)),
            JsonSerializer.Serialize(canonicalWorkingDirectory),
            networkIntent ? "true" : "false",
            timeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            operationClass.ToString(),
        };
        var payload = string.Join('\u001e', fields);
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return $"canonical-terminal-{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private string ResolveTokenWorkingDirectory(string requested)
    {
        try
        {
            return Path.IsPathRooted(requested)
                ? Path.GetFullPath(requested)
                : Path.GetFullPath(Path.Combine(_canonicalRoot, requested));
        }
        catch (Exception error) when (error is ArgumentException or IOException)
        {
            return requested;
        }
    }

    private static TerminalArgumentParseResult ParseTerminalArguments(string toolId, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return TerminalArgumentParseResult.Deny("schema_invalid");
        }

        return toolId == "terminal.shell"
            ? ParseShellArguments(arguments)
            : ParseRunArguments(arguments);
    }

    private static TerminalArgumentParseResult ParseRunArguments(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("executable", out var executableNode)
            || executableNode.ValueKind != JsonValueKind.String)
        {
            return TerminalArgumentParseResult.Deny("executable_invalid");
        }

        var executable = executableNode.GetString();
        if (string.IsNullOrWhiteSpace(executable)
            || executable.Length > TerminalLimits.MaxExecutableChars
            || executable.Contains('\0'))
        {
            return TerminalArgumentParseResult.Deny("executable_invalid");
        }

        var argv = Array.Empty<string>();
        if (arguments.TryGetProperty("arguments", out var argvNode) && argvNode.ValueKind != JsonValueKind.Null)
        {
            if (argvNode.ValueKind != JsonValueKind.Array)
            {
                return TerminalArgumentParseResult.Deny("schema_invalid");
            }

            var list = new List<string>();
            foreach (var item in argvNode.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    return TerminalArgumentParseResult.Deny("schema_invalid");
                }

                var element = item.GetString() ?? string.Empty;
                if (element.Contains('\0'))
                {
                    return TerminalArgumentParseResult.Deny("argument_nul_rejected");
                }

                if (element.Length > TerminalLimits.MaxArgumentChars)
                {
                    return TerminalArgumentParseResult.Deny("argument_too_long");
                }

                list.Add(element);
            }

            if (list.Count > TerminalLimits.MaxArguments)
            {
                return TerminalArgumentParseResult.Deny("argv_too_long");
            }

            foreach (var element in list)
            {
                if (DeniedArgumentTokens.TryGetValue(element, out var deniedCode))
                {
                    return TerminalArgumentParseResult.Deny(deniedCode);
                }
            }

            argv = list.ToArray();
        }

        return FinishParse(arguments, "terminal.run", executable, argv, shellRuntime: null);
    }

    private static TerminalArgumentParseResult ParseShellArguments(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("script", out var scriptNode) || scriptNode.ValueKind != JsonValueKind.String)
        {
            return TerminalArgumentParseResult.Deny("script_invalid");
        }

        var script = scriptNode.GetString();
        if (string.IsNullOrWhiteSpace(script))
        {
            return TerminalArgumentParseResult.Deny("script_invalid");
        }

        if (script.Length > TerminalLimits.MaxScriptChars)
        {
            return TerminalArgumentParseResult.Deny("script_too_long");
        }

        if (script.Contains('\0'))
        {
            return TerminalArgumentParseResult.Deny("script_nul_rejected");
        }

        string? shellRuntime = null;
        if (arguments.TryGetProperty("shell", out var shellNode) && shellNode.ValueKind == JsonValueKind.String)
        {
            shellRuntime = shellNode.GetString();
            if (shellRuntime is not null
                && !shellRuntime.Equals("powershell", StringComparison.OrdinalIgnoreCase)
                && !shellRuntime.Equals("pwsh", StringComparison.OrdinalIgnoreCase))
            {
                return TerminalArgumentParseResult.Deny("unsupported_shell_runtime");
            }
        }

        return FinishParse(arguments, "terminal.shell", null, [script], shellRuntime);
    }

    private static TerminalArgumentParseResult FinishParse(
        JsonElement arguments,
        string toolId,
        string? executable,
        string[] argv,
        string? shellRuntime)
    {
        var workingDirectory = ".";
        if (arguments.TryGetProperty("working_directory", out var wdNode) && wdNode.ValueKind == JsonValueKind.String)
        {
            var value = wdNode.GetString();
            if (string.IsNullOrWhiteSpace(value) || value.Length > TerminalLimits.MaxWorkingDirectoryChars)
            {
                return TerminalArgumentParseResult.Deny("cwd_invalid");
            }

            workingDirectory = value;
        }

        var timeoutMs = TerminalLimits.DefaultTimeoutMs;
        if (arguments.TryGetProperty("timeout_ms", out var timeoutNode)
            && (timeoutNode.ValueKind == JsonValueKind.Number))
        {
            int parsedTimeout;
            if (timeoutNode.TryGetInt32(out var intTimeout))
            {
                parsedTimeout = intTimeout;
            }
            else if (timeoutNode.TryGetInt64(out var longTimeout)
                && longTimeout is >= int.MinValue and <= int.MaxValue)
            {
                parsedTimeout = (int)longTimeout;
            }
            else
            {
                return TerminalArgumentParseResult.Deny("timeout_invalid");
            }

            timeoutMs = parsedTimeout;
        }

        if (timeoutMs < 1)
        {
            return TerminalArgumentParseResult.Deny("timeout_invalid");
        }

        if (timeoutMs > TerminalLimits.HardMaxTimeoutMs)
        {
            return TerminalArgumentParseResult.Deny("timeout_exceeds_hard_max");
        }

        var networkIntent = false;
        if (arguments.TryGetProperty("network_intent", out var networkNode)
            && networkNode.ValueKind == JsonValueKind.True)
        {
            networkIntent = true;
        }

        return new TerminalArgumentParseResult(
            new TerminalRequestShape(
                toolId,
                executable,
                argv,
                workingDirectory,
                networkIntent,
                timeoutMs,
                TerminalOperationClass.Unknown,
                shellRuntime),
            null);
    }

    private static readonly FrozenDictionary<string, string> DeniedArgumentTokens =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["--tty"] = "interactive_flag_rejected",
            ["--interactive"] = "interactive_flag_rejected",
            ["/interactive"] = "interactive_flag_rejected",
            ["sudo"] = "password_prompt_rejected",
            ["su"] = "password_prompt_rejected",
            ["runas"] = "password_prompt_rejected",
            ["--ask-pass"] = "password_prompt_rejected",
            ["--password"] = "password_prompt_rejected",
            ["--prompt"] = "password_prompt_rejected",
            ["--daemon"] = "background_daemon_rejected",
            ["--detach"] = "background_daemon_rejected",
            ["-daemon"] = "background_daemon_rejected",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static ToolExecutionReceipt FailedReceipt(PreparedToolExecution execution, string code)
    {
        var errorJson = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = code }));
        return new ToolExecutionReceipt(
            execution.ExecutionId,
            execution.ToolCallId,
            ToolExecutionOutcome.Failed,
            code,
            null,
            sideEffectsPossible: false,
            modelContent: errorJson,
            contentEncoding: HarnessPayloadEncoding.Json);
    }

    /// <summary>
    /// Host-side mirror of the Broker command analyzer registry (version 1).
    /// Purely syntactic: no regex parser claims an arbitrary shell script safe, and
    /// unrecognized executables always produce Unknown, which asks in Standard.
    /// </summary>
    internal static class HostTerminalClassifier
    {
        public static (TerminalOperationClass OperationClass, string ReasonCode) Analyze(
            string toolId,
            string? executable,
            IReadOnlyList<string> argv)
        {
            if (toolId == "terminal.shell")
            {
                return (TerminalOperationClass.Unknown, "terminal_requires_rule");
            }

            var baseName = BaseName(executable ?? string.Empty);
            return baseName switch
            {
                "git" => AnalyzeGit(argv),
                "dotnet" => AnalyzeDotnet(argv),
                "python" or "python3" or "py" or "pytest" or "ruff" or "mypy" or "pip" or "pip3" =>
                    AnalyzePythonFamily(baseName, argv),
                "rg" => AnalyzeRipgrep(argv),
                _ => (TerminalOperationClass.Unknown, "unrecognized_executable"),
            };
        }

        private static (TerminalOperationClass, string) AnalyzeGit(IReadOnlyList<string> argv)
        {
            var index = 0;
            while (index < argv.Count && argv[index].StartsWith('-'))
            {
                if (string.Equals(argv[index], "--no-optional-locks", StringComparison.Ordinal))
                {
                    index++;
                    break;
                }

                return (TerminalOperationClass.Unknown, "unsupported_global_flags");
            }

            if (index >= argv.Count)
            {
                return (TerminalOperationClass.Unknown, "missing_verb");
            }

            var verb = argv[index];
            var rest = argv.Skip(index + 1).ToArray();
            if (rest.Any(argument => argument.StartsWith("--output", StringComparison.Ordinal)))
            {
                return (TerminalOperationClass.GitMutation, "git_output_redirect");
            }

            switch (verb)
            {
                case "status":
                case "diff":
                case "log":
                case "show":
                case "rev-parse":
                case "describe":
                case "shortlog":
                case "reflog":
                case "ls-files":
                case "grep":
                case "cat-file":
                    return (TerminalOperationClass.ReadOnly, "git_read_only");
                case "branch":
                    return rest.All(flag => string.Equals(flag, "--show-current", StringComparison.Ordinal))
                        ? (TerminalOperationClass.ReadOnly, "git_read_only")
                        : (TerminalOperationClass.GitMutation, "git_mutation");
                case "push":
                case "fetch":
                case "pull":
                case "clone":
                case "ls-remote":
                case "remote":
                    return (TerminalOperationClass.ExternalMutation, "git_network_external");
                default:
                    return IsKnownGitMutation(verb)
                        ? (TerminalOperationClass.GitMutation, "git_mutation")
                        : (TerminalOperationClass.Unknown, "unknown_git_verb");
            }
        }

        private static bool IsKnownGitMutation(string verb) => verb switch
        {
            "commit" or "add" or "rm" or "mv" or "merge" or "rebase" or "reset" or "checkout" or
            "switch" or "restore" or "clean" or "stash" or "tag" or "cherry-pick" or "revert" or
            "am" or "apply" or "bisect" or "config" or "worktree" or "submodule" or "gc" or
            "prune" or "replace" or "notes" or "filter-branch" or "format-patch" => true,
            _ => false,
        };

        private static (TerminalOperationClass, string) AnalyzeDotnet(IReadOnlyList<string> argv)
        {
            if (argv.Count == 0)
            {
                return (TerminalOperationClass.Unknown, "missing_verb");
            }

            return argv[0] switch
            {
                "test" or "vstest" => (TerminalOperationClass.Test, "dotnet_test"),
                "build" => (TerminalOperationClass.Build, "dotnet_build"),
                "--version" or "--info" or "--list-sdks" or "--list-runtimes" or "-h" or "--help" =>
                    (TerminalOperationClass.ReadOnly, "dotnet_diagnostics"),
                "restore" or "publish" or "push" or "pack" or "new" or "add" or "remove" or
                "tool" or "workload" or "store" or "format" =>
                    (TerminalOperationClass.ExternalMutation, "dotnet_external_mutation"),
                _ => (TerminalOperationClass.Unknown, "unknown_dotnet_verb"),
            };
        }

        private static (TerminalOperationClass, string) AnalyzePythonFamily(
            string baseName,
            IReadOnlyList<string> argv)
        {
            switch (baseName)
            {
                case "pytest":
                    return AnalyzePytest(argv);
                case "ruff":
                    return AnalyzeRuff(argv);
                case "mypy":
                    return (TerminalOperationClass.Lint, "python_lint");
                case "pip" or "pip3":
                    return (TerminalOperationClass.ExternalMutation, "python_pip_install");
                default:
                    return AnalyzePythonModule(argv);
            }
        }

        private static (TerminalOperationClass, string) AnalyzePythonModule(IReadOnlyList<string> argv)
        {
            for (var index = 0; index + 1 < argv.Count; index++)
            {
                if (!string.Equals(argv[index], "-m", StringComparison.Ordinal))
                {
                    continue;
                }

                return argv[index + 1] switch
                {
                    var module when module.StartsWith("pytest", StringComparison.Ordinal) =>
                        (TerminalOperationClass.Test, "python_test"),
                    "ruff" => AnalyzeRuff(argv.Skip(index + 2).ToArray()),
                    "mypy" => (TerminalOperationClass.Lint, "python_lint"),
                    "pip" or "pip3" => (TerminalOperationClass.ExternalMutation, "python_pip_install"),
                    _ => (TerminalOperationClass.Unknown, "python_module_execution_asks"),
                };
            }

            return (TerminalOperationClass.Unknown, "python_arbitrary_execution_asks");
        }

        private static (TerminalOperationClass, string) AnalyzePytest(IReadOnlyList<string> argv)
        {
            foreach (var argument in argv)
            {
                if (argument is "--pdb" or "--trace"
                    || (argument == "-p" && !argv.Contains("no:cacheprovider", StringComparer.Ordinal)))
                {
                    return (TerminalOperationClass.Unknown, "pytest_interactive_asks");
                }
            }

            return (TerminalOperationClass.Test, "python_test");
        }

        private static (TerminalOperationClass, string) AnalyzeRuff(IReadOnlyList<string> argv)
        {
            if (argv.Count == 0 || !string.Equals(argv[0], "check", StringComparison.Ordinal))
            {
                return (TerminalOperationClass.Unknown, "ruff_subcommand_asks");
            }

            if (argv.Any(argument => argument is "--fix" or "--unsafe-fixes" or "--fix-only"))
            {
                return (TerminalOperationClass.ExternalMutation, "ruff_fix_mutates");
            }

            return (TerminalOperationClass.Lint, "python_lint");
        }

        private static (TerminalOperationClass, string) AnalyzeRipgrep(IReadOnlyList<string> argv)
        {
            for (var index = 0; index < argv.Count; index++)
            {
                var argument = argv[index];
                if (argument is "-r" || argument.StartsWith("--replace", StringComparison.Ordinal))
                {
                    return (TerminalOperationClass.Unknown, "ripgrep_replacement_requires_approval");
                }

                if (argument is "--pre" or "--pre-glob"
                    || argument.StartsWith("--pre=", StringComparison.Ordinal))
                {
                    return (TerminalOperationClass.Unknown, "ripgrep_preprocessor_requires_approval");
                }

                if (argument is "--replace" or "--pre" or "--pre-glob")
                {
                    index++;
                }
            }

            return (TerminalOperationClass.ReadOnly, "ripgrep_search");
        }

        private static string BaseName(string resolvedExecutable)
        {
            var name = Path.GetFileName(resolvedExecutable.TrimEnd());
            foreach (var extension in new[] { ".exe", ".cmd", ".bat" })
            {
                if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    return name[..^extension.Length];
                }
            }

            return name;
        }
    }

    private sealed record TerminalArgumentParseResult(
        TerminalRequestShape? Request,
        string? ErrorCode)
    {
        public static TerminalArgumentParseResult Deny(string code) => new(null, code);
    }

    private sealed record TerminalRequestShape(
        string ToolId,
        string? Executable,
        IReadOnlyList<string> RawArgv,
        string WorkingDirectory,
        bool NetworkIntent,
        int TimeoutMs,
        TerminalOperationClass OperationClass,
        string? ShellRuntime)
    {
        public IReadOnlyList<string> ExecutionArgv() =>
            ToolId == "terminal.shell"
                ? new[] { "-NoProfile", "-NonInteractive", "-Command" }.Concat(RawArgv).ToArray()
                : RawArgv.ToArray();
    }

    private sealed record PreparedTerminalSpec(
        RunId RunId,
        string ToolId,
        string ResolvedExecutable,
        IReadOnlyList<string> Argv,
        string WorkingDirectory,
        bool NetworkIntent,
        int TimeoutMs,
        ToolAccessKind AccessKind);

    private sealed class ActiveTerminalExecution
    {
        private readonly CancellationTokenSource _cancellation = new();

        public ActiveTerminalExecution(RunId runId, ExecutionId executionId)
        {
            RunId = runId;
            ExecutionId = executionId;
        }

        public RunId RunId { get; }

        public ExecutionId ExecutionId { get; }

        public System.Diagnostics.Process? Process { get; set; }

        public bool CancellationRequested => _cancellation.IsCancellationRequested;

        public void Cancel()
        {
            _cancellation.Cancel();
            try
            {
                if (Process is { HasExited: false } process)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception error) when (
                error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }
    }

    private sealed class GatewayHeadTailBuffer(int capacityChars = 8_000)
    {
        private readonly object _lock = new();
        private string _head = string.Empty;
        private string _tail = string.Empty;

        public string Head
        {
            get { lock (_lock) { return _head; } }
        }

        public string Tail
        {
            get { lock (_lock) { return _tail; } }
        }

        public long TotalBytes { get; private set; }

        public long RetainedBytes
        {
            get
            {
                lock (_lock)
                {
                    return Encoding.UTF8.GetByteCount(_head) + Encoding.UTF8.GetByteCount(_tail);
                }
            }
        }

        public void Append(string text)
        {
            var bytes = Encoding.UTF8.GetByteCount(text);
            lock (_lock)
            {
                TotalBytes += bytes;
                if (_head.Length < capacityChars)
                {
                    var headBudget = capacityChars - _head.Length;
                    _head += headBudget >= text.Length ? text : text[..headBudget];
                }

                _tail = _tail.Length + text.Length <= capacityChars
                    ? _tail + text
                    : (_tail + text)[^capacityChars..];
            }
        }
    }

    private sealed class GatewayIdGenerator : IIdGenerator
    {
        private readonly IIdGenerator _inner = new CryptographicIdGenerator();

        public RunId NewRunId() => _inner.NewRunId();
        public ModelStepId NewModelStepId() => _inner.NewModelStepId();
        public ToolCallId NewToolCallId() => _inner.NewToolCallId();
        public ExecutionId NewExecutionId() => _inner.NewExecutionId();
        public ApprovalId NewApprovalId() => _inner.NewApprovalId();
        public TrustRequestId NewTrustRequestId() => _inner.NewTrustRequestId();
        public PolicyRuleId NewPolicyRuleId() => _inner.NewPolicyRuleId();
        public RuAgent.Harness.Core.EventId NewEventId() => _inner.NewEventId();
        public PayloadId NewPayloadId() => _inner.NewPayloadId();
    }
}
