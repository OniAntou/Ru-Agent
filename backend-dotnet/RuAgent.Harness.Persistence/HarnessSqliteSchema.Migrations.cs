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
            new(31, 32, ProviderIntentMigration),
        ];
    }
}
