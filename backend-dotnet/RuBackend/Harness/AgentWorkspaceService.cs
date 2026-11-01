using System.Globalization;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Workspace;
using RuAgent.Protocol.Harness;
using RuBackend.Harness.Workspace;
using static RuBackend.Harness.AgentV1Shared;

namespace RuBackend.Harness;

public sealed partial class AgentWorkspaceService
{
    private readonly HarnessSqliteOptions _options;
    private readonly SqliteWorkspaceStore _workspaceStore;
    private readonly WorkspaceTrustService _trustService;
    private readonly ITimeProvider _time;
    private readonly WorkspaceCoordinatorStore _workspaceCoordinatorStore;
    private readonly IWorkspaceJournalProtector _workspaceJournalProtector;

    public AgentWorkspaceService(HarnessSqliteOptions options, SqliteWorkspaceStore workspaceStore,
        WorkspaceTrustService trustService, ITimeProvider time,
        WorkspaceCoordinatorStore workspaceCoordinatorStore, IWorkspaceJournalProtector workspaceJournalProtector)
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

}
