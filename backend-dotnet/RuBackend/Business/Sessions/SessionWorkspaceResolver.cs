using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Workspace;
using RuBackend.Harness.Workspace;

namespace RuBackend.Business.Sessions;

/// <summary>Latest-run workspace lookup for session lists and exports.</summary>
public static class SessionWorkspaceResolver
{
    public const int MaximumRequestedSessions = 500;

    public static string BuildLatestWorkspaceSql(int sessionCount)
    {
        if (sessionCount is < 1 or > MaximumRequestedSessions)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionCount));
        }

        var requestedSessions = string.Join(", ", Enumerable.Range(0, sessionCount)
            .Select(index => $"($session_{index})"));
        return $"""
            WITH requested(session_id) AS (VALUES {requestedSessions})
            SELECT requested.session_id, latest.workspace_id
            FROM requested
            JOIN agent_runs AS latest ON latest.run_id = (
                SELECT candidate.run_id
                FROM agent_runs AS candidate
                WHERE candidate.session_id = requested.session_id
                ORDER BY candidate.created_at_utc DESC, candidate.run_id DESC
                LIMIT 1)
            """;
    }

    public static string LabelForRoot(string root)
    {
        var label = Path.GetFileName(root.TrimEnd('/', '\\'));
        return string.IsNullOrEmpty(label) ? "workspace" : label;
    }
    public static async Task<IReadOnlyDictionary<string, (string WorkspaceId, string Label, string Path)>>
        ResolveAsync(
            IReadOnlyList<string> sessionIds,
            HarnessSqliteOptions sqliteOptions,
            SqliteWorkspaceStore workspaceStore,
            WorkspaceTrustService trustService,
            CancellationToken cancellationToken)
    {
        var empty = new Dictionary<string, (string WorkspaceId, string Label, string Path)>(StringComparer.Ordinal);
        if (sessionIds.Count == 0)
        {
            return empty;
        }

        try
        {
            var latestBySession = new Dictionary<string, string>(StringComparer.Ordinal);
            await using var connection = await OpenHarnessConnectionAsync(sqliteOptions, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = SessionWorkspaceResolver.BuildLatestWorkspaceSql(sessionIds.Count);
            for (var index = 0; index < sessionIds.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                command.Parameters.AddWithValue($"$session_{index}", sessionIds[index]);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                latestBySession[reader.GetString(0)] = reader.GetString(1);
            }

            var roots = new Dictionary<string, (string Label, string Path)>(StringComparer.Ordinal);
            foreach (var workspaceId in latestBySession.Values.Distinct(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var stored = await workspaceStore.GetAsync(new WorkspaceId(workspaceId), cancellationToken).ConfigureAwait(false);
                    if (stored is null)
                    {
                        continue;
                    }

                    var root = await trustService.DecryptCanonicalRootAsync(stored, cancellationToken).ConfigureAwait(false);
                    roots[workspaceId] = (SessionWorkspaceResolver.LabelForRoot(root), root);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Unknown or moved workspace: the session stays ungrouped.
                }
            }

            var result = new Dictionary<string, (string WorkspaceId, string Label, string Path)>(StringComparer.Ordinal);
            foreach (var (sessionId, workspaceId) in latestBySession)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (roots.TryGetValue(workspaceId, out var root))
                {
                    result[sessionId] = (workspaceId, root.Label, root.Path);
                }
            }

            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return empty;
        }
    }

    internal static async Task<SqliteConnection> OpenHarnessConnectionAsync(
        HarnessSqliteOptions sqliteOptions,
        CancellationToken cancellationToken)
    {
        // Read-only and unpooled: this resolver must not create the database, and
        // disposed handles must not keep test or host data directories open.
        return await sqliteOptions.OpenConnectionAsync(
                cancellationToken,
                SqliteOpenMode.ReadOnly,
                pooling: false)
            .ConfigureAwait(false);
    }

}
