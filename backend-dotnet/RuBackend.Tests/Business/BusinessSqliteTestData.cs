using Microsoft.Data.Sqlite;
using RuBackend.Business;

namespace RuBackend.Tests.Business;

internal static class BusinessSqliteTestData
{
    public static async Task<string> ReadSettingsAsync(DataPaths paths)
    {
        await using var connection = await OpenAsync(paths);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT document FROM business_settings WHERE id = 1;";
        return (string)(await command.ExecuteScalarAsync())!;
    }

    public static async Task<string[]> ReadActivityAsync(DataPaths paths)
    {
        await using var connection = await OpenAsync(paths);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT document FROM business_activity ORDER BY sequence;";
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return rows.ToArray();
    }

    public static async Task ExecuteAsync(DataPaths paths, string sql)
    {
        await using var connection = await OpenAsync(paths);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SqliteConnection> OpenAsync(DataPaths paths)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = paths.AgentHarnessDatabase,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }
}
