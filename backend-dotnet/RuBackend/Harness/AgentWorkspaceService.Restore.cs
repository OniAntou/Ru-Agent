using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Workspace;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using static RuBackend.Harness.AgentV1Shared;

namespace RuBackend.Harness;

public sealed partial class AgentWorkspaceService
{
    private const long MaxRestoreSizeBytes = 16 * 1024 * 1024; // 16MB cap per restore
    private static readonly string MissingFileSha256 = new('0', 64);
    private static readonly TimeSpan MinRestoreInterval = TimeSpan.FromSeconds(1); // Rate limit: max 1 restore/sec per workspace
    private static readonly ConcurrentDictionary<string, DateTimeOffset> _lastRestoreTime = new(StringComparer.Ordinal);

    private static bool TryReserveRestoreSlot(string workspaceId, DateTimeOffset now)
    {
        while (true)
        {
            if (!_lastRestoreTime.TryGetValue(workspaceId, out var current))
            {
                if (_lastRestoreTime.TryAdd(workspaceId, now))
                {
                    return true;
                }

                continue;
            }

            if (now - current < MinRestoreInterval)
            {
                return false;
            }

            if (_lastRestoreTime.TryUpdate(workspaceId, now, current))
            {
                return true;
            }
        }
    }

    public async Task<AgentServiceResult> RestoreWorkspaceChangeAsync(
        string workspaceIdRaw,
        string changeId,
        WorkspaceChangeRestoreRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsWorkspaceId(workspaceIdRaw)
            || string.IsNullOrWhiteSpace(changeId)
            || changeId.Length > 256)
        {
            return new AgentServiceResult(400, new AgentErrorResponse("invalid_workspace_change"));
        }

        if (request is null
            || request.ExpectedCurrentSha256 is null
            || request.ExpectedCurrentSha256.Length != 64
            || !IsLowerHex(request.ExpectedCurrentSha256))
        {
            return new AgentServiceResult(400, new AgentErrorResponse("invalid_expected_current_sha256"));
        }

        // Rate limiting per workspace. The slot is reserved atomically before any
        // work starts, so two concurrent restores cannot both pass a stale check.
        var now = _time.UtcNow;
        if (!TryReserveRestoreSlot(workspaceIdRaw, now))
        {
            return new AgentServiceResult(429, new AgentErrorResponse("workspace_restore_rate_limited"));
        }

        try
        {
            var workspaceId = new WorkspaceId(workspaceIdRaw);
            var stored = await _workspaceStore.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
            if (stored is null)
            {
                return new AgentServiceResult(404, new AgentErrorResponse("workspace_not_found"));
            }

            var envelope = await _workspaceCoordinatorStore.GetChangeEnvelopeAsync(workspaceId, changeId, cancellationToken).ConfigureAwait(false);
            if (envelope is null)
            {
                return new AgentServiceResult(404, new AgentErrorResponse("workspace_change_not_found"));
            }

            if (envelope.Change.Status == "restored")
            {
                return new AgentServiceResult(200, new WorkspaceChangeRestoreResponse(changeId, "restored", envelope.Change.BeforeSha256 ?? string.Empty, changeId));
            }

            var root = await _trustService.DecryptCanonicalRootAsync(stored, cancellationToken).ConfigureAwait(false);
            var target = ResolveWorkspaceFile(root, envelope.Change.NormalizedPath);
            if (ContainsReparsePointBoundary(root, target))
            {
                throw new UnauthorizedAccessException("workspace_reparse_boundary");
            }

            var targetExists = File.Exists(target);
            var currentSha = targetExists
                ? await ComputeFileSha256Async(target, cancellationToken).ConfigureAwait(false)
                : MissingFileSha256;
            if (!RestoreTargetMatches(currentSha, request.ExpectedCurrentSha256))
            {
                return new AgentServiceResult(409, new AgentErrorResponse("workspace_restore_stale"));
            }

            var plaintext = await _workspaceJournalProtector.DecryptAsync(workspaceId, changeId, envelope.Change.NormalizedPath, envelope.Journal, cancellationToken).ConfigureAwait(false);
            if (!TryReadRestoreContent(plaintext.Span, out var restoredBytes))
            {
                return new AgentServiceResult(409, new AgentErrorResponse("workspace_restore_unavailable"));
            }

            // Cap restore size
            if (restoredBytes.Length > MaxRestoreSizeBytes)
            {
                return new AgentServiceResult(413, new AgentErrorResponse("workspace_restore_too_large"));
            }

            var restoredSha = Convert.ToHexString(SHA256.HashData(restoredBytes)).ToLowerInvariant();
            var restoreChangeId = $"chg_restore_{Guid.NewGuid():N}";
            var restorePayload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                restore_of = changeId,
                restored_sha256 = restoredSha,
                content_base64 = Convert.ToBase64String(restoredBytes),
            });
            var encrypted = await _workspaceJournalProtector.EncryptAsync(workspaceId, restoreChangeId, envelope.Change.NormalizedPath, restorePayload, cancellationToken).ConfigureAwait(false);

            if (ContainsReparsePointBoundary(root, target))
            {
                throw new UnauthorizedAccessException("workspace_reparse_boundary");
            }

            // Missing targets use an atomic no-overwrite create. Existing files are
            // checked and replaced through one handle that denies concurrent writes
            // and rename-over, preserving the bytes if the write itself fails.
            if (!await TryApplyRestoreAsync(target, restoredBytes, request.ExpectedCurrentSha256, cancellationToken)
                    .ConfigureAwait(false))
            {
                return new AgentServiceResult(409, new AgentErrorResponse("workspace_restore_stale"));
            }

            // Verify the result after either the no-overwrite create or the
            // handle-bound existing-file update.
            var postRestoreSha = File.Exists(target)
                ? await ComputeFileSha256Async(target, cancellationToken).ConfigureAwait(false)
                : MissingFileSha256;
            if (!string.Equals(postRestoreSha, restoredSha, StringComparison.Ordinal))
            {
                return new AgentServiceResult(409, new AgentErrorResponse("workspace_restore_verification_failed"));
            }

            await _workspaceCoordinatorStore.AppendChangeAsync(
                workspaceId,
                runId: null,
                restoreChangeId,
                envelope.Change.NormalizedPath,
                targetExists ? currentSha : MissingFileSha256,
                restoredSha,
                encrypted,
                "restored",
                _time.UtcNow,
                cancellationToken).ConfigureAwait(false);
            await _workspaceCoordinatorStore.MarkChangeRestoredAsync(workspaceId, changeId, cancellationToken).ConfigureAwait(false);
            return new AgentServiceResult(200, new WorkspaceChangeRestoreResponse(changeId, "restored", restoredSha, restoreChangeId));
        }
        catch (HarnessStoreException exception) when (exception.Code is "workspace_root_decrypt_failed" or "workspace_identity_changed" or "workspace_root_key_unavailable")
        {
            return new AgentServiceResult(409, new AgentErrorResponse("workspace_unavailable"));
        }
        catch (IOException)
        {
            return new AgentServiceResult(409, new AgentErrorResponse("workspace_restore_io_failed"));
        }
        catch (UnauthorizedAccessException)
        {
            return new AgentServiceResult(409, new AgentErrorResponse("workspace_restore_io_failed"));
        }
    }

    private static async Task<bool> TryApplyRestoreAsync(
        string target,
        byte[] restoredBytes,
        string expectedSha,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(target))
            ?? throw new IOException("workspace_restore_parent_missing");
        Directory.CreateDirectory(parent);

        if (string.Equals(expectedSha, MissingFileSha256, StringComparison.Ordinal))
        {
            using var parentLease = DirectoryHandleLease.Open(parent, requireCanonicalPath: true);
            if (File.Exists(target) || Directory.Exists(target))
            {
                return false;
            }

            var temporary = target + ".ru-restore-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllBytesAsync(temporary, restoredBytes, cancellationToken).ConfigureAwait(false);
                try
                {
                    File.Move(temporary, target, overwrite: false);
                }
                catch (IOException) when (File.Exists(target) || Directory.Exists(target))
                {
                    return false;
                }

                return true;
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch (IOException)
                {
                    // Best effort cleanup; the no-overwrite create has not touched the target.
                }
            }
        }

        using var existingParentLease = DirectoryHandleLease.Open(parent, requireCanonicalPath: true);
        FileStream held;
        try
        {
            held = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.Read,
                bufferSize: 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }

        await using (held.ConfigureAwait(false))
        {
            if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0 || held.Length > MaxRestoreSizeBytes)
            {
                return false;
            }

            var currentSha = await ComputeStreamSha256Async(held, cancellationToken).ConfigureAwait(false);
            if (!RestoreTargetMatches(currentSha, expectedSha))
            {
                return false;
            }

            var originalBytes = new byte[checked((int)held.Length)];
            held.Position = 0;
            await held.ReadExactlyAsync(originalBytes, cancellationToken).ConfigureAwait(false);
            try
            {
                held.Position = 0;
                await held.WriteAsync(restoredBytes, cancellationToken).ConfigureAwait(false);
                held.SetLength(restoredBytes.Length);
                await held.FlushAsync(cancellationToken).ConfigureAwait(false);
                held.Flush(flushToDisk: true);
                var writtenSha = await ComputeStreamSha256Async(held, cancellationToken).ConfigureAwait(false);
                var expectedWrittenSha = Convert.ToHexString(SHA256.HashData(restoredBytes)).ToLowerInvariant();
                if (!string.Equals(writtenSha, expectedWrittenSha, StringComparison.Ordinal))
                {
                    throw new IOException("workspace_restore_verification_failed");
                }

                return true;
            }
            catch
            {
                held.Position = 0;
                await held.WriteAsync(originalBytes, CancellationToken.None).ConfigureAwait(false);
                held.SetLength(originalBytes.Length);
                held.Flush(flushToDisk: true);
                throw;
            }
        }
    }

    /// <summary>
    /// The restore target must still hold exactly the bytes the client saw. The
    /// early comparison avoids journal work for stale requests; TryApplyRestoreAsync
    /// repeats it while holding a write-excluding handle before changing an existing file.
    /// </summary>
    internal static bool RestoreTargetMatches(string currentSha, string expectedSha) =>
        string.Equals(currentSha, expectedSha, StringComparison.Ordinal);

    private static async Task<string> ComputeFileSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 128 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<string> ComputeStreamSha256Async(Stream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ResolveWorkspaceFile(string root, string relativePath) =>
        AgentV1Shared.ResolveWorkspaceFile(root, relativePath);

    private static bool ContainsReparsePointBoundary(string root, string target) =>
        AgentV1Shared.ContainsReparsePointBoundary(root, target);

    private static bool TryReadRestoreContent(ReadOnlySpan<byte> plaintext, out byte[] content) =>
        AgentV1Shared.TryReadRestoreContent(plaintext, out content);
}
