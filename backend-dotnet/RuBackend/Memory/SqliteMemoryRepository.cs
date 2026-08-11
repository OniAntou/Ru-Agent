using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace RuBackend.Memory;

public sealed class SqliteMemoryRepository : IMemoryRepository
{
    private static readonly Regex TokenPattern = new(@"\w+", RegexOptions.CultureInvariant);
    private static readonly Regex WindowsPathPattern = new(@"\b[A-Za-z]:\\[^\s,.;!?]+", RegexOptions.CultureInvariant);
    private static readonly string[] ValidStatuses = ["active", "superseded", "deleted"];
    private SqliteConnection? _connection;
    private readonly string _databasePath;
    private readonly string _legacyTextsPath;
    private readonly int _retrieveScanLimit;
    private readonly double _retrieveMinScore;
    private readonly int _retrieveLookbackDays;
    private readonly bool _readOnly;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public SqliteMemoryRepository(MemorySqliteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.DatabasePath))
        {
            throw new ArgumentException("DatabasePath is required.", nameof(options));
        }

        _legacyTextsPath = Path.GetFullPath(options.LegacyTextsPath);
        _retrieveScanLimit = options.RetrieveScanLimit;
        _retrieveMinScore = options.RetrieveMinScore;
        _retrieveLookbackDays = options.RetrieveLookbackDays;
        _readOnly = options.ReadOnly;

        var fullPath = Path.GetFullPath(options.DatabasePath);
        _databasePath = fullPath;
        var parent = Path.GetDirectoryName(fullPath);
        if (!_readOnly && !string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (_readOnly)
        {
            if (!File.Exists(fullPath))
            {
                _connection = OpenInMemory();
                Initialize();
            }

            return;
        }

        try
        {
            _connection = Open(fullPath, readOnly: false);
            Initialize();
        }
        catch (SqliteException)
        {
            _connection?.Dispose();
            Quarantine(fullPath);
            _connection = Open(fullPath, readOnly: false);
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
            OpenReadOnlyConnectionIfNeeded();
            await using var command = CreateCommand("SELECT * FROM memories WHERE id = $id");
            command.Parameters.AddWithValue("$id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
        }
        finally
        {
            CloseReadOnlyConnection();
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<MemoryRecord>> ListActiveAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureOpen();
            OpenReadOnlyConnectionIfNeeded();
            await using var command = CreateCommand("""
                SELECT * FROM memories
                WHERE status = $active_status AND (expires_at IS NULL OR expires_at > $now OR pinned = $pinned)
                ORDER BY pinned DESC, updated_at DESC
                """);
            command.Parameters.AddWithValue("$active_status", "active");
            command.Parameters.AddWithValue("$now", SerializeTimestamp(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$pinned", 1);
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
            CloseReadOnlyConnection();
            _gate.Release();
        }
    }

    public async Task<bool> IsLegacyMigrationRecordedAsync(string sourceHash, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureOpen();
            OpenReadOnlyConnectionIfNeeded();
            await using var command = CreateCommand("SELECT 1 FROM legacy_memory_migrations WHERE source_hash = $source_hash LIMIT 1");
            command.Parameters.AddWithValue("$source_hash", sourceHash);
            return await command.ExecuteScalarAsync(cancellationToken) is not null;
        }
        finally
        {
            CloseReadOnlyConnection();
            _gate.Release();
        }
    }

    public async Task RecordLegacyMigrationAsync(string sourceHash, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureOpen();
            EnsureWritable();
            await using var command = CreateCommand("INSERT OR IGNORE INTO legacy_memory_migrations (source_hash) VALUES ($source_hash)");
            command.Parameters.AddWithValue("$source_hash", sourceHash);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<MemoryRecord>> RetrieveAsync(string query, int limit, int maxChars, CancellationToken cancellationToken)
    {
        var queryTokens = Tokens(query);
        if (queryTokens.Count == 0 || limit <= 0 || maxChars <= 0)
        {
            return [];
        }

        var queryScopes = WindowsPathPattern.Matches(query)
            .Select(match => match.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;
        var lookback = now.AddDays(-_retrieveLookbackDays);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureOpen();
            OpenReadOnlyConnectionIfNeeded();
            await using var command = CreateCommand("""
                SELECT * FROM memories
                WHERE status = $active_status
                  AND (expires_at IS NULL OR expires_at > $now OR pinned = $pinned)
                  AND (pinned = $pinned OR updated_at >= $lookback)
                ORDER BY pinned DESC, updated_at DESC
                LIMIT $scan_limit
                """);
            command.Parameters.AddWithValue("$active_status", "active");
            command.Parameters.AddWithValue("$now", SerializeTimestamp(now));
            command.Parameters.AddWithValue("$pinned", 1);
            command.Parameters.AddWithValue("$lookback", SerializeTimestamp(lookback));
            command.Parameters.AddWithValue("$scan_limit", _retrieveScanLimit);

            var ranked = new List<(double Score, int Position, MemoryRecord Record)>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var position = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                var record = ReadRecord(reader);
                var scopeMatch = record.Scope is not null && queryScopes.Contains(record.Scope);
                if (queryScopes.Count > 0 && record.Scope is not null && !scopeMatch)
                {
                    position++;
                    continue;
                }

                var overlap = queryTokens.Intersect(Tokens($"{record.Content} {record.Scope ?? string.Empty}")).Count();
                var lexicalScore = (double)overlap / queryTokens.Count;
                var score =
                    0.55 * lexicalScore
                    + 0.15 * (scopeMatch ? 1 : 0)
                    + 0.05 * record.Importance
                    + 0.03 * record.Confidence
                    + 0.02 * (record.Pinned ? 1 : 0);
                if (score >= _retrieveMinScore)
                {
                    ranked.Add((score, position, record));
                }

                position++;
            }

            return FitRecords(
                ranked
                    .OrderByDescending(item => item.Score)
                    .ThenByDescending(item => item.Record.UpdatedAt)
                    .ThenBy(item => item.Position)
                    .Select(item => item.Record),
                Math.Min(limit, 5),
                maxChars);
        }
        finally
        {
            CloseReadOnlyConnection();
            _gate.Release();
        }
    }

    public async Task<MemoryRecord> UpsertAsync(MemoryCandidate candidate, CancellationToken cancellationToken)
    {
        var content = candidate.Content.Trim();
        var evidence = candidate.Evidence.Trim();
        var scope = string.IsNullOrWhiteSpace(candidate.Scope) ? null : candidate.Scope.Trim();
        if (content.Length == 0 || evidence.Length == 0)
        {
            throw new ArgumentException("Memory content and evidence cannot be blank.", nameof(candidate));
        }

        if (content.Length > 100000 || evidence.Length > 100000)
        {
            throw new ArgumentException("Memory content and evidence cannot exceed 100,000 characters.", nameof(candidate));
        }

        if (candidate.Confidence is < 0 or > 1 || candidate.Importance is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(candidate), "Memory confidence and importance must be between 0 and 1.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureOpen();
            EnsureWritable();
            var now = DateTimeOffset.UtcNow;
            await using var transaction = (SqliteTransaction)await _connection!.BeginTransactionAsync(cancellationToken);
            var duplicateId = candidate.SupersedesId is null
                ? await FindActiveDuplicateAsync(candidate.Kind, content, scope, now, transaction, cancellationToken)
                : null;
            if (duplicateId is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return await ReadByIdAsync(duplicateId, cancellationToken)
                    ?? throw new InvalidOperationException("Duplicate memory disappeared before readback.");
            }

            if (candidate.SupersedesId is not null)
            {
                await MarkSupersededAsync(candidate.SupersedesId, now, transaction, cancellationToken);
            }

            var newId = Guid.NewGuid().ToString("D");
            await InsertAsync(new MemoryCandidate(candidate.Kind, content, scope, evidence, candidate.Confidence, candidate.Importance, candidate.ExpiresAt, candidate.SupersedesId), newId, now, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await ReadByIdAsync(newId, cancellationToken)
                ?? throw new InvalidOperationException("Inserted memory disappeared before readback.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureOpen();
            EnsureWritable();
            await using var transaction = (SqliteTransaction)await _connection!.BeginTransactionAsync(cancellationToken);
            await ExecuteTransactionalAsync("DELETE FROM memories", transaction, cancellationToken);
            await ExecuteTransactionalAsync("DELETE FROM legacy_memory_migrations", transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(_legacyTextsPath) && File.Exists(_legacyTextsPath))
            {
                File.Delete(_legacyTextsPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> FindActiveDuplicateAsync(
        MemoryKind kind,
        string content,
        string? scope,
        DateTimeOffset now,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand("""
            SELECT id FROM memories
            WHERE status = $active_status
              AND kind = $kind
              AND content = $content COLLATE NOCASE
              AND (expires_at IS NULL OR expires_at > $now)
              AND (scope IS $scope OR scope = $scope)
            LIMIT 1
            """);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$active_status", "active");
        command.Parameters.AddWithValue("$kind", SerializeKind(kind));
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$now", SerializeTimestamp(now));
        command.Parameters.AddWithValue("$scope", scope ?? (object)DBNull.Value);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private async Task MarkSupersededAsync(
        string id,
        DateTimeOffset now,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand("""
            UPDATE memories
            SET status = $superseded_status, updated_at = $updated_at
            WHERE id = $id AND status = $active_status
            """);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$superseded_status", "superseded");
        command.Parameters.AddWithValue("$updated_at", SerializeTimestamp(now));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$active_status", "active");
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Superseded memory does not exist or is not active.");
        }
    }

    private async Task InsertAsync(
        MemoryCandidate candidate,
        string id,
        DateTimeOffset now,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand("""
            INSERT INTO memories (
                id, kind, content, scope, evidence, confidence, importance,
                created_at, updated_at, accessed_at, expires_at, status, pinned
            ) VALUES (
                $id, $kind, $content, $scope, $evidence, $confidence, $importance,
                $created_at, $updated_at, $accessed_at, $expires_at, $status, $pinned)
            """);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$kind", SerializeKind(candidate.Kind));
        command.Parameters.AddWithValue("$content", candidate.Content);
        command.Parameters.AddWithValue("$scope", candidate.Scope ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$evidence", candidate.Evidence);
        command.Parameters.AddWithValue("$confidence", candidate.Confidence);
        command.Parameters.AddWithValue("$importance", candidate.Importance);
        command.Parameters.AddWithValue("$created_at", SerializeTimestamp(now));
        command.Parameters.AddWithValue("$updated_at", SerializeTimestamp(now));
        command.Parameters.AddWithValue("$accessed_at", DBNull.Value);
        command.Parameters.AddWithValue("$expires_at", candidate.ExpiresAt is null ? DBNull.Value : SerializeTimestamp(candidate.ExpiresAt.Value));
        command.Parameters.AddWithValue("$status", "active");
        command.Parameters.AddWithValue("$pinned", 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<MemoryRecord?> ReadByIdAsync(string id, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand("SELECT * FROM memories WHERE id = $id");
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
    }

    private async Task ExecuteTransactionalAsync(string sql, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(sql);
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Python parity: this is the explicit case-fold subset needed by the
    // repository's Unicode fixture. Regex \w+ remains the token boundary.
    private static IReadOnlySet<string> Tokens(string text) =>
        TokenPattern.Matches(CaseFold(text))
            .Select(match => match.Value)
            .ToHashSet(StringComparer.Ordinal);

    private static string CaseFold(string text) =>
        string.Concat(text.ToLowerInvariant().SelectMany(character => character switch
        {
            '\u00DF' => "ss", // LATIN SMALL LETTER SHARP S
            '\u03C2' => "\u03C3", // GREEK SMALL LETTER FINAL SIGMA
            '\u0149' => "\u02BCn", // LATIN SMALL LETTER N PRECEDED BY APOSTROPHE
            '\u017F' => "s", // LATIN SMALL LETTER LONG S
            '\u212A' => "k", // KELVIN SIGN
            '\u2126' => "\u03C9", // OHM SIGN
            '\uFB00' => "ff",
            '\uFB01' => "fi",
            '\uFB02' => "fl",
            '\uFB03' => "ffi",
            '\uFB04' => "ffl",
            '\uFB05' or '\uFB06' => "st",
            _ => character.ToString(),
        }));

    private static IReadOnlyList<MemoryRecord> FitRecords(IEnumerable<MemoryRecord> records, int limit, int maxChars)
    {
        var fitted = new List<MemoryRecord>();
        var usedChars = 0;
        foreach (var record in records)
        {
            if (fitted.Count >= limit)
            {
                break;
            }

            var line = RenderRecord(record);
            var separator = fitted.Count == 0 ? 0 : 1;
            if (usedChars + separator + line.Length > maxChars)
            {
                break;
            }

            fitted.Add(record);
            usedChars += separator + line.Length;
        }

        return fitted;
    }

    private static string RenderRecord(MemoryRecord record) =>
        $"- [{SerializeKind(record.Kind)} | {record.Scope ?? "global"}] {record.Content}";

    private static string SerializeKind(MemoryKind kind) => kind switch
    {
        MemoryKind.Profile => "profile",
        MemoryKind.Preference => "preference",
        MemoryKind.Project => "project",
        MemoryKind.Decision => "decision",
        MemoryKind.SessionSummary => "session_summary",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown memory kind."),
    };

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
                _connection?.Dispose();
                _connection = null;
                _disposed = true;
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private static SqliteConnection Open(string path, bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
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

    private static SqliteConnection OpenInMemory()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ":memory:",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Private,
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

    private void OpenReadOnlyConnectionIfNeeded()
    {
        if (!_readOnly || _connection is not null)
        {
            return;
        }

        if (File.Exists(_databasePath))
        {
            _connection = Open(_databasePath, readOnly: true);
            return;
        }

        _connection = OpenInMemory();
        Initialize();
    }

    private void CloseReadOnlyConnection()
    {
        if (!_readOnly || _connection is null)
        {
            return;
        }

        _connection.Dispose();
        _connection = null;
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
        using var command = CreateCommand("DELETE FROM memories WHERE status = $active_status AND pinned = $unpinned AND expires_at IS NOT NULL AND expires_at < $now");
        command.Parameters.AddWithValue("$active_status", "active");
        command.Parameters.AddWithValue("$unpinned", 0);
        command.Parameters.AddWithValue("$now", SerializeTimestamp(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
    }

    private SqliteCommand CreateCommand(string sql)
    {
        EnsureOpen();
        var command = (_connection ?? throw new InvalidOperationException("Memory database is not open.")).CreateCommand();
        command.CommandText = sql;
        return command;
    }

    private void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void EnsureWritable()
    {
        if (_readOnly)
        {
            throw new InvalidOperationException("Memory repository is read-only.");
        }
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
