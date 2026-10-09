using Microsoft.Data.Sqlite;
using RuAgent.Harness.Persistence;

namespace RuBackend.Business;

internal sealed class BusinessSqliteDatabase(DataPaths paths)
{
    private readonly HarnessSqliteOptions _options = new(paths.AgentHarnessDatabase);
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _isInitialized;

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_isInitialized)
            {
                var health = await HarnessSqliteSchema.InitializeAsync(_options, cancellationToken).ConfigureAwait(false);
                if (!health.IsAvailable) throw new IOException(health.ErrorCode);
                _isInitialized = true;
            }
        }
        finally { _initializationGate.Release(); }
        return await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<bool> HasMigratedAsync(
        SqliteConnection connection, SqliteTransaction transaction, string name, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM business_migrations WHERE name = $name;";
        command.Parameters.AddWithValue("$name", name);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    public static async Task MarkMigratedAsync(
        SqliteConnection connection, SqliteTransaction transaction, string name, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO business_migrations(name) VALUES ($name);";
        command.Parameters.AddWithValue("$name", name);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
