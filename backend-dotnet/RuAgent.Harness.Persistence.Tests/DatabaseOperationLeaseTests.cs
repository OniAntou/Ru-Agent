using RuAgent.Harness.Persistence.Maintenance;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class DatabaseOperationLeaseTests
{
    [Fact]
    public void Online_readers_share_but_exclude_database_pair_replacement()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-maintenance-leases-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "agent_harness.db");
        try
        {
            using (DatabaseOperationLease.Acquire(path, exclusive: false))
            using (DatabaseOperationLease.Acquire(path, exclusive: false))
            {
                Assert.Equal("database_maintenance_in_progress", Assert.Throws<DatabaseMaintenanceException>(
                    () => DatabaseOperationLease.Acquire(path, exclusive: true)).ErrorCode);
            }
            using (DatabaseOperationLease.Acquire(path, exclusive: true))
            {
                Assert.Throws<DatabaseMaintenanceException>(() => DatabaseOperationLease.Acquire(path, exclusive: false));
                Assert.Throws<DatabaseMaintenanceException>(() => DatabaseOperationLease.Acquire(path, exclusive: true));
            }
            using var released = DatabaseOperationLease.Acquire(path, exclusive: false);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
