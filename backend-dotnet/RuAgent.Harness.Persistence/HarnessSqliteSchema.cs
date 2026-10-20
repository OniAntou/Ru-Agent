using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace RuAgent.Harness.Persistence;

public sealed record HarnessStoreHealth(bool IsAvailable, int SchemaVersion, string? ErrorCode)
{
    public static HarnessStoreHealth Available(int schemaVersion) =>
        new(true, schemaVersion, null);

    public static HarnessStoreHealth Unavailable(string errorCode) =>
        new(false, 0, errorCode);
}

public static partial class HarnessSqliteSchema
{
    public static int CurrentVersion => MigrationCatalog.Value[^1].To;

    /// <summary>
    /// Process-wide memo of a successful initialization, keyed by database path.
    /// <see cref="InitializeAsync"/> runs a full integrity probe
    /// (<c>PRAGMA quick_check</c> scans the whole file) plus the table and index
    /// shape checks, which is startup work; request-path callers (session
    /// artifact operations, work-state saves) must not pay it per operation.
    /// Results are only cached when the store is available: a transient storage
    /// failure must be able to recover on the next call.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Task<HarnessStoreHealth>> InitializedStores =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The request-path entry point: initializes the store at most once per
    /// database path per process. Startup keeps calling
    /// <see cref="InitializeAsync"/> directly so the full probe still runs once.
    /// </summary>
    public static async Task<HarnessStoreHealth> EnsureInitializedAsync(
        HarnessSqliteOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var key = Path.GetFullPath(options.DatabasePath);
        if (InitializedStores.TryGetValue(key, out var cached))
        {
            return await cached.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        var health = await InitializeAsync(options, cancellationToken).ConfigureAwait(false);
        if (health.IsAvailable)
        {
            InitializedStores.TryAdd(key, Task.FromResult(health));
        }

        return health;
    }

    public static async Task<HarnessStoreHealth> InitializeAsync(
        HarnessSqliteOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            var directory = Path.GetDirectoryName(options.DatabasePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return HarnessStoreHealth.Unavailable("harness_store_unavailable");
            }

            Directory.CreateDirectory(directory);
            await using var connection = await options.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (var walCmd = connection.CreateCommand())
            {
                // On synchronized or shared storage (OneDrive-backed folders,
                // SMB shares) this pragma silently falls back to another journal
                // mode. The whole multi-process design -- busy_timeout, shared
                // readers, the durability reasoning behind the migration
                // transactions -- assumes wal, so report the store unavailable
                // rather than start on a wrong assumption.
                walCmd.CommandText = "PRAGMA journal_mode = WAL;";
                var mode = await walCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (mode is not string journal
                    || !string.Equals(journal, "wal", StringComparison.OrdinalIgnoreCase))
                {
                    return HarnessStoreHealth.Unavailable("harness_journal_not_wal");
                }
            }

            await using (var foreignKeysCmd = connection.CreateCommand())
            {
                foreignKeysCmd.CommandText = "PRAGMA foreign_keys = ON;";
                await foreignKeysCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var existingVersion = await ReadUserVersionAsync(connection, transaction: null, cancellationToken)
                .ConfigureAwait(false);
            var migrations = MigrationCatalog.Value;
            if (existingVersion != 0 && existingVersion != CurrentVersion
                && !migrations.Any(migration => migration.From == existingVersion))
                return HarnessStoreHealth.Unavailable("harness_schema_incompatible");

            if (existingVersion != CurrentVersion)
            {
                // Commit the entire selected suffix and its version together.
                // Fresh installs use the released baseline, so their final shape
                // cannot diverge from an upgrade of the supported release.
                var batch = new System.Text.StringBuilder(existingVersion == 0 ? ReleasedBaselineSql : "");
                var cursor = existingVersion == 0 ? 18 : existingVersion;
                while (cursor != CurrentVersion)
                {
                    var migration = migrations.SingleOrDefault(candidate => candidate.From == cursor);
                    if (migration is null || migration.To <= cursor)
                        return HarnessStoreHealth.Unavailable("harness_schema_incompatible");
                    foreach (var statement in migration.Statements) batch.AppendLine(statement);
                    cursor = migration.To;
                }
                await ApplyMigrationAsync(connection, existingVersion, batch.ToString(), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (await ReadUserVersionAsync(connection, transaction: null, cancellationToken).ConfigureAwait(false) != CurrentVersion)
                return HarnessStoreHealth.Unavailable("harness_schema_incompatible");

            if (!await HarnessSqliteSchemaValidator.ValidateAsync(connection, cancellationToken)
                .ConfigureAwait(false))
            {
                return HarnessStoreHealth.Unavailable("harness_schema_invalid");
            }

            return HarnessStoreHealth.Available(CurrentVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedStorageFailure(exception))
        {
            return HarnessStoreHealth.Unavailable("harness_store_unavailable");
        }
    }

    private static async Task<long> ReadUserVersionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version";
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    /// <summary>
    /// Applies one upgrade batch and advances <c>user_version</c> in a single transaction.
    /// </summary>
    /// <remarks>
    /// The DDL and the version bump used to run as separate autocommit statements, so a
    /// crash between them left a database that was neither the old schema nor the new one,
    /// and the version still said "not upgraded" -- the next start re-ran the batch against
    /// tables an earlier attempt had already rebuilt. The released run-table rebuild is not
    /// idempotent (it drops the original and relies on the rename), so that database could
    /// not be opened again at all. SQLite rolls back DDL, and <c>user_version</c> is part of
    /// the transaction too, so one transaction makes an interrupted upgrade indistinguishable
    /// from one that never started: the next start simply repeats it.
    ///
    /// The version is also re-read *inside* the transaction. Two processes starting at the
    /// same time both read the old version before either transaction, but the second one
    /// blocks on the write lock until the first commits; without the re-read it would then
    /// replay the batch against the already-migrated schema. If the version moved since the
    /// pre-transaction read, this batch is skipped instead.
    /// </remarks>
    private static async Task ApplyMigrationAsync(
        SqliteConnection connection,
        long expectedVersion,
        string migrationSql,
        CancellationToken cancellationToken)
    {
        // Foreign keys are disabled for the whole batch, not inside it: the pragma is a
        // no-op inside a transaction, and it cannot be left out. The released run-table rebuild
        // drops a table that rows cascade from, so with enforcement on, dropping the old
        // table would delete the rows the rebuild is in the middle of copying. The setting
        // is per connection and the connection is pooled, so the finally below always
        // re-enables it -- including on the rollback path, where a skipped re-enable
        // would hand a pragma-OFF connection to the next borrower.
        await using (var disable = connection.CreateCommand())
        {
            disable.CommandText = "PRAGMA foreign_keys = OFF;";
            await disable.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                var versionUnderLock = await ReadUserVersionAsync(connection, transaction, cancellationToken)
                    .ConfigureAwait(false);
                if (versionUnderLock != expectedVersion)
                {
                    // Another process migrated the database after this call read the
                    // version but before the batch took the write lock. The schema has
                    // moved on, so the batch must not run again; the post-initialize
                    // validator judges the resulting shape.
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                await using (var migration = connection.CreateCommand())
                {
                    migration.Transaction = transaction;
                    migration.CommandText = migrationSql;
                    await using var migrationReader = await migration.ExecuteReaderAsync(cancellationToken)
                        .ConfigureAwait(false);
                    do
                    {
                        while (await migrationReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                        }
                    }
                    while (await migrationReader.NextResultAsync(cancellationToken).ConfigureAwait(false));
                }

                await using (var version = connection.CreateCommand())
                {
                    version.Transaction = transaction;
                    version.CommandText = $"PRAGMA user_version = {CurrentVersion};";
                    await version.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A failed rollback (cancelled token, I/O error) must not
                    // replace the migration failure the caller reports.
                }

                throw;
            }
        }
        finally
        {
            // Re-enable even when the batch threw: this connection goes back to
            // a pool (HarnessSqliteOptions sets Pooling = true), and a per-
            // connection pragma left OFF would be silently inherited by the next
            // borrower -- the comment below only holds for a *closed*
            // connection, not a pooled one.
            await using var enable = connection.CreateCommand();
            enable.CommandText = "PRAGMA foreign_keys = ON;";
            try
            {
                await enable.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (SqliteException)
            {
                // The connection itself is broken (disk, share); pooling drops
                // it, so no poisoned state can be handed to the next borrower.
            }
        }
    }

    private static bool IsExpectedStorageFailure(Exception exception) =>
        exception is SqliteException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException;

}
