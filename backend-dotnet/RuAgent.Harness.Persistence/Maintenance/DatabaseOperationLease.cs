using System;
using System.IO;

namespace RuAgent.Harness.Persistence.Maintenance;

/// <summary>Coordinates online readers with offline database/key replacement.</summary>
public sealed class DatabaseOperationLease : IDisposable
{
    private readonly FileStream _stream;
    private DatabaseOperationLease(FileStream stream) => _stream = stream;

    public static DatabaseOperationLease Acquire(string databasePath, bool exclusive)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
        Directory.CreateDirectory(directory);
        try
        {
            return new DatabaseOperationLease(new FileStream(
                Path.Combine(directory, ".database-operation.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, exclusive ? FileShare.None : FileShare.ReadWrite));
        }
        catch (IOException exception)
        {
            throw new DatabaseMaintenanceException("database_maintenance_in_progress", exception);
        }
    }

    public void Dispose() => _stream.Dispose();
}
