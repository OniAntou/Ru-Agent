namespace RuAgent.Harness.Persistence;

public static partial class HarnessSqliteSchema
{
    // Retain leases, grants, run state and existing shared capacity choices.
    // Old exclusive/single-slot workspaces adopt the always-parallel default.
    private const string SchemaVersionEighteenToNineteen = """
        UPDATE workspace_coordinators
        SET mode = 'shared',
            max_concurrent_runs = CASE WHEN max_concurrent_runs < 2 THEN 32 ELSE max_concurrent_runs END,
            policy_json = json_set(policy_json,
                '$.mode', 'shared',
                '$.max_concurrent_runs', CASE WHEN max_concurrent_runs < 2 THEN 32 ELSE max_concurrent_runs END),
            version = version + 1,
            updated_at_utc = strftime('%Y-%m-%dT%H:%M:%f+00:00', 'now')
        WHERE mode <> 'shared' OR max_concurrent_runs < 2;
        """;
}
