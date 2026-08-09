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
