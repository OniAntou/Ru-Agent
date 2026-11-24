using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed partial class SqliteSubagentStore
{
    private Task<bool> IsInboxBoundaryAsync(SqliteConnection connection, SqliteTransaction transaction, RunId runId, CancellationToken ct) =>
        HarnessStepBoundary.IsAtStepBoundaryAsync(connection, transaction, Payloads, runId, ct);
}
