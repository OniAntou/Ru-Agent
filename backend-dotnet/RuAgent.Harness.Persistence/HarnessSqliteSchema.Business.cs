namespace RuAgent.Harness.Persistence;

public static partial class HarnessSqliteSchema
{
    private const string SchemaVersionSeventeenToEighteen = """
        CREATE TABLE IF NOT EXISTS business_migrations (name TEXT PRIMARY KEY NOT NULL);
        CREATE TABLE IF NOT EXISTS business_settings (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            document TEXT NOT NULL CHECK (json_valid(document))
        );
        CREATE TABLE IF NOT EXISTS business_sessions (
            session_id TEXT PRIMARY KEY NOT NULL,
            document BLOB NOT NULL,
            summary TEXT NOT NULL CHECK (json_valid(summary)),
            updated_at_utc TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS business_sessions_updated ON business_sessions(updated_at_utc DESC, session_id);
        CREATE TABLE IF NOT EXISTS business_activity (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            event_id TEXT UNIQUE,
            timestamp REAL NOT NULL,
            document TEXT NOT NULL CHECK (json_valid(document))
        );
        CREATE INDEX IF NOT EXISTS business_activity_timestamp ON business_activity(timestamp DESC, sequence DESC);
        """;
}
