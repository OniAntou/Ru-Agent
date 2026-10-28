namespace RuAgent.Harness.Persistence.Maintenance;

/// <summary>Exclusive, OS-released ownership of one Ru business database profile.</summary>
public sealed class DatabaseOwnerLease : IDisposable
{
    private readonly FileStream _stream;
    private bool _disposed;

    private DatabaseOwnerLease(FileStream stream) => _stream = stream;

    public static string GetLockPath(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        return Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(databasePath))!,
            ".database-owner.lock");
    }

    public static DatabaseOwnerLease Acquire(string databasePath)
    {
        var lockPath = GetLockPath(databasePath);
        var directory = Path.GetDirectoryName(lockPath)!;
        try
        {
            Directory.CreateDirectory(directory);
            return new DatabaseOwnerLease(new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None));
        }
        catch (IOException exception)
        {
            throw new DatabaseMaintenanceException("database_profile_in_use", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new DatabaseMaintenanceException("database_owner_lock_unavailable", exception);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
    }
}
