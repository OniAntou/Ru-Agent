namespace RuAgent.Harness.Persistence;

public static partial class HarnessSqliteSchema
{
    private const string SchemaVersionTwentyTwoToTwentyThree = """
        CREATE TABLE IF NOT EXISTS agent_subagent_change_sets (
            run_id TEXT PRIMARY KEY REFERENCES agent_subagent_attempts(run_id) ON DELETE CASCADE,
            protection_version INTEGER NOT NULL CHECK(protection_version=1),
            nonce BLOB NOT NULL CHECK(length(nonce)=12),
            ciphertext BLOB NOT NULL CHECK(length(ciphertext)<=16777216),
            authentication_tag BLOB NOT NULL CHECK(length(authentication_tag)=16)
        );
        CREATE TABLE IF NOT EXISTS agent_subagent_change_chunks (
            run_id TEXT NOT NULL REFERENCES agent_subagent_change_sets(run_id) ON DELETE CASCADE,
            file_index INTEGER NOT NULL CHECK(file_index>=0 AND file_index<50000),
            chunk_index INTEGER NOT NULL CHECK(chunk_index>=0 AND chunk_index<32),
            protection_version INTEGER NOT NULL CHECK(protection_version=1),
            nonce BLOB NOT NULL CHECK(length(nonce)=12),
            ciphertext BLOB NOT NULL CHECK(length(ciphertext)<=1048576),
            authentication_tag BLOB NOT NULL CHECK(length(authentication_tag)=16),
            PRIMARY KEY(run_id,file_index,chunk_index)
        );
        """;
}
