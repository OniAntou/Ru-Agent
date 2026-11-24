namespace RuAgent.Harness.Persistence;

public static partial class HarnessSqliteSchema
{
    /// <summary>
    /// Steers the user sent into a working root run (ADR 0056). The text waits in an
    /// encrypted payload no event binds, so the model never reads it before delivery; a
    /// delivered steer records the event that put it into the run. A steer never delivered
    /// to a terminal run reads as returned. Replay-safe like every migration here.
    /// </summary>
    private const string RunSteerMigration = """
        CREATE TABLE IF NOT EXISTS agent_run_steers (
            steer_id TEXT PRIMARY KEY CHECK(length(steer_id) = 38),
            run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
            payload_id TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            delivered_event_id TEXT NULL,
            delivered_at_utc TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_agent_run_steers_run
            ON agent_run_steers(run_id, delivered_event_id, created_at_utc);
        SELECT 1;
        """;
}
