using Microsoft.Data.Sqlite;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class SchemaVersionCompatibilityTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(12)]
    [InlineData(17)]
    [InlineData(99)]
    public async Task Unsupported_schema_is_rejected_without_touching_owned_rows(int version)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $$"""
            INSERT INTO business_settings(id, document) VALUES (1, '{}');
            PRAGMA user_version = {{version}};
            """;
        await command.ExecuteNonQueryAsync();

        var health = await HarnessSqliteSchema.InitializeAsync(fixture.Options, default);

        Assert.False(health.IsAvailable);
        Assert.Equal("harness_schema_incompatible", health.ErrorCode);
        command.CommandText = "SELECT document FROM business_settings WHERE id=1";
        Assert.Equal("{}", await command.ExecuteScalarAsync());
        command.CommandText = "PRAGMA user_version";
        Assert.Equal((long)version, await command.ExecuteScalarAsync());
    }
}
