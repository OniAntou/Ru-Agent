using Microsoft.Data.Sqlite;
using RuAgent.Harness.Persistence;

namespace RuBackend.Tests;

/// <summary>
/// Releases the pooled SQLite handles a test opened against one harness database
/// file so its temp directory can be deleted on Windows. Only that file's pool is
/// cleared: the process-global <c>SqliteConnection.ClearAllPools()</c> can dispose
/// a pooled connection another test collection is still using, which is the
/// load-only <c>ObjectDisposedException</c> this suite used to hit.
/// </summary>
internal static class SqliteTestPools
{
    public static void ClearHarnessDatabase(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return;
        }

        // Opening through HarnessSqliteOptions -- the single production construction
        // path -- yields a handle whose pool key is exactly the one the backend
        // stores used, with no connection-string copy here that could drift.
        var connection = new HarnessSqliteOptions(databasePath)
            .OpenConnectionAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        connection.Dispose();
        SqliteConnection.ClearPool(connection);
    }
}
