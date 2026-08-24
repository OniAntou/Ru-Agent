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

    internal async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken,
        SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = mode,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
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
