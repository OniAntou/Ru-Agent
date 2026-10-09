using System.Net;
using RuBackend.Business;
using RuBackend.Business.Settings;
using RuBackend.Providers;
using Xunit;

namespace RuBackend.Tests.Providers;

/// <summary>
/// A failed catalog read stays reported while the retry throttle holds, so the
/// snapshot has to say when the failure was observed. Without it a fifteen-second-old
/// rejection reads exactly like one happening now.
/// </summary>
public sealed class CatalogFailureAgeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-catalog-age-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_cached_failure_carries_when_it_happened_and_clears_once_a_read_succeeds()
    {
        var cacheFile = await SeedAsync();
        var store = new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root));
        await store.SaveAsync((await store.ReadAsync(default)) with
        { Backend = "models.dev:openrouter", Model = "stepfun/step-3.7-flash" }, default);

        var handler = new ToggleHandler();
        var clock = new Clock();
        var catalog = new ProviderCatalogService(new HttpClient(handler), new AlwaysKey(), clock, cacheFile);

        handler.FailNext = true;
        var failed = await catalog.GetAsync(await store.ReadAsync(default), false, default);
        Assert.Equal("error", failed.Status);
        Assert.Equal("catalog_http_error", failed.ErrorCode);
        Assert.NotNull(failed.ErrorAtUtc);

        // Inside the retry throttle the same failure is served again, so the timestamp
        // is the only thing distinguishing it from a fresh one. Note that an explicit
        // refresh does not bypass the throttle: only time does.
        var repeated = await catalog.GetAsync(await store.ReadAsync(default), true, default);
        Assert.Equal("catalog_http_error", repeated.ErrorCode);
        Assert.Equal(failed.ErrorAtUtc, repeated.ErrorAtUtc);

        // Past the throttle the read is retried, and a success must clear the date.
        clock.Advance(TimeSpan.FromSeconds(31));
        var recovered = await catalog.GetAsync(await store.ReadAsync(default), false, default);
        Assert.Equal("ok", recovered.Status);
        Assert.Null(recovered.ErrorCode);
        Assert.Null(recovered.ErrorAtUtc);
    }

    [Fact]
    public async Task A_signed_out_provider_still_dates_its_failure()
    {
        var cacheFile = await SeedAsync();
        var store = new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root));
        await store.SaveAsync((await store.ReadAsync(default)) with
        { Backend = "models.dev:openrouter", Model = "stepfun/step-3.7-flash" }, default);

        var catalog = new ProviderCatalogService(
            new HttpClient(new ToggleHandler()), new NoKey(), TimeProvider.System, cacheFile);

        var snapshot = await catalog.GetAsync(await store.ReadAsync(default), false, default);
        Assert.Equal("catalog_unauthorized", snapshot.ErrorCode);
        // Signed out is the current state, not a stale failure, but the field is still
        // populated so consumers never have to special-case which code carries a date.
        Assert.NotNull(snapshot.ErrorAtUtc);
    }

    private async Task<string> SeedAsync()
    {
        Directory.CreateDirectory(_root);
        var entries = Enumerable.Range(0, 60).Select(i => $"\"prov{i:000}\":{{\"api\":\"https://prov{i:000}.example/v1\"}}");
        var providers = "{\"openrouter\":{\"api\":\"https://openrouter.ai/api/v1\"}," + string.Join(",", entries) + "}";
        var cacheFile = Path.Combine(_root, "models-dev.json");
        await File.WriteAllTextAsync(cacheFile,
            $$"""{"fetched_at":"2099-01-01T00:00:00+00:00","payload":{{providers}}}""");
        return cacheFile;
    }

    public void Dispose()
    {
        SqliteTestPools.ClearHarnessDatabase(DataPaths.Resolve(_root, _root, _root, _root).AgentHarnessDatabase);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class AlwaysKey : ICredentialStore
    {
        public Task<string?> GetAsync(string s, string a, CancellationToken c) => Task.FromResult<string?>("key");
        public Task SetAsync(string s, string a, string v, CancellationToken c) => throw new NotSupportedException();
        public Task DeleteAsync(string s, string a, CancellationToken c) => throw new NotSupportedException();
    }

    private sealed class NoKey : ICredentialStore
    {
        public Task<string?> GetAsync(string s, string a, CancellationToken c) => Task.FromResult<string?>(null);
        public Task SetAsync(string s, string a, string v, CancellationToken c) => throw new NotSupportedException();
        public Task DeleteAsync(string s, string a, CancellationToken c) => throw new NotSupportedException();
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class ToggleHandler : HttpMessageHandler
    {
        public bool FailNext;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c)
        {
            if (FailNext)
            {
                FailNext = false;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"stepfun/step-3.7-flash"}]}"""),
            });
        }
    }
}
