using System.Globalization;
using Microsoft.Data.Sqlite;
using RuBackend.Memory;
using Xunit;

namespace RuBackend.Tests.Memory;

public sealed class SqliteMemoryRepositoryTests
{
    [Fact]
    public async Task GetAsync_reads_a_fixture_record_with_utc_timestamps()
    {
        var (repository, options) = CreateRepository();
        await using (repository)
        {
            var expected = MemoryFixture.Load().ActiveRecords[0];
            await InsertRawAsync(repository, options, expected);

            var actual = await repository.GetAsync(expected.Id, CancellationToken.None);

            Assert.Equal(expected, actual);
            Assert.Equal(TimeSpan.Zero, actual!.CreatedAt.Offset);
            Assert.Equal(TimeSpan.Zero, actual.UpdatedAt.Offset);
        }
    }

    [Fact]
    public async Task ListActive_excludes_expired_unpinned_records()
    {
        var (repository, options) = CreateRepository();
        await using (repository)
        {
            var fixture = MemoryFixture.Load();
            await InsertRawAsync(repository, options, fixture.ExpiredRecords[0]);
            await InsertRawAsync(repository, options, fixture.ActiveRecords[0]);

            var records = await repository.ListActiveAsync(CancellationToken.None);

            Assert.DoesNotContain(records, record => record.Id == fixture.ExpiredRecords[0].Id);
            Assert.Contains(records, record => record.Id == fixture.ActiveRecords[0].Id);
        }
    }

    [Fact]
    public async Task Open_purges_expired_unpinned_active_rows_but_keeps_pinned_rows()
    {
        var (repository, options) = CreateRepository();
        await using (repository)
        {
            var expired = MemoryFixture.Load().ExpiredRecords[0];
            await InsertRawAsync(repository, options, expired with { Id = "expired-on-open" });
            await InsertRawAsync(repository, options, expired with { Id = "pinned-expired-on-open", Pinned = true });
        }

        await using var reopened = new SqliteMemoryRepository(options);

        Assert.Null(await reopened.GetAsync("expired-on-open", CancellationToken.None));
        Assert.NotNull(await reopened.GetAsync("pinned-expired-on-open", CancellationToken.None));
    }

    [Fact]
    public async Task Legacy_migration_marker_is_idempotent()
    {
        var (repository, _) = CreateRepository();
        await using (repository)
        {
            Assert.False(await repository.IsLegacyMigrationRecordedAsync("source-1", CancellationToken.None));
            await repository.RecordLegacyMigrationAsync("source-1", CancellationToken.None);
            await repository.RecordLegacyMigrationAsync("source-1", CancellationToken.None);

            Assert.True(await repository.IsLegacyMigrationRecordedAsync("source-1", CancellationToken.None));
        }
    }

    [Fact]
    public async Task DisposeAsync_closes_the_repository()
    {
        var (repository, _) = CreateRepository();
        await repository.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => repository.GetAsync("missing", CancellationToken.None));
    }

    [Fact]
    public async Task Corrupt_database_is_quarantined_before_rebuild()
    {
        var options = new MemorySqliteOptions
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), "ru-memory-tests", Guid.NewGuid().ToString("N"), "memory.db"),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(options.DatabasePath)!);
        File.WriteAllText(options.DatabasePath, "not a sqlite database");

        await using var rebuilt = new SqliteMemoryRepository(options);

        Assert.True(File.Exists(options.DatabasePath + ".corrupt"));
        Assert.NotNull(await rebuilt.ListActiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Upsert_returns_existing_id_for_an_active_duplicate_in_same_scope()
    {
        var (repository, _) = CreateRepository();
        await using (repository)
        {
            var candidate = new MemoryCandidate(
                MemoryKind.Preference,
                "prefers concise bilingual replies",
                "D:\\LLM",
                "duplicate evidence",
                0.92,
                0.70);

            var first = await repository.UpsertAsync(candidate, CancellationToken.None);
            var second = await repository.UpsertAsync(candidate with { Evidence = "different evidence" }, CancellationToken.None);

            Assert.Equal(first.Id, second.Id);
            Assert.Single(await repository.ListActiveAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task Upsert_supersedes_the_old_active_row_before_returning_the_new_row()
    {
        var (repository, options) = CreateRepository();
        await using (repository)
        {
            var old = MemoryFixture.Load().ActiveRecords[1] with { Id = "old-preference" };
            await InsertRawAsync(repository, options, old);

            var replacement = await repository.UpsertAsync(
                new MemoryCandidate(
                    MemoryKind.Preference,
                    "Replies should be detailed and bilingual.",
                    null,
                    "correction",
                    1.0,
                    0.9,
                    SupersedesId: old.Id),
                CancellationToken.None);

            Assert.Equal("active", replacement.Status);
            Assert.Equal("superseded", (await repository.GetAsync(old.Id, CancellationToken.None))!.Status);
            Assert.Single(await repository.ListActiveAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task Retrieve_applies_scope_filter_weighted_score_threshold_and_context_limits()
    {
        var (repository, options) = CreateRepository();
        await using (repository)
        {
            var fixture = MemoryFixture.Load();
            foreach (var record in fixture.ActiveRecords)
            {
                await InsertRawAsync(repository, options, record);
            }

            await InsertRawAsync(repository, options, new MemoryRecord(
                "different-scope",
                MemoryKind.Project,
                "project preference",
                "D:\\Other",
                "scope",
                1.0,
                1.0,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                null,
                null,
                "active",
                false));
            await InsertRawAsync(repository, options, new MemoryRecord(
                "below-threshold",
                MemoryKind.SessionSummary,
                "unrelated words",
                null,
                "low score",
                0.1,
                0.1,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                null,
                null,
                "active",
                false));

            var records = await repository.RetrieveAsync("project concise D:\\LLM", 5, 1200, CancellationToken.None);

            Assert.Equal(new[] { "project-scoped", "pinned-preference" }, records.Select(record => record.Id));
            Assert.DoesNotContain(records, record => record.Id == "different-scope");
            Assert.DoesNotContain(records, record => record.Id == "below-threshold");
            Assert.True(records.Count <= 5);
            Assert.True(string.Join("\n", records.Select(record => $"- [{record.Kind.ToString().ToLowerInvariant()} | {record.Scope ?? "global"}] {record.Content}")).Length <= 1200);
        }
    }

    [Fact]
    public async Task Retrieve_excludes_expired_unpinned_records_but_keeps_expired_pinned_records()
    {
        var (repository, options) = CreateRepository();
        await using (repository)
        {
            var expired = MemoryFixture.Load().ExpiredRecords[0];
            await InsertRawAsync(repository, options, expired with { Id = "expired-retrieval", Content = "project preference expired" });
            await InsertRawAsync(repository, options, expired with { Id = "pinned-expired-retrieval", Content = "project preference pinned", Pinned = true });

            var records = await repository.RetrieveAsync("project preference", 5, 1200, CancellationToken.None);

            Assert.DoesNotContain(records, record => record.Id == "expired-retrieval");
            Assert.Contains(records, record => record.Id == "pinned-expired-retrieval");
        }
    }

    [Fact]
    public async Task Retrieve_honors_scan_limit_and_caps_results_to_five_records_and_character_budget()
    {
        var options = new MemorySqliteOptions
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), "ru-memory-tests", Guid.NewGuid().ToString("N"), "memory.db"),
            RetrieveScanLimit = 1,
        };
        await using var repository = new SqliteMemoryRepository(options);

        var now = DateTimeOffset.UtcNow;
        await InsertRawAsync(repository, options, new MemoryRecord(
            "old-match", MemoryKind.Project, "alpha old match", null, "test", 1, 1,
            now.AddSeconds(-1), now.AddSeconds(-1), null, null, "active", false));
        await InsertRawAsync(repository, options, new MemoryRecord(
            "newer-non-match", MemoryKind.Project, "recent unrelated", null, "test", 1, 1,
            now, now, null, null, "active", false));

        Assert.Empty(await repository.RetrieveAsync("alpha", 10, 1200, CancellationToken.None));

        await InsertRawAsync(repository, options, new MemoryRecord(
            "alpha-one", MemoryKind.Project, "alpha one", null, "test", 1, 1,
            now.AddSeconds(1), now.AddSeconds(1), null, null, "active", false));
        var capped = await repository.RetrieveAsync("alpha", 10, 40, CancellationToken.None);

        Assert.InRange(capped.Count, 1, 5);
        Assert.True(string.Join("\n", capped.Select(record => $"- [{record.Kind.ToString().ToLowerInvariant()} | {record.Scope ?? "global"}] {record.Content}")).Length <= 40);
    }

    [Fact]
    public async Task Retrieve_preserves_unicode_word_tokens_for_vietnamese_text()
    {
        var (repository, options) = CreateRepository();
        await using (repository)
        {
            await InsertRawAsync(repository, options, new MemoryRecord(
                "unicode-memory", MemoryKind.Preference, "Anh thích cà phê đen", null, "test", 1, 1,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, "active", false));

            var records = await repository.RetrieveAsync("thích cà phê", 5, 1200, CancellationToken.None);

            Assert.Contains(records, record => record.Id == "unicode-memory");
        }
    }

    [Fact]
    public async Task Clear_removes_database_rows_migration_markers_and_registered_legacy_artifact()
    {
        var options = new MemorySqliteOptions
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), "ru-memory-tests", Guid.NewGuid().ToString("N"), "memory.db"),
            LegacyTextsPath = Path.Combine(Path.GetTempPath(), "ru-memory-tests", Guid.NewGuid().ToString("N"), "memory_texts.json"),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(options.LegacyTextsPath)!);
        await File.WriteAllTextAsync(options.LegacyTextsPath, "legacy");

        await using var repository = new SqliteMemoryRepository(options);
        await repository.UpsertAsync(new MemoryCandidate(MemoryKind.Profile, "clear me", null, "test", 1, 1), CancellationToken.None);
        await repository.RecordLegacyMigrationAsync("clear-source", CancellationToken.None);

        await repository.ClearAsync(CancellationToken.None);

        Assert.Empty(await repository.ListActiveAsync(CancellationToken.None));
        Assert.False(await repository.IsLegacyMigrationRecordedAsync("clear-source", CancellationToken.None));
        Assert.False(File.Exists(options.LegacyTextsPath));
    }

    private static (SqliteMemoryRepository Repository, MemorySqliteOptions Options) CreateRepository()
    {
        var options = new MemorySqliteOptions
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), "ru-memory-tests", Guid.NewGuid().ToString("N"), "memory.db"),
        };
        return (new SqliteMemoryRepository(options), options);
    }

    private static async Task InsertRawAsync(SqliteMemoryRepository repository, MemorySqliteOptions options, MemoryRecord record)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 30,
            ForeignKeys = true,
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO memories
                (id, kind, content, scope, evidence, confidence, importance, created_at,
                 updated_at, accessed_at, expires_at, status, pinned)
            VALUES ($id, $kind, $content, $scope, $evidence, $confidence, $importance,
                    $created_at, $updated_at, $accessed_at, $expires_at, $status, $pinned)
            """;
        Add(command, "$id", record.Id);
        Add(command, "$kind", record.Kind switch
        {
            MemoryKind.Profile => "profile",
            MemoryKind.Preference => "preference",
            MemoryKind.Project => "project",
            MemoryKind.Decision => "decision",
            MemoryKind.SessionSummary => "session_summary",
            _ => throw new ArgumentOutOfRangeException(),
        });
        Add(command, "$content", record.Content);
        Add(command, "$scope", record.Scope);
        Add(command, "$evidence", record.Evidence);
        Add(command, "$confidence", record.Confidence);
        Add(command, "$importance", record.Importance);
        Add(command, "$created_at", record.CreatedAt);
        Add(command, "$updated_at", record.UpdatedAt);
        Add(command, "$accessed_at", record.AccessedAt);
        Add(command, "$expires_at", record.ExpiresAt);
        Add(command, "$status", record.Status);
        Add(command, "$pinned", record.Pinned ? 1 : 0);
        await command.ExecuteNonQueryAsync();
    }

    private static void Add(SqliteCommand command, string name, object? value)
    {
        var parameter = command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        if (value is DateTimeOffset timestamp)
        {
            parameter.Value = timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        }
    }
}
