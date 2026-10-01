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
    /// <summary>
    /// Clears the pool for <paramref name="databasePath"/>. Returns <see langword="true"/>
    /// when the pool was cleared, <see langword="false"/> when there was no file or the
    /// attempt failed. This runs from teardown, so a failure is logged and swallowed
    /// rather than thrown over the test result it runs after.
    /// </summary>
    public static bool ClearHarnessDatabase(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return false;
        }

        try
        {
            // Opening through HarnessSqliteOptions -- the single production construction
            // path -- yields a handle whose pool key is exactly the one the backend
            // stores used, with no connection-string copy here that could drift.
            var connection = new HarnessSqliteOptions(databasePath)
                .OpenConnectionAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            connection.Dispose();
            SqliteConnection.ClearPool(connection);
            return true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"SqliteTestPools: could not clear '{databasePath}': {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }
}
