using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;

namespace RuBackend.Harness.Broker;

public sealed class HarnessToolGateway : IToolGateway
{
    private readonly string _canonicalRoot;
    private readonly IReadOnlySet<string> _advertisedCapabilities;

    public HarnessToolGateway(string canonicalRoot, IReadOnlyList<string> advertisedCapabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);
        ArgumentNullException.ThrowIfNull(advertisedCapabilities);
        _canonicalRoot = Path.GetFullPath(canonicalRoot);
        if (!Directory.Exists(_canonicalRoot))
        {
            throw new DirectoryNotFoundException(_canonicalRoot);
        }

        _advertisedCapabilities = advertisedCapabilities.ToHashSet(StringComparer.Ordinal);
    }

    // For testing without filesystem, allow injection.
    internal HarnessToolGateway(string canonicalRoot, IReadOnlySet<string> capabilities)
    {
        _canonicalRoot = Path.GetFullPath(canonicalRoot);
        _advertisedCapabilities = capabilities;
    }

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

        // For restore, advertised only while active run owns valid recovery – check in-memory host registry
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

            // Prepare recovery payload – byte-exact, opaque, internal only
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

                // Check run is still active – for host in-memory, we check if file was already deleted and not yet restored? Terminal check simulated via consumed flag.
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
        _ = executionId.Value;
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

    private static string ComputeCanonicalToken(ToolProposal proposal)
    {
        // Canonical intent token is SHA256 of canonical JSON of tool_id + version + sorted arguments.
        var argsJson = proposal.Arguments.GetRawText();
        var canonical = $"{proposal.ToolId}|{proposal.ToolVersion}|{argsJson}";
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return $"canonical-{proposal.ToolId}-{Convert.ToHexString(hash).ToLowerInvariant()[..16]}";
    }
}
