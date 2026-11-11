namespace RuAgent.Harness.Persistence;

public static partial class HarnessSqliteSchema
{
    private sealed record SchemaMigration(int From, int To, IEnumerable<string> Statements)
    {
        public SchemaMigration(int from, int to, string sql) : this(from, to, new[] { sql }) { }
    }

    // One catalog owns supported sources and the target version. Add a future
    // upgrade here once; Initialize applies its selected suffix atomically.
    // The unreleased v19-v28 fragments are consolidated into one transaction
    // ending at v29, including when upgrading a released v18 database. Retaining
    // their source fences also allows current development profiles to upgrade.
    // Lazy construction avoids static-field ordering across partial source files.
    private static readonly Lazy<SchemaMigration[]> MigrationCatalog = new(CreateMigrationCatalog);

    private static SchemaMigration[] CreateMigrationCatalog()
    {
        string[] unreleased =
        [
            SchemaVersionEighteenToNineteen, SchemaVersionNineteenToTwenty,
            SchemaVersionTwentyToTwentyOne, SchemaVersionTwentyOneToTwentyTwo,
            SchemaVersionTwentyTwoToTwentyThree, SchemaVersionTwentyThreeToTwentyFour,
            SchemaVersionTwentyFourToTwentyFive, SchemaVersionTwentyFiveToTwentySix,
            SchemaVersionTwentySixToTwentySeven, SchemaVersionTwentySevenToTwentyEight,
        ];
        return
        [
            // One canonical upgrade for the released baseline. No intermediate
            // version is written, including when a late statement fails.
            new(18, 29, unreleased),
            // Bounded development bridges share lazy suffixes, not copied SQL
            // strings or handwritten combinations. Their fixtures are frozen.
            .. Enumerable.Range(1, unreleased.Length - 1)
                .Select(index => new SchemaMigration(18 + index, 29, unreleased.Skip(index))),
            new(28, 29, "SELECT 1;"),
            new(29, 30, SessionHistorySchemaMigration),
            new(30, 31, ContextProjectionMigration),
            new(31, 32, """
                CREATE INDEX ix_agent_events_execution_request
                    ON agent_events(run_id, execution_id, tool_call_id, provider_call_id, sequence)
                    WHERE kind = 'tool.execution_requested';
                SELECT 1;
                """),
            new(32, 33, ProviderIntentMigration),
            new(33, 34, """
                ALTER TABLE business_session_source_receipts ADD COLUMN source_role TEXT;
                ALTER TABLE business_session_source_receipts ADD COLUMN source_run_id TEXT;
                ALTER TABLE business_session_messages ADD COLUMN image_attachment_count INTEGER NOT NULL DEFAULT 0 CHECK(image_attachment_count BETWEEN 0 AND 64);
                UPDATE business_session_messages
                SET image_attachment_count=COALESCE(image_counts.image_count, 0)
                FROM (SELECT run_id, MIN(64, COUNT(*)) AS image_count FROM agent_payloads
                    WHERE kind=11 AND (audience & 2) != 0 GROUP BY run_id) AS image_counts
                WHERE business_session_messages.role='user'
                    AND business_session_messages.source_run_id=image_counts.run_id;
                UPDATE business_session_source_receipts
                SET source_role=(SELECT role FROM business_session_messages m
                        WHERE m.session_id=business_session_source_receipts.session_id
                            AND m.source_message_id=business_session_source_receipts.source_message_id LIMIT 1),
                    source_run_id=(SELECT source_run_id FROM business_session_messages m
                        WHERE m.session_id=business_session_source_receipts.session_id
                            AND m.source_message_id=business_session_source_receipts.source_message_id LIMIT 1);
                UPDATE business_session_source_receipts
                SET source_role=CASE o.role WHEN 0 THEN 'user' WHEN 1 THEN 'ai' END,
                    source_run_id=o.run_id
                FROM agent_conversation_outbox o
                WHERE business_session_source_receipts.source_message_id=o.intent_id
                    AND business_session_source_receipts.session_id=o.session_id
                    AND business_session_source_receipts.source_run_id IS NULL;
                CREATE INDEX ix_business_session_source_receipts_run_role
                    ON business_session_source_receipts(session_id, source_role, source_run_id);
                SELECT 1;
                """),
            new(34, 35, SubagentFamilyBudgetMigration),
            new(35, 36, SubagentFamilyBudgetUnboundedMigration),
        ];
    }
}
