using System.Globalization;
using Microsoft.Data.Sqlite;

namespace RuBackend.Memory;

public sealed class SqliteMemoryRepository : IMemoryRepository
{
    private static readonly string[] ValidStatuses = ["active", "superseded", "deleted"];
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public SqliteMemoryRepository(MemorySqliteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.DatabasePath))
        {
            throw new ArgumentException("DatabasePath is required.", nameof(options));
        }

        var fullPath = Path.GetFullPath(options.DatabasePath);
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        try
        {
            _connection = Open(fullPath);
            Initialize();
        }
        catch (SqliteException)
        {
            _connection?.Dispose();
            Quarantine(fullPath);
            _connection = Open(fullPath);
            Initialize();
        }

        PurgeExpired();
    }

    public async Task<MemoryRecord?> GetAsync(string id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureOpen();
            await using var command = CreateCommand("SELECT * FROM memories WHERE id = $id");
            command.Parameters.AddWithValue("$id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<MemoryRecord>> ListActiveAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureOpen();
            await using var command = CreateCommand("""
                SELECT * FROM memories
                WHERE status = 'active' AND (expires_at IS NULL OR expires_at > $now OR pinned = 1)
                ORDER BY pinned DESC, updated_at DESC
                """);
            command.Parameters.AddWithValue("$now", SerializeTimestamp(DateTimeOffset.UtcNow));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var records = new List<MemoryRecord>();
            while (await reader.ReadAsync(cancellationToken))
            {
                records.Add(ReadRecord(reader));
            }

            return records;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> IsLegacyMigrationRecordedAsync(string sourceHash, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureOpen();
            await using var command = CreateCommand("SELECT 1 FROM legacy_memory_migrations WHERE source_hash = $source_hash LIMIT 1");
            command.Parameters.AddWithValue("$source_hash", sourceHash);
            return await command.ExecuteScalarAsync(cancellationToken) is not null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordLegacyMigrationAsync(string sourceHash, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureOpen();
            await using var command = CreateCommand("INSERT OR IGNORE INTO legacy_memory_migrations (source_hash) VALUES ($source_hash)");
            command.Parameters.AddWithValue("$source_hash", sourceHash);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<IReadOnlyList<MemoryRecord>> RetrieveAsync(string query, int limit, int maxChars, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Retrieval is implemented by Task 3.");

    public Task<MemoryRecord> UpsertAsync(MemoryCandidate candidate, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Upsert is implemented by Task 3.");

    public Task ClearAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("Clear is implemented by Task 3.");

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync();
        try
        {
            if (!_disposed)
            {
                _connection.Dispose();
                _disposed = true;
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 30,
            ForeignKeys = true,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void Initialize()
    {
        using var command = CreateCommand("""
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA mmap_size = 300000000;
            PRAGMA temp_store = MEMORY;
            CREATE TABLE IF NOT EXISTS memories (
                id TEXT PRIMARY KEY,
                kind TEXT NOT NULL CHECK(kind IN ('profile', 'preference', 'project', 'decision', 'session_summary')),
                content TEXT NOT NULL,
                scope TEXT,
                evidence TEXT NOT NULL,
                confidence REAL NOT NULL CHECK(confidence >= 0 AND confidence <= 1),
                importance REAL NOT NULL CHECK(importance >= 0 AND importance <= 1),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                accessed_at TEXT,
                expires_at TEXT,
                status TEXT NOT NULL CHECK(status IN ('active', 'superseded', 'deleted')),
                pinned INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_memories_status_expires ON memories (status, expires_at);
            CREATE TABLE IF NOT EXISTS legacy_memory_migrations (
                source_hash TEXT PRIMARY KEY
            );
            """);
        command.ExecuteNonQuery();
    }

    private void PurgeExpired()
    {
        using var command = CreateCommand("DELETE FROM memories WHERE status = 'active' AND pinned = 0 AND expires_at IS NOT NULL AND expires_at < $now");
        command.Parameters.AddWithValue("$now", SerializeTimestamp(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
    }

    private SqliteCommand CreateCommand(string sql)
    {
        EnsureOpen();
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    private void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static MemoryRecord ReadRecord(SqliteDataReader reader)
    {
        var kind = reader.GetString(reader.GetOrdinal("kind"));
        var status = reader.GetString(reader.GetOrdinal("status"));
        return new MemoryRecord(
            reader.GetString(reader.GetOrdinal("id")),
            ParseKind(kind),
            reader.GetString(reader.GetOrdinal("content")),
            GetNullableString(reader, "scope"),
            reader.GetString(reader.GetOrdinal("evidence")),
            reader.GetDouble(reader.GetOrdinal("confidence")),
            reader.GetDouble(reader.GetOrdinal("importance")),
            ParseTimestamp(reader.GetString(reader.GetOrdinal("created_at"))),
            ParseTimestamp(reader.GetString(reader.GetOrdinal("updated_at"))),
            ParseNullableTimestamp(reader, "accessed_at"),
            ParseNullableTimestamp(reader, "expires_at"),
            ParseStatus(status),
            reader.GetBoolean(reader.GetOrdinal("pinned")));
    }

    private static MemoryKind ParseKind(string value) => value switch
    {
        "profile" => MemoryKind.Profile,
        "preference" => MemoryKind.Preference,
        "project" => MemoryKind.Project,
        "decision" => MemoryKind.Decision,
        "session_summary" => MemoryKind.SessionSummary,
        _ => throw new InvalidDataException($"Unknown memory kind: {value}"),
    };

    private static string ParseStatus(string value) =>
        ValidStatuses.Contains(value, StringComparer.Ordinal)
            ? value
            : throw new InvalidDataException($"Unknown memory status: {value}");

    private static string? GetNullableString(SqliteDataReader reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetString(reader.GetOrdinal(name));

    private static DateTimeOffset? ParseNullableTimestamp(SqliteDataReader reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name)) ? null : ParseTimestamp(reader.GetString(reader.GetOrdinal(name)));

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static string SerializeTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void Quarantine(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var corruptPath = path + ".corrupt";
        File.Copy(path, corruptPath, overwrite: true);
        File.Delete(path);
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var sidecar = path + suffix;
            if (File.Exists(sidecar))
            {
                File.Delete(sidecar);
            }
        }
    }
}
