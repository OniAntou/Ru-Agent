using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RuBackend.Memory;
using RuBackend.Tests.Fakes;
using Xunit;

namespace RuBackend.Tests.Memory;

public sealed class MemoryEndpointTests
{
    [Fact]
    public async Task GetMemory_returns_only_the_WinUI_dto_fields()
    {
        var record = Record("memory-1", MemoryKind.Preference, "Anh thích cà phê.");
        var service = CreateTemporaryMemoryService([record], out _);
        using var factory = new BackendWebApplicationFactory(memoryService: service);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/memory");
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            ["id", "text", "category", "created_at"],
            payload[0].EnumerateObject().Select(property => property.Name).ToArray());
        Assert.True(payload[0].TryGetProperty("id", out _));
        Assert.True(payload[0].TryGetProperty("text", out _));
        Assert.True(payload[0].TryGetProperty("category", out _));
        Assert.True(payload[0].TryGetProperty("created_at", out _));
        Assert.False(payload[0].TryGetProperty("evidence", out _));
    }

    [Fact]
    public async Task Unauthorized_memory_access_is_rejected_before_the_service_runs()
    {
        var service = CreateTemporaryMemoryService(
            [Record("memory-1", MemoryKind.Preference, "private")],
            out var repository);
        using var factory = new BackendWebApplicationFactory(memoryService: service);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/memory");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, repository.ListActiveCount);
    }

    [Fact]
    public async Task Direct_endpoint_serves_memory_without_calling_the_runtime_worker()
    {
        var service = CreateTemporaryMemoryService(
            [Record("memory-1", MemoryKind.Project, "local")],
            out _);
        var runtimeWorker = new StubRuntimeWorkerClient(
            new HttpRequestException("runtime worker must not be called"));
        using var factory = new BackendWebApplicationFactory(
            memoryService: service,
            runtimeWorkerClient: runtimeWorker);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/memory");
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("memory-1", payload[0].GetProperty("id").GetString());
        Assert.Empty(runtimeWorker.Requests);
    }

    [Fact]
    public async Task AspNet_delete_returns_the_existing_success_json()
    {
        var service = CreateTemporaryMemoryService(
            [Record("memory-1", MemoryKind.Project, "local")],
            out var repository);
        using var factory = new BackendWebApplicationFactory(memoryService: service);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.DeleteAsync("/memory");
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("success", payload.GetProperty("status").GetString());
        Assert.Equal(1, repository.ClearCount);
    }

    [Fact]
    public async Task AspNet_delete_returns_the_busy_error_without_exception_details()
    {
        var service = CreateTemporaryMemoryService(
            [Record("memory-1", MemoryKind.Project, "local")],
            out var repository);
        repository.ClearError = new MemoryBusyException();
        using var factory = new BackendWebApplicationFactory(memoryService: service);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.DeleteAsync("/memory");
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("error", payload.GetProperty("status").GetString());
        Assert.Contains("Memory is busy", payload.GetProperty("message").GetString());
        Assert.DoesNotContain(nameof(MemoryBusyException), payload.ToString(), StringComparison.Ordinal);
    }

    private static MemoryService CreateTemporaryMemoryService(
        IReadOnlyList<MemoryRecord> records,
        out TemporaryMemoryRepository repository)
    {
        repository = new TemporaryMemoryRepository(records);
        var directory = Path.Combine(Path.GetTempPath(), "ru-memory-endpoint-tests", Guid.NewGuid().ToString("N"));
        return new MemoryService(
            repository,
            new MemoryPolicy(),
            new MemorySqliteOptions
            {
                DatabasePath = Path.Combine(directory, "memory.db"),
            });
    }

    private static MemoryRecord Record(string id, MemoryKind kind, string content) =>
        new(
            id,
            kind,
            content,
            null,
            "synthetic evidence",
            0.9,
            0.8,
            DateTimeOffset.Parse("2026-08-10T00:00:00+00:00"),
            DateTimeOffset.Parse("2026-08-10T00:00:00+00:00"),
            null,
            null,
            "active",
            false);

    private sealed class TemporaryMemoryRepository : IMemoryRepository
    {
        private readonly IReadOnlyList<MemoryRecord> _records;

        public TemporaryMemoryRepository(IReadOnlyList<MemoryRecord> records)
        {
            _records = records;
        }

        public int ListActiveCount { get; private set; }
        public int ClearCount { get; private set; }
        public Exception? ClearError { get; set; }

        public Task<MemoryRecord?> GetAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(_records.FirstOrDefault(record => record.Id == id));

        public Task<IReadOnlyList<MemoryRecord>> ListActiveAsync(CancellationToken cancellationToken)
        {
            ListActiveCount++;
            return Task.FromResult(_records);
        }

        public Task<IReadOnlyList<MemoryRecord>> RetrieveAsync(
            string query,
            int limit,
            int maxChars,
            CancellationToken cancellationToken) =>
            Task.FromResult(_records);

        public Task<MemoryRecord> UpsertAsync(MemoryCandidate candidate, CancellationToken cancellationToken) =>
            Task.FromResult(Record(
                Guid.NewGuid().ToString("D"),
                candidate.Kind,
                candidate.Content));

        public Task ClearAsync(CancellationToken cancellationToken)
        {
            ClearCount++;
            if (ClearError is not null)
            {
                return Task.FromException(ClearError);
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
