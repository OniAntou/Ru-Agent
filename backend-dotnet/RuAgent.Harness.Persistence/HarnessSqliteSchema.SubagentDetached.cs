namespace RuAgent.Harness.Persistence;

public static partial class HarnessSqliteSchema
{
    /// <summary>
    /// Schema v43: detached children (ADR 0045). A row marks a child that keeps running after its
    /// creating root ends normally. It cascades with the child, so session deletion needs no new
    /// step, and a table rather than a column keeps the migration replay-safe for a rewound
    /// development database.
    /// </summary>
    private const string SubagentDetachedMigration = """
        CREATE TABLE IF NOT EXISTS agent_subagent_detached (
            agent_id TEXT PRIMARY KEY REFERENCES agent_subagents(agent_id) ON DELETE CASCADE
        );
        SELECT 1;
        """;
}
