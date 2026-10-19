namespace RuAgent.Harness.Persistence;

public static partial class HarnessSqliteSchema
{
    private const string SchemaVersionTwentyFiveToTwentySix = """
        CREATE TABLE IF NOT EXISTS agent_subagent_workspace_allocations (
            root_run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
            agent_id TEXT NOT NULL UNIQUE,
            snapshot_id TEXT NOT NULL UNIQUE,
            stage INTEGER NOT NULL CHECK(stage BETWEEN 0 AND 4),
            protection_version INTEGER NULL,
            nonce BLOB NULL,
            ciphertext BLOB NULL,
            authentication_tag BLOB NULL,
            PRIMARY KEY(root_run_id,agent_id),
            CHECK((stage=0 AND protection_version IS NULL AND nonce IS NULL AND ciphertext IS NULL AND authentication_tag IS NULL)
                OR (stage>0 AND protection_version IS NOT NULL AND protection_version=1
                    AND nonce IS NOT NULL AND length(nonce)=12 AND ciphertext IS NOT NULL
                    AND authentication_tag IS NOT NULL AND length(authentication_tag)=16))
        );
        """;
}
