using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Approval;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Reconciliation;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Policy;
using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Harness.Persistence.Workspace;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using RuBackend.Business.Sessions;
using RuBackend.Business.Settings;
using RuBackend.Harness.Policy;
using RuBackend.Harness.Workspace;
using RuBackend.Providers;
using RuAgent.Runtime.Adapters.Models;
using static RuBackend.Harness.AgentV1Shared;

namespace RuBackend.Harness;

public sealed class AgentWorkspaceService
{
    private readonly HarnessSqliteOptions _options;
    private readonly SqliteWorkspaceStore _workspaceStore;
    private readonly WorkspaceTrustService _trustService;
    private readonly ITimeProvider _time;
    private readonly WorkspaceCoordinatorStore _workspaceCoordinatorStore;
    private readonly WorkspaceJournalProtector _workspaceJournalProtector;

    public AgentWorkspaceService(HarnessSqliteOptions options, SqliteWorkspaceStore workspaceStore,
        WorkspaceTrustService trustService, ITimeProvider time,
        WorkspaceCoordinatorStore workspaceCoordinatorStore, WorkspaceJournalProtector workspaceJournalProtector)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _workspaceStore = workspaceStore ?? throw new ArgumentNullException(nameof(workspaceStore));
        _trustService = trustService ?? throw new ArgumentNullException(nameof(trustService));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _workspaceCoordinatorStore = workspaceCoordinatorStore ?? throw new ArgumentNullException(nameof(workspaceCoordinatorStore));
        _workspaceJournalProtector = workspaceJournalProtector ?? throw new ArgumentNullException(nameof(workspaceJournalProtector));
    }

    public async Task<AgentServiceResult> GetWorkspaceConcurrencyAsync(string workspaceIdRaw, CancellationToken cancellationToken)
    {
        if (!IsWorkspaceId(workspaceIdRaw))
        {
            return new AgentServiceResult(400, new AgentErrorResponse("invalid_workspace_id"));
        }

        try
        {
            var workspaceId = new WorkspaceId(workspaceIdRaw);
            var coordinator = await ReadWorkspaceCoordinatorAsync(workspaceId, cancellationToken).ConfigureAwait(false);
            var activeClaims = await _workspaceCoordinatorStore.CountActiveClaimsAsync(workspaceId, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            return new AgentServiceResult(
                200,
                new WorkspaceConcurrencyResponse(
                    workspaceIdRaw,
                    new WorkspaceConcurrencyPolicy(
                        coordinator.Mode,
                        coordinator.MaxConcurrentRuns,
                        coordinator.LeaseTtlSeconds,
                        coordinator.MergeTextConflicts,
                        coordinator.ExternalWriterDetection),
                    activeClaims,
                    coordinator.UpdatedAtUtc.ToString("O")));
        }
        catch (HarnessStoreException exception) when (exception.Code == "workspace_not_found")
        {
            return new AgentServiceResult(404, new AgentErrorResponse(exception.Code));
        }
    }

    /// <summary>
    /// Reads the coordinator policy for a GET without materializing a row: a read
    /// request must not write, so a workspace with no coordinator yet answers with
    /// the same defaults the write path would have created.
    /// </summary>
    private async Task<WorkspaceCoordinatorRecord> ReadWorkspaceCoordinatorAsync(
        WorkspaceId workspaceId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = """
                SELECT mode, max_concurrent_runs, lease_ttl_seconds,
                       merge_text_conflicts, external_writer_detection, commit_sequence,
                       policy_json, version, created_at_utc, updated_at_utc
                FROM workspace_coordinators
                WHERE workspace_id = $workspace_id
                """;
            read.Parameters.AddWithValue("$workspace_id", workspaceId.Value);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new WorkspaceCoordinatorRecord(
                    workspaceId.Value,
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3) == 1,
                    reader.GetInt32(4) == 1,
                    reader.GetInt64(5),
                    reader.GetString(6),
                    reader.GetInt64(7),
                    DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
            }
        }

        await using (var workspace = connection.CreateCommand())
        {
            workspace.CommandText = "SELECT 1 FROM agent_workspaces WHERE workspace_id = $workspace_id";
            workspace.Parameters.AddWithValue("$workspace_id", workspaceId.Value);
            if (await workspace.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            {
                throw new HarnessStoreException("workspace_not_found");
            }
        }

        var now = _time.UtcNow;
        return new WorkspaceCoordinatorRecord(
            workspaceId.Value,
            "shared",
            32,
            30,
            true,
            true,
            0,
            "{}",
            1,
            now,
            now);
    }

    public async Task<AgentServiceResult> UpdateWorkspaceConcurrencyAsync(
        string workspaceIdRaw,
        WorkspaceConcurrencyUpdateRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsWorkspaceId(workspaceIdRaw) || request is null)
        {
            return new AgentServiceResult(400, new AgentErrorResponse("invalid_workspace_concurrency_request"));
        }

        try
        {
            var workspaceId = new WorkspaceId(workspaceIdRaw);
            var current = await _workspaceCoordinatorStore.GetOrCreateAsync(workspaceId, cancellationToken).ConfigureAwait(false);
            var updated = await _workspaceCoordinatorStore.UpdatePolicyAsync(
                workspaceId,
                current.Version,
                request.Mode,
                request.MaxConcurrentRuns,
                request.LeaseTtlSeconds,
                request.MergeTextConflicts,
                request.ExternalWriterDetection,
                cancellationToken).ConfigureAwait(false);
            var activeClaims = await _workspaceCoordinatorStore.CountActiveClaimsAsync(workspaceId, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            return new AgentServiceResult(
                200,
                new WorkspaceConcurrencyResponse(
                    workspaceIdRaw,
                    new WorkspaceConcurrencyPolicy(
                        updated.Mode,
                        updated.MaxConcurrentRuns,
                        updated.LeaseTtlSeconds,
                        updated.MergeTextConflicts,
                        updated.ExternalWriterDetection),
                    activeClaims,
                    updated.UpdatedAtUtc.ToString("O")));
        }
        catch (HarnessStoreException exception) when (exception.Code is "workspace_not_found" or "invalid_workspace_concurrency_policy" or "workspace_concurrency_version_conflict")
        {
            var status = exception.Code == "workspace_not_found" ? 404 : 409;
            return new AgentServiceResult(status, new AgentErrorResponse(exception.Code));
        }
    }

    public async Task<AgentServiceResult> ListWorkspaceChangesAsync(
        string workspaceIdRaw,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!IsWorkspaceId(workspaceIdRaw))
        {
            return new AgentServiceResult(400, new AgentErrorResponse("invalid_workspace_id"));
        }

        try
        {
            var workspaceId = new WorkspaceId(workspaceIdRaw);
            var changes = await _workspaceCoordinatorStore.ListChangesAsync(workspaceId, afterSequence, limit, cancellationToken).ConfigureAwait(false);
            return new AgentServiceResult(
                200,
                new WorkspaceChangesResponse(
                    changes.Select(change => new WorkspaceChangeItem(
                        change.ChangeId,
                        change.WorkspaceId,
                        change.NormalizedPath,
                        change.CommitSequence,
                        change.Status,
                        change.BeforeSha256,
                        change.AfterSha256,
                        change.CreatedAtUtc.ToString("O"))).ToArray(),
                    changes.Count == limit ? changes[^1].CommitSequence.ToString(CultureInfo.InvariantCulture) : null));
        }
        catch (HarnessStoreException exception) when (exception.Code == "invalid_workspace_changes_query")
        {
            return new AgentServiceResult(400, new AgentErrorResponse(exception.Code));
        }
    }

    public async Task<AgentServiceResult> ListWorkspaceActivityAsync(
        string workspaceIdRaw,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!IsWorkspaceId(workspaceIdRaw))
        {
            return new AgentServiceResult(400, new AgentErrorResponse("invalid_workspace_id"));
        }

        try
        {
            var workspaceId = new WorkspaceId(workspaceIdRaw);
            var changes = await _workspaceCoordinatorStore.ListChangesAsync(workspaceId, afterSequence, limit, cancellationToken).ConfigureAwait(false);
            var activity = changes.Select(change =>
            {
                var external = change.RunId is null;
                return new WorkspaceActivityItem(
                    change.ChangeId,
                    change.WorkspaceId,
                    external
                        ? "workspace_external_change_detected"
                        : change.Status == "rejected" ? "workspace_conflict_detected" : "workspace_file_changed",
                    change.NormalizedPath,
                    external ? WorkspaceWriterKind.UnknownExternal : WorkspaceWriterKind.RuPeer,
                    change.CommitSequence,
                    change.CreatedAtUtc.ToString("O"),
                    null);
            }).ToArray();
            var nextCursor = changes.Count == limit
                ? changes[^1].CommitSequence.ToString(CultureInfo.InvariantCulture)
                : null;
            return new AgentServiceResult(200, new WorkspaceActivityResponse(activity, nextCursor));
        }
        catch (HarnessStoreException exception) when (exception.Code == "invalid_workspace_changes_query")
        {
            return new AgentServiceResult(400, new AgentErrorResponse(exception.Code));
        }
    }

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
                : Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant();
            // The external-change journal records a missing file as 64 zeros, so a
            // client echoing that value must match a file that is still missing.
            var matchesMissingMarker = !targetExists
                && string.Equals(request.ExpectedCurrentSha256, MissingFileSha256, StringComparison.Ordinal);
            var expectedSha = matchesMissingMarker ? MissingFileSha256 : request.ExpectedCurrentSha256;
            if (!RestoreTargetMatches(currentSha, expectedSha))
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

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (ContainsReparsePointBoundary(root, target))
            {
                throw new UnauthorizedAccessException("workspace_reparse_boundary");
            }

            var temporary = target + ".ru-restore-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllBytesAsync(temporary, restoredBytes, cancellationToken).ConfigureAwait(false);
                // Journal decrypt/encrypt and the temp write can take long enough for an
                // editor or another run to change the target after the first check. Check
                // again at the last moment; a changed target is a stale restore, never a
                // silent overwrite of the newer content.
                var targetAtReplace = File.Exists(target)
                    ? await ComputeFileSha256Async(target, cancellationToken).ConfigureAwait(false)
                    : MissingFileSha256;
                if (!RestoreTargetMatches(targetAtReplace, expectedSha))
                {
                    return new AgentServiceResult(409, new AgentErrorResponse("workspace_restore_stale"));
                }

                File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
                catch (IOException)
                {
                    // Best effort: a locked temp file stays behind as a
                    // `.ru-restore-*` sibling; nothing removes it automatically.
                }
            }

            // Re-validate after move to detect symlink swap (TOCTOU)
            var postMoveSha = File.Exists(target)
                ? await ComputeFileSha256Async(target, cancellationToken).ConfigureAwait(false)
                : Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant();
            if (!string.Equals(postMoveSha, restoredSha, StringComparison.Ordinal))
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

    /// <summary>
    /// The restore target must still hold exactly the bytes the client saw. There is
    /// no atomic compare-and-swap for a file, so both the first check and the
    /// last-moment check use this exact comparison; the second one is what keeps a
    /// concurrent editor or run from being overwritten.
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

    private static string ResolveWorkspaceFile(string root, string relativePath) =>
        AgentV1Shared.ResolveWorkspaceFile(root, relativePath);

    private static bool ContainsReparsePointBoundary(string root, string target) =>
        AgentV1Shared.ContainsReparsePointBoundary(root, target);

    private static bool TryReadRestoreContent(ReadOnlySpan<byte> plaintext, out byte[] content) =>
        AgentV1Shared.TryReadRestoreContent(plaintext, out content);
}
