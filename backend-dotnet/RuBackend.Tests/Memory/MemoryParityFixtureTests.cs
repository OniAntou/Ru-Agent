using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
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
            new[]
            {
                MemoryKind.Profile,
                MemoryKind.Preference,
                MemoryKind.Project,
                MemoryKind.Decision,
                MemoryKind.SessionSummary,
            },
            Enum.GetValues<MemoryKind>());

        Assert.Equal(
            new[]
            {
                new MemoryRecord(
                    "project-scoped",
                    MemoryKind.Project,
                    "Ru memory ownership migration is scoped to the D:\\LLM project.",
                    "D:\\LLM",
                    "synthetic project fixture",
                    0.98,
                    0.90,
                    Utc("2026-08-10T00:00:00+00:00"),
                    Utc("2026-08-10T00:00:00+00:00"),
                    Utc("2026-08-10T00:00:00+00:00"),
                    null,
                    "active",
                    false),
                new MemoryRecord(
                    "pinned-preference",
                    MemoryKind.Preference,
                    "Replies should remain concise and bilingual.",
                    null,
                    "synthetic pinned fixture",
                    0.95,
                    0.85,
                    Utc("2026-08-09T00:00:00+00:00"),
                    Utc("2026-08-09T00:00:00+00:00"),
                    null,
                    null,
                    "active",
                    true),
                new MemoryRecord(
                    "decision-record",
                    MemoryKind.Decision,
                    "ASP.NET Core is the future memory owner.",
                    null,
                    "synthetic decision fixture",
                    0.90,
                    0.80,
                    Utc("2026-08-08T00:00:00+00:00"),
                    Utc("2026-08-08T00:00:00+00:00"),
                    null,
                    null,
                    "active",
                    false),
            },
            fixture.ActiveRecords);

        Assert.Equal(
            new[]
            {
                new MemoryRecord(
                    "expired-record",
                    MemoryKind.SessionSummary,
                    "This synthetic memory has expired.",
                    null,
                    "synthetic expiry fixture",
                    0.60,
                    0.40,
                    Utc("2026-08-01T00:00:00+00:00"),
                    Utc("2026-08-01T00:00:00+00:00"),
                    null,
                    Utc("2026-08-09T00:00:00+00:00"),
                    "active",
                    false),
            },
            fixture.ExpiredRecords);

        Assert.Equal(new[] { "project-scoped", "pinned-preference" }, fixture.RetrievalCases[0].ExpectedIds);

        var duplicate = Assert.Single(fixture.Candidates, candidate => candidate.Id == "duplicate-candidate");
        var duplicateCopy = Assert.Single(fixture.Candidates, candidate => candidate.Id == "duplicate-candidate-copy");
        Assert.Equal(MemoryKind.Preference, duplicate.Kind);
        Assert.Equal(duplicate.Kind, duplicateCopy.Kind);
        Assert.Equal("prefers concise bilingual replies", duplicate.Content);
        Assert.Equal(duplicate.Content, duplicateCopy.Content);
        Assert.Equal("duplicate", duplicate.Evidence);
        Assert.Equal(duplicate.Evidence, duplicateCopy.Evidence);
        Assert.Equal(0.92, duplicate.Confidence);
        Assert.Equal(duplicate.Confidence, duplicateCopy.Confidence);
        Assert.Equal(0.70, duplicate.Importance);
        Assert.Equal(duplicate.Importance, duplicateCopy.Importance);
        Assert.Null(duplicate.ExpiresAt);
        Assert.Null(duplicateCopy.ExpiresAt);
        Assert.Null(duplicate.SupersedesId);
        Assert.Equal("duplicate-candidate", duplicateCopy.SupersedesId);
    }

    private static DateTimeOffset Utc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
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
