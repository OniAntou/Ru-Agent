using System.Text.Json;
using System.Text.Json.Serialization;
using RuBackend.Memory;
using Xunit;

namespace RuBackend.Tests.Memory;

public sealed class MemoryParityFixtureTests
{
    [Fact]
    public void Fixture_contains_the_memory_contract_cases()
    {
        var fixture = MemoryFixture.Load();

        Assert.Equal(
            new[] { MemoryKind.Project, MemoryKind.Preference, MemoryKind.Decision },
            fixture.ActiveRecords.Select(record => record.Kind));
        Assert.Equal("2026-08-10T00:00:00+00:00", fixture.ActiveRecords[0].CreatedAt.ToString("yyyy-MM-ddTHH:mm:sszzz"));
        Assert.Equal("2026-08-10T00:00:00.0000000+00:00", fixture.ActiveRecords[0].CreatedAt.ToString("O"));
        Assert.Equal(new[] { "project-scoped", "pinned-preference" }, fixture.RetrievalCases[0].ExpectedIds);

        var candidate = Assert.Single(fixture.Candidates, candidate => candidate.Id == "duplicate-candidate");
        Assert.Equal(MemoryKind.Preference, candidate.Kind);
        Assert.Equal("prefers concise bilingual replies", candidate.Content);
        Assert.Equal("duplicate", candidate.Evidence);
        Assert.Equal(0.92, candidate.Confidence);
        Assert.Equal(0.70, candidate.Importance);
        Assert.Null(candidate.ExpiresAt);
        Assert.Equal("project-scoped", fixture.ActiveRecords[0].Id);
        Assert.Equal("D:\\LLM", fixture.ActiveRecords[0].Scope);
        Assert.True(fixture.ActiveRecords[1].Pinned);
        Assert.Equal("expired-record", fixture.ExpiredRecords[0].Id);
    }
}

internal sealed class MemoryFixture
{
    public required IReadOnlyList<MemoryRecord> ActiveRecords { get; init; }
    public required IReadOnlyList<MemoryRecord> ExpiredRecords { get; init; }
    public required IReadOnlyList<FixtureCandidate> Candidates { get; init; }
    public required IReadOnlyList<RetrievalCase> RetrievalCases { get; init; }

    public static MemoryFixture Load()
    {
        var fixturePath = FindFixturePath();
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());

        return JsonSerializer.Deserialize<MemoryFixture>(File.ReadAllText(fixturePath), options)
            ?? throw new InvalidOperationException($"Unable to deserialize memory fixture: {fixturePath}");
    }

    private static string FindFixturePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "fixtures", "memory-parity.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate tests/fixtures/memory-parity.json.");
    }
}

internal sealed record FixtureCandidate(
    string Id,
    MemoryKind Kind,
    string Content,
    string? Scope,
    string Evidence,
    double Confidence,
    double Importance,
    DateTimeOffset? ExpiresAt,
    string? SupersedesId);

internal sealed record RetrievalCase(
    string Query,
    string? Scope,
    IReadOnlyList<string> ExpectedIds);
