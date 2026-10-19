namespace RuAgent.Harness.Persistence;

public static partial class HarnessSqliteSchema
{
    // Cleanup evidence must survive relation loss with the internal-run marker.
    // Execution still requires a live attempt/owner; this FK grants no authority.
    private const string SchemaVersionTwentyFourToTwentyFive = """
        CREATE TABLE agent_subagent_scopes_retained (
            run_id TEXT PRIMARY KEY REFERENCES agent_runs(run_id) ON DELETE CASCADE,
            protection_version INTEGER NOT NULL CHECK(protection_version=1),
            nonce BLOB NOT NULL CHECK(length(nonce)=12),
            ciphertext BLOB NOT NULL,
            authentication_tag BLOB NOT NULL CHECK(length(authentication_tag)=16)
        );
        INSERT INTO agent_subagent_scopes_retained(run_id,protection_version,nonce,ciphertext,authentication_tag)
            SELECT run_id,protection_version,nonce,ciphertext,authentication_tag FROM agent_subagent_scopes;
        DROP TABLE agent_subagent_scopes;
        ALTER TABLE agent_subagent_scopes_retained RENAME TO agent_subagent_scopes;
        """;
}
