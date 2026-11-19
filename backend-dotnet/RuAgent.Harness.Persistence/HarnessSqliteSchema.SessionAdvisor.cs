namespace RuAgent.Harness.Persistence;

public static partial class HarnessSqliteSchema
{
    // The advisor a session's new root runs pin (ADR 0047, amended). No row means the
    // session has no advisor, which is where every new session starts. IF NOT EXISTS keeps
    // the step repeatable for development databases rewound to an older user_version.
    private const string SessionAdvisorMigration = """
        CREATE TABLE IF NOT EXISTS agent_session_advisors (
            session_id TEXT PRIMARY KEY CHECK(length(session_id) BETWEEN 1 AND 128),
            backend TEXT NOT NULL CHECK(length(backend) BETWEEN 1 AND 128),
            model TEXT NOT NULL CHECK(length(model) BETWEEN 1 AND 256),
            updated_at_utc TEXT NOT NULL
        );
        SELECT 1;
        """;
}
