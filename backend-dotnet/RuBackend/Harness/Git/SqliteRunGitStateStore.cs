using System.Text.Json;
using Microsoft.Data.Sqlite;
using RuAgent.Protocol.Harness;

namespace RuBackend.Harness.Git;

public sealed record RunGitRehydratedState(
    RunGitWorktreeSnapshot StartSnapshot,
    IReadOnlyList<string> AttributedPaths,
    DateTimeOffset StoredAtUtc);

/// <summary>
/// Host-side durable store for run-attributed Git/workspace state. It persists the
/// bounded content-free start snapshot and the attributed-path set so a restarted Broker
/// can rehydrate them for a refreshed lease. Snapshots carry hashes, codes, and paths
/// only; raw diffs and file contents are never persisted here or anywhere in this
/// database. Once a run is recorded as ineligible for a managed commit, the flag can
/// never flip back within that run.
/// </summary>
public sealed class SqliteRunGitStateStore : IDisposable
{
    public const int MaxStoredEntries = 1000;
    private const int MaxRunIdLength = 128;
    private const int MaxSnapshotJsonCharacters = 4_000_000;
    private const int MaxAttributedPathLength = 4096;
    private const int MaxAttributedPathsJsonCharacters = 1_000_000;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly string _databasePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private bool _disposed;

    public SqliteRunGitStateStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("DatabasePath is required.", nameof(databasePath));
        }

        _databasePath = Path.GetFullPath(databasePath);
        var parent = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        _connection = Open(_databasePath);
        Initialize(_connection);
    }

    public async Task SaveStartSnapshotAsync(string runId, RunGitWorktreeSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ValidateRunId(runId);
        ArgumentNullException.ThrowIfNull(snapshot);
        var bounded = BoundEntries(snapshot);
        var json = JsonSerializer.Serialize(bounded, SerializerOptions);
        if (json.Length > MaxSnapshotJsonCharacters)
        {
            throw new ArgumentException("start_snapshot_too_large", nameof(snapshot));
        }
        var now = DateTimeOffset.UtcNow.ToString("O");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var previouslyIneligible = await ReadIneligibilityAsync(runId, cancellationToken);
            if (previouslyIneligible)
            {
                bounded = bounded with { ManagedCommitEligible = false };
                json = JsonSerializer.Serialize(bounded, SerializerOptions);
            }

            await using var command = CreateCommand("""
                INSERT INTO run_git_state (run_id, start_snapshot_json, managed_commit_eligible, attributed_paths_json, stored_at_utc, updated_at_utc)
                VALUES ($run_id, $snapshot_json, $eligible, '[]', $now, $now)
                ON CONFLICT(run_id) DO UPDATE SET
                    start_snapshot_json = $snapshot_json,
                    managed_commit_eligible = $eligible,
                    updated_at_utc = $now
                """);
            command.Parameters.AddWithValue("$run_id", runId);
            command.Parameters.AddWithValue("$snapshot_json", json);
            command.Parameters.AddWithValue("$eligible", bounded.ManagedCommitEligible ? 1 : 0);
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> ReadIneligibilityAsync(string runId, CancellationToken cancellationToken)
    {
        ValidateRunId(runId);
        await using var command = CreateCommand(
            "SELECT managed_commit_eligible FROM run_git_state WHERE run_id = $run_id");
        command.Parameters.AddWithValue("$run_id", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) && reader.GetInt64(0) == 0;
    }

    public async Task<RunGitWorktreeSnapshot?> TryGetStartSnapshotAsync(string runId, CancellationToken cancellationToken = default)
    {
        ValidateRunId(runId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var command = CreateCommand(
                "SELECT start_snapshot_json FROM run_git_state WHERE run_id = $run_id");
            command.Parameters.AddWithValue("$run_id", runId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            return reader.IsDBNull(0) ? null : DeserializeSnapshot(reader.GetString(0));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAttributedPathsAsync(string runId, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ValidateRunId(runId);
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count > MaxStoredEntries)
        {
            throw new ArgumentException("attributed_paths_too_many", nameof(paths));
        }

        var boundedPaths = paths
            .Select(path => path?.Trim() ?? string.Empty)
            .ToArray();
        if (boundedPaths.Any(path => path.Length == 0 || path.Length > MaxAttributedPathLength))
        {
            throw new ArgumentException("attributed_path_invalid", nameof(paths));
        }

        var json = JsonSerializer.Serialize(boundedPaths, SerializerOptions);
        if (json.Length > MaxAttributedPathsJsonCharacters)
        {
            throw new ArgumentException("attributed_paths_too_large", nameof(paths));
        }
        var now = DateTimeOffset.UtcNow.ToString("O");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var command = CreateCommand("""
                INSERT INTO run_git_state (run_id, start_snapshot_json, managed_commit_eligible, attributed_paths_json, stored_at_utc, updated_at_utc)
                VALUES ($run_id, NULL, 1, $paths_json, $now, $now)
                ON CONFLICT(run_id) DO UPDATE SET
                    attributed_paths_json = $paths_json,
                    updated_at_utc = $now
                """);
            command.Parameters.AddWithValue("$run_id", runId);
            command.Parameters.AddWithValue("$paths_json", json);
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<string>?> TryGetAttributedPathsAsync(string runId, CancellationToken cancellationToken = default)
    {
        ValidateRunId(runId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var command = CreateCommand(
                "SELECT attributed_paths_json FROM run_git_state WHERE run_id = $run_id");
            command.Parameters.AddWithValue("$run_id", runId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            return DeserializeAttributedPaths(reader.GetString(0));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Rehydrates the complete bounded start state for a run after a restart: the Host
    /// returns the stored snapshot and attributed-path set to the refreshed lease holder.
    /// Returns null when the run has no recorded start snapshot.
    /// </summary>
    public async Task<RunGitRehydratedState?> TryLoadForRefreshedLeaseAsync(string runId, CancellationToken cancellationToken = default)
    {
        ValidateRunId(runId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var command = CreateCommand(
                """
                SELECT start_snapshot_json, attributed_paths_json, stored_at_utc
                FROM run_git_state WHERE run_id = $run_id
                """);
            command.Parameters.AddWithValue("$run_id", runId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
            {
                return null;
            }

            var snapshot = DeserializeSnapshot(reader.GetString(0));
            if (snapshot is null)
            {
                return null;
            }

            var paths = DeserializeAttributedPaths(reader.GetString(1));
            if (paths is null)
            {
                return null;
            }
            var storedAt = DateTimeOffset.TryParse(reader.GetString(2), out var parsed) ? parsed : DateTimeOffset.MinValue;
            return new RunGitRehydratedState(snapshot, paths, storedAt);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static RunGitWorktreeSnapshot BoundEntries(RunGitWorktreeSnapshot snapshot)
    {
        if (snapshot.Entries.Count <= MaxStoredEntries && !snapshot.StatusTruncated)
        {
            return snapshot;
        }

        var entries = snapshot.Entries;
        var truncated = snapshot.StatusTruncated || entries.Count > MaxStoredEntries;
        if (entries.Count > MaxStoredEntries)
        {
            entries = entries.Take(MaxStoredEntries).ToList();
        }

        return snapshot with { Entries = entries, StatusTruncated = truncated };
    }

    private static RunGitWorktreeSnapshot? DeserializeSnapshot(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxSnapshotJsonCharacters)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RunGitWorktreeSnapshot>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string>? DeserializeAttributedPaths(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxAttributedPathsJsonCharacters)
        {
            return null;
        }

        try
        {
            var paths = JsonSerializer.Deserialize<List<string>>(json, SerializerOptions);
            if (paths is null || paths.Count > MaxStoredEntries)
            {
                return null;
            }

            var bounded = paths
                .Select(path => path?.Trim() ?? string.Empty)
                .ToArray();
            return bounded.Any(path => path.Length == 0 || path.Length > MaxAttributedPathLength)
                ? null
                : bounded;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ValidateRunId(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (runId.Length > MaxRunIdLength)
        {
            throw new ArgumentException("run_id_too_long", nameof(runId));
        }
    }

    private SqliteCommand CreateCommand(string text)
    {
        var command = _connection?.CreateCommand() ?? throw new ObjectDisposedException(nameof(SqliteRunGitStateStore));
        command.CommandText = text;
        return command;
    }

    private static SqliteConnection Open(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static void Initialize(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS run_git_state (
                run_id TEXT PRIMARY KEY,
                start_snapshot_json TEXT,
                managed_commit_eligible INTEGER NOT NULL DEFAULT 1,
                attributed_paths_json TEXT NOT NULL DEFAULT '[]',
                stored_at_utc TEXT,
                updated_at_utc TEXT NOT NULL
            )
            """;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_connection is not null)
        {
            SqliteConnection.ClearPool(_connection);
            _connection.Dispose();
            _connection = null;
        }

        _gate.Dispose();
    }
}
