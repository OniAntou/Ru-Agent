using Microsoft.Data.Sqlite;

namespace RuAgent.Harness.Persistence;

public sealed record HarnessSqliteOptions
{
    private const int DefaultBusyTimeoutMilliseconds = 5_000;

    public HarnessSqliteOptions(
        string databasePath,
        int busyTimeoutMilliseconds = DefaultBusyTimeoutMilliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (busyTimeoutMilliseconds is < 1 or > 60_000)
        {
            throw new ArgumentOutOfRangeException(nameof(busyTimeoutMilliseconds));
        }

        DatabasePath = Path.GetFullPath(databasePath);
        BusyTimeoutMilliseconds = busyTimeoutMilliseconds;
    }

    public string DatabasePath { get; }

    public int BusyTimeoutMilliseconds { get; }

    public async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken,
        SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        // Private per-connection caches only: a shared cache makes table-level
        // locks fail immediately with SQLITE_LOCKED (bypassing busy_timeout)
        // whenever one pooled connection holds a read transaction while another
        // writes the same table.
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = mode,
            Pooling = true,
            Cache = SqliteCacheMode.Private,
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA foreign_keys = ON; PRAGMA busy_timeout = {BusyTimeoutMilliseconds};";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
