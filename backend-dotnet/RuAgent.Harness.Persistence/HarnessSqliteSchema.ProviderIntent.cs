namespace RuAgent.Harness.Persistence;

public static partial class HarnessSqliteSchema
{
    private const string ProviderIntentMigration = """
        CREATE TABLE agent_run_provider_intents (
            run_id TEXT PRIMARY KEY REFERENCES agent_runs(run_id) ON DELETE CASCADE,
            backend TEXT NOT NULL CHECK(length(backend) BETWEEN 1 AND 128),
            model TEXT NOT NULL CHECK(length(model) BETWEEN 1 AND 256)
        );
        """;
}
