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

    /// <summary>
    /// Images a steer carries (ADR 0056). Each waits in an encrypted <c>SteerImage</c> payload
    /// that no run-image listing reads; delivery copies it into the run as a <c>UserImage</c>.
    /// The hash makes a retried send with other images an idempotency conflict.
    /// </summary>
    private const string RunSteerImageMigration = """
        CREATE TABLE IF NOT EXISTS agent_run_steer_images (
            steer_id TEXT NOT NULL REFERENCES agent_run_steers(steer_id) ON DELETE CASCADE,
            ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 63),
            payload_id TEXT NOT NULL,
            sha256 TEXT NOT NULL CHECK(length(sha256) = 64),
            PRIMARY KEY(steer_id, ordinal)
        );
        SELECT 1;
        """;

    /// <summary>
    /// Retained steers (ADR 0056 amendment). A steer still unread when the run it was sent to
    /// ends is read by the session's next root run; this row names that run, so a reopened chat
    /// attributes the steer's images to it. A table rather than a new column keeps the
    /// migration replay-safe for a development database rewound to an earlier version.
    /// </summary>
    private const string RunSteerCarryMigration = """
        CREATE TABLE IF NOT EXISTS agent_run_steer_carries (
            steer_id TEXT PRIMARY KEY REFERENCES agent_run_steers(steer_id) ON DELETE CASCADE,
            run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_agent_run_steer_carries_run
            ON agent_run_steer_carries(run_id);
        SELECT 1;
        """;
}
