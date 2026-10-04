using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuBackend.Business;
using RuBackend.Business.Settings;
using RuBackend.Providers;
using RuBackend.Tests;
using RuBackend.Tests.Harness;
using Xunit;

namespace RuBackend.Tests.Providers;

public sealed class AgentProviderSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-run-provider-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Same_run_keeps_model_key_and_effort_after_settings_change()
    {
        var store = new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root));
        var initial = (await store.ReadAsync(default)) with
        { Backend = "Custom OpenAI", CustomModel = "a", CustomUrl = "https://provider.invalid/v1" };
        await store.SaveAsync(ProviderReasoningPreferences.Save(initial, "high", ProviderSelection.From(initial)), default);
        await using var harness = await BackendHarnessTestFixture.CreateAsync();
        var run = await harness.SeedRunAsync();
        var credentials = new Credentials();
        var catalog = new ProviderCatalogService(new HttpClient(new Handler()), credentials, TimeProvider.System);
        var selections = new SqliteAgentRunProviderSelectionStore(harness.Options);
        using var factory = new AgentProviderSnapshotFactory(
            store, credentials, catalog, selections, (_, _) => Task.FromResult(false));

        var first = await ReadAsync(factory, run.Run.RunId);
        var metadataResolver = new AgentRunModelMetadataResolver(factory, selections);
        var firstMetadata = await metadataResolver.ResolveAsync(run.Run.RunId, default);
        Assert.Equal("a", first.Model);
        Assert.Equal("a", firstMetadata.ModelLabel);
        Assert.Equal("chat_completions", firstMetadata.Protocol);
        Assert.Equal(ProviderToolCallingMode.Native, firstMetadata.ToolCallingMode);
        Assert.Equal(222_222, firstMetadata.ContextWindowTokens);
        Assert.Equal("provider_catalog", firstMetadata.ContextWindowSource);
        Assert.Equal(8_192, firstMetadata.OutputTokenReserve);
        Assert.Equal("high", first.RequestOptions?["reasoning_effort"]?.GetValue<string>());
        Assert.Equal("key-one", first.Credential);

        credentials.Key = "key-two";
        await store.SaveAsync((await store.ReadAsync(default)) with { CustomModel = "b" }, default);
        var stillFirst = await ReadAsync(factory, run.Run.RunId);
        var stillFirstMetadata = await metadataResolver.ResolveAsync(run.Run.RunId, default);
        Assert.Equal("a", stillFirst.Model);
        Assert.Equal(firstMetadata, stillFirstMetadata);
        Assert.Equal("key-one", stillFirst.Credential);
        Assert.Equal("high", stillFirst.RequestOptions?["reasoning_effort"]?.GetValue<string>());
        Assert.Empty(Directory.GetFiles(_root, "provider-bootstrap-*.bin"));
    }

    [Fact]
    public async Task Recreated_factory_resumes_run_with_durable_provider_selection()
    {
        var store = new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root));
        var initial = (await store.ReadAsync(default)) with
        { Backend = "Custom OpenAI", CustomModel = "a", CustomUrl = "https://provider-a.invalid/v1" };
        await store.SaveAsync(initial, default);
        await using var harness = await BackendHarnessTestFixture.CreateAsync();
        var run = await harness.SeedRunAsync();
        var credentials = new Credentials();
        var catalog = new ProviderCatalogService(new HttpClient(new Handler()), credentials, TimeProvider.System);
        var runId = run.Run.RunId;
        var selections = new SqliteAgentRunProviderSelectionStore(harness.Options);
        ProviderBootstrapData pinned;
        using (var firstFactory = new AgentProviderSnapshotFactory(
                   store, credentials, catalog, selections, (_, _) => Task.FromResult(false)))
        {
            pinned = await firstFactory.GetAsync(runId, default);
            var firstMetadata = await new AgentRunModelMetadataResolver(firstFactory, selections)
                .ResolveAsync(runId, default);
            Assert.Equal("a", firstMetadata.ModelLabel);
            Assert.Equal(222_222, firstMetadata.ContextWindowTokens);
        }

        await store.SaveAsync((await store.ReadAsync(default)) with
        { CustomModel = "b", CustomUrl = "https://provider-b.invalid/v1" }, default);
        using var restartedFactory = new AgentProviderSnapshotFactory(
            store, credentials, catalog, selections, (_, _) => Task.FromResult(false));

        var resumed = await restartedFactory.GetAsync(runId, default);
        var resumedMetadata = await new AgentRunModelMetadataResolver(restartedFactory, selections)
            .ResolveAsync(runId, default);

        Assert.Equal(pinned.Backend, resumed.Backend);
        Assert.Equal(pinned.Protocol, resumed.Protocol);
        Assert.Equal(pinned.Endpoint, resumed.Endpoint);
        Assert.Equal(pinned.Model, resumed.Model);
        Assert.Equal("a", resumedMetadata.ModelLabel);
        Assert.Equal(222_222, resumedMetadata.ContextWindowTokens);
    }

    [Fact]
    public async Task Recreated_openai_pin_rejects_anthropic_credential_account_before_reads()
    {
        var settings = new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root));
        await settings.SaveAsync((await settings.ReadAsync(default)) with
        {
            Backend = "OpenAI",
            OpenAiModel = "gpt-5.6-sol",
        }, default);
        await using var harness = await BackendHarnessTestFixture.CreateAsync();
        var run = await harness.SeedRunAsync();
        var firstCredentials = new TrackingCredentials();
        var firstCatalogHandler = new TrackingCatalogHandler();
        var firstCatalog = new ProviderCatalogService(
            new HttpClient(firstCatalogHandler), firstCredentials, TimeProvider.System);
        var selections = new SqliteAgentRunProviderSelectionStore(harness.Options);
        using (var firstFactory = new AgentProviderSnapshotFactory(
                   settings,
                   firstCredentials,
                   firstCatalog,
                   selections,
                   (_, _) => Task.FromResult(false)))
        {
            _ = await firstFactory.GetAsync(run.Run.RunId, default);
        }

        await using (var connection = new SqliteConnection(
                         $"Data Source={harness.Options.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE agent_run_provider_selections SET account = $account WHERE run_id = $run_id";
            update.Parameters.AddWithValue("$account", "anthropic_api_key");
            update.Parameters.AddWithValue("$run_id", run.Run.RunId.Value);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        var resumedCredentials = new TrackingCredentials();
        var resumedCatalogHandler = new TrackingCatalogHandler();
        var resumedCatalog = new ProviderCatalogService(
            new HttpClient(resumedCatalogHandler), resumedCredentials, TimeProvider.System);
        using var restartedFactory = new AgentProviderSnapshotFactory(
            settings,
            resumedCredentials,
            resumedCatalog,
            selections,
            (_, _) => Task.FromResult(false));

        var error = await Assert.ThrowsAsync<ProviderCatalogException>(() =>
            restartedFactory.GetAsync(run.Run.RunId, default));

        Assert.Equal("provider_selection_invalid", error.Code);
        Assert.Empty(resumedCredentials.RequestedAccounts);
        Assert.Empty(resumedCatalogHandler.Requests);
    }

    [Fact]
    public async Task Recreated_openai_pin_rejects_noncanonical_base_uri_before_reads()
    {
        var settings = new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root));
        await settings.SaveAsync((await settings.ReadAsync(default)) with
        {
            Backend = "OpenAI",
            OpenAiModel = "gpt-5.6-sol",
        }, default);
        await using var harness = await BackendHarnessTestFixture.CreateAsync();
        var run = await harness.SeedRunAsync();
        var firstCredentials = new TrackingCredentials();
        var firstCatalogHandler = new TrackingCatalogHandler();
        var firstCatalog = new ProviderCatalogService(
            new HttpClient(firstCatalogHandler), firstCredentials, TimeProvider.System);
        var selections = new SqliteAgentRunProviderSelectionStore(harness.Options);
        using (var firstFactory = new AgentProviderSnapshotFactory(
                   settings,
                   firstCredentials,
                   firstCatalog,
                   selections,
                   (_, _) => Task.FromResult(false)))
        {
            _ = await firstFactory.GetAsync(run.Run.RunId, default);
        }

        await using (var connection = new SqliteConnection(
                         $"Data Source={harness.Options.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE agent_run_provider_selections SET base_uri = $base_uri WHERE run_id = $run_id";
            update.Parameters.AddWithValue("$base_uri", "https://attacker.invalid/v1");
            update.Parameters.AddWithValue("$run_id", run.Run.RunId.Value);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        var resumedCredentials = new TrackingCredentials();
        var resumedCatalogHandler = new TrackingCatalogHandler();
        var resumedCatalog = new ProviderCatalogService(
            new HttpClient(resumedCatalogHandler), resumedCredentials, TimeProvider.System);
        using var restartedFactory = new AgentProviderSnapshotFactory(
            settings,
            resumedCredentials,
            resumedCatalog,
            selections,
            (_, _) => Task.FromResult(false));

        var error = await Assert.ThrowsAsync<ProviderCatalogException>(() =>
            restartedFactory.GetAsync(run.Run.RunId, default));

        Assert.Equal("provider_selection_invalid", error.Code);
        Assert.Empty(resumedCredentials.RequestedAccounts);
        Assert.Empty(resumedCatalogHandler.Requests);
    }

    [Fact]
    public async Task Recreated_custom_pin_allows_a_null_account_without_credentials()
    {
        var settings = new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root));
        await settings.SaveAsync((await settings.ReadAsync(default)) with
        {
            Backend = "Custom OpenAI",
            CustomModel = "gpt-5.6-sol",
            CustomUrl = "https://provider.invalid/v1",
        }, default);
        await using var harness = await BackendHarnessTestFixture.CreateAsync();
        var run = await harness.SeedRunAsync();
        var selections = new SqliteAgentRunProviderSelectionStore(harness.Options);
        var initialCredentials = new Credentials();
        var initialCatalog = new ProviderCatalogService(
            new HttpClient(new TrackingCatalogHandler()), initialCredentials, TimeProvider.System);
        using (var firstFactory = new AgentProviderSnapshotFactory(
                   settings,
                   initialCredentials,
                   initialCatalog,
                   selections,
                   (_, _) => Task.FromResult(false)))
        {
            _ = await firstFactory.GetAsync(run.Run.RunId, default);
        }

        await using (var connection = new SqliteConnection(
                         $"Data Source={harness.Options.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE agent_run_provider_selections SET account = NULL WHERE run_id = $run_id";
            update.Parameters.AddWithValue("$run_id", run.Run.RunId.Value);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        var resumedCredentials = new TrackingCredentials();
        var resumedCatalogHandler = new TrackingCatalogHandler();
        var resumedCatalog = new ProviderCatalogService(
            new HttpClient(resumedCatalogHandler), resumedCredentials, TimeProvider.System);
        using var restartedFactory = new AgentProviderSnapshotFactory(
            settings,
            resumedCredentials,
            resumedCatalog,
            selections,
            (_, _) => Task.FromResult(false));

        var resumed = await restartedFactory.GetAsync(run.Run.RunId, default);

        Assert.Equal(string.Empty, resumed.Credential);
        Assert.Empty(resumedCredentials.RequestedAccounts);
        Assert.Null(Assert.Single(resumedCatalogHandler.Requests).Credential);
    }

    [Fact]
    public async Task Public_host_recovery_reuses_run_provider_pin_from_sqlite()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-provider-recovery-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var paths = DataPaths.Resolve(root, root, root, Path.Combine(root, "data"), Path.Combine(root, "logs"));
        var settings = new SettingsStore(paths);
        var credentials = new Credentials();
        await settings.SaveAsync((await settings.ReadAsync(default)) with
        {
            Backend = "Custom OpenAI",
            CustomModel = "deepseek-v4-flash-free",
            CustomUrl = "https://provider-a.invalid/v1",
        }, default);

        try
        {
            var runId = await CreateAndPinRunAsync(paths, settings, credentials, workspace);
            await settings.SaveAsync((await settings.ReadAsync(default)) with
            { CustomUrl = "https://provider-b.invalid/v1" }, default);

            using var restartedHost = new BackendWebApplicationFactory(
                dataPaths: paths, credentialStore: credentials, settingsStore: settings);
            var metadataResolver = restartedHost.Services.GetRequiredService<IAgentRunModelMetadataResolver>();
            var resumedMetadata = await metadataResolver.ResolveAsync(runId, default);
            var resumed = await restartedHost.Services.GetRequiredService<AgentProviderSnapshotFactory>()
                .GetAsync(runId, default);

            Assert.Equal("https://provider-a.invalid/v1/chat/completions", resumed.Endpoint.AbsoluteUri);
            Assert.Equal("deepseek-v4-flash-free", resumed.Model);
            Assert.Equal("deepseek-v4-flash-free", resumedMetadata.ModelLabel);
            Assert.Equal(ProviderToolCallingMode.Native, resumedMetadata.ToolCallingMode);
            Assert.Null(resumedMetadata.ContextWindowTokens);
            await using (var connection = new SqliteConnection($"Data Source={paths.AgentHarnessDatabase};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var columns = connection.CreateCommand();
                columns.CommandText = "PRAGMA table_info(agent_run_provider_selections)";
                await using var reader = await columns.ExecuteReaderAsync();
                var names = new List<string>();
                while (await reader.ReadAsync()) names.Add(reader.GetString(1));
                Assert.DoesNotContain("credential", names);
                Assert.DoesNotContain("secret", names);
            }

            await using (var connection = new SqliteConnection(
                             $"Data Source={paths.AgentHarnessDatabase};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM agent_run_provider_selections WHERE run_id = $run_id";
                command.Parameters.AddWithValue("$run_id", runId.Value);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            using var legacyHost = new BackendWebApplicationFactory(dataPaths: paths, settingsStore: settings);
            var legacyError = await Assert.ThrowsAsync<ProviderCatalogException>(() =>
                legacyHost.Services.GetRequiredService<AgentProviderSnapshotFactory>().GetAsync(runId, default));
            Assert.Equal("provider_selection_missing", legacyError.Code);
        }
        finally
        {
            SqliteTestPools.ClearHarnessDatabase(paths.AgentHarnessDatabase);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<RunId> CreateAndPinRunAsync(
        DataPaths paths,
        ISettingsStore settings,
        ICredentialStore credentials,
        string workspace)
    {
        using var host = new BackendWebApplicationFactory(
            dataPaths: paths, credentialStore: credentials, settingsStore: settings);
        using var client = host.CreateAuthenticatedClient();
        using var sessionResponse = await client.PostAsync("/sessions/new", null);
        var sessionBody = await sessionResponse.Content.ReadFromJsonAsync<JsonElement>();
        var request = new
        {
            session_id = sessionBody.GetProperty("session_id").GetString(),
            workspace_path = workspace,
            prompt = "durable provider selection",
            preset = "standard",
            idempotency_key = "provider-recovery-" + Guid.NewGuid().ToString("N"),
            preset_selection_version = 1,
        };
        using var runResponse = await client.PostAsJsonAsync("/agent/v1/runs", request);
        Assert.True(
            runResponse.StatusCode == HttpStatusCode.Created,
            $"Run creation failed with {(int)runResponse.StatusCode}: {await runResponse.Content.ReadAsStringAsync()}");
        var runBody = await runResponse.Content.ReadFromJsonAsync<JsonElement>();
        var runId = new RunId(runBody.GetProperty("run_id").GetString()!);

        var pin = await host.Services.GetRequiredService<AgentProviderSnapshotFactory>()
            .GetAsync(runId, default);

        Assert.Equal("https://provider-a.invalid/v1/chat/completions", pin.Endpoint.AbsoluteUri);
        Assert.Equal("key-one", pin.Credential);
        return runId;
    }

    [Theory]
    [InlineData("missing", "default", "model_unavailable")]
    [InlineData("a", "max", "reasoning_unsupported")]
    public async Task Invalid_selection_never_writes_a_dispatch_handoff(string model, string effort, string expectedCode)
    {
        var store = new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root));
        var settings = (await store.ReadAsync(default)) with
        { Backend = "Custom OpenAI", CustomModel = model, CustomUrl = "https://provider.invalid/v1" };
        await store.SaveAsync(ProviderReasoningPreferences.Save(settings, effort, ProviderSelection.From(settings)), default);
        await using var harness = await BackendHarnessTestFixture.CreateAsync();
        var run = await harness.SeedRunAsync();
        var credentials = new Credentials();
        var catalog = new ProviderCatalogService(new HttpClient(new Handler()), credentials, TimeProvider.System);
        using var factory = new AgentProviderSnapshotFactory(
            store, credentials, catalog, new SqliteAgentRunProviderSelectionStore(harness.Options),
            (_, _) => Task.FromResult(false));
        var error = await Assert.ThrowsAsync<ProviderCatalogException>(() => factory.GetAsync(run.Run.RunId, default));
        Assert.Equal(expectedCode, error.Code);
        Assert.Empty(Directory.GetFiles(_root, "provider-bootstrap-*.bin"));
    }

    [Fact]
    public async Task Missing_key_fails_fast_before_any_network_fetch()
    {
        var store = new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root));
        await store.SaveAsync((await store.ReadAsync(default)) with
        { Backend = "OpenAI", OpenAiModel = "gpt-4o" }, default);
        await using var harness = await BackendHarnessTestFixture.CreateAsync();
        var run = await harness.SeedRunAsync();
        var catalog = new ProviderCatalogService(new HttpClient(new ExplodingHandler()), new EmptyCredentials(), TimeProvider.System);
        using var factory = new AgentProviderSnapshotFactory(
            store, new EmptyCredentials(), catalog, new SqliteAgentRunProviderSelectionStore(harness.Options),
            (_, _) => Task.FromResult(false));

        var error = await Assert.ThrowsAsync<ProviderCatalogException>(() => factory.GetAsync(run.Run.RunId, default));
        Assert.Equal("catalog_unauthorized", error.Code);
        Assert.Empty(Directory.GetFiles(_root, "provider-bootstrap-*.bin"));
    }

    [Fact]
    public async Task Missing_model_fails_fast_before_any_network_fetch()
    {
        var store = new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root));
        await store.SaveAsync((await store.ReadAsync(default)) with
        { Backend = "OpenAI", OpenAiModel = "" }, default);
        await using var harness = await BackendHarnessTestFixture.CreateAsync();
        var run = await harness.SeedRunAsync();
        var catalog = new ProviderCatalogService(new HttpClient(new ExplodingHandler()), new KeyedCredentials(), TimeProvider.System);
        using var factory = new AgentProviderSnapshotFactory(
            store, new KeyedCredentials(), catalog, new SqliteAgentRunProviderSelectionStore(harness.Options),
            (_, _) => Task.FromResult(false));

        var error = await Assert.ThrowsAsync<ProviderCatalogException>(() => factory.GetAsync(run.Run.RunId, default));
        Assert.Equal("model_unavailable", error.Code);
        Assert.Empty(Directory.GetFiles(_root, "provider-bootstrap-*.bin"));
    }

    /// <summary>
    /// A catalog that cannot be read is reported as itself, never as a missing model.
    /// </summary>
    /// <remarks>
    /// The model check reads the catalog, so the two failures sit on one path and it
    /// would be easy to collapse them. They need different codes because they need
    /// different fixes: a rejected key, a rate limit and a server error are not solved by
    /// changing the model, and a model that really is absent is not solved by retrying.
    /// This pins the separation rather than a past defect.
    /// </remarks>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "catalog_unauthorized")]
    [InlineData(HttpStatusCode.TooManyRequests, "catalog_rate_limited")]
    [InlineData(HttpStatusCode.InternalServerError, "catalog_http_error")]
    public async Task An_unreadable_catalog_reports_its_own_failure_not_a_missing_model(
        HttpStatusCode status, string expectedCode)
    {
        // The catalog endpoint comes from the models.dev entry, so the handler can
        // answer for it. A key is present, so the credential check does not fire first
        // and the failure genuinely originates in the catalog read.
        var cacheFile = await WriteModelsDevCacheAsync();
        var store = new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root));
        await store.SaveAsync((await store.ReadAsync(default)) with
        { Backend = "models.dev:openrouter", Model = "stepfun/step-3.7-flash:free" }, default);
        var credentials = new KeyedCredentials();
        var catalog = new ProviderCatalogService(
            new HttpClient(new FailingCatalogHandler(status)), credentials, TimeProvider.System, cacheFile);
        await using var harness = await BackendHarnessTestFixture.CreateAsync();
        var run = await harness.SeedRunAsync();
        using var factory = new AgentProviderSnapshotFactory(
            store, credentials, catalog, new SqliteAgentRunProviderSelectionStore(harness.Options),
            (_, _) => Task.FromResult(false));

        var error = await Assert.ThrowsAsync<ProviderCatalogException>(() => factory.GetAsync(run.Run.RunId, default));
        Assert.Equal(expectedCode, error.Code);
        Assert.Empty(Directory.GetFiles(_root, "provider-bootstrap-*.bin"));
    }

    /// <summary>
    /// A model the provider's catalog does not list is reported as a missing model, not
    /// as whatever else went wrong.
    /// </summary>
    /// <remarks>
    /// The model check reads the catalog, so the two failures are adjacent: a catalog
    /// that could not be read at all must report its own code (covered above), and only a
    /// catalog that was read successfully may accuse the model.
    /// </remarks>
    [Fact]
    public async Task A_model_absent_from_a_healthy_catalog_reports_a_missing_model()
    {
        var cacheFile = await WriteModelsDevCacheAsync();
        var store = new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root));
        await store.SaveAsync((await store.ReadAsync(default)) with
        { Backend = "models.dev:openrouter", Model = "stepfun/step-3.7-flash:free" }, default);
        var credentials = new KeyedCredentials();
        var catalog = new ProviderCatalogService(new HttpClient(new Handler()), credentials, TimeProvider.System, cacheFile);
        await using var harness = await BackendHarnessTestFixture.CreateAsync();
        var run = await harness.SeedRunAsync();
        using var factory = new AgentProviderSnapshotFactory(
            store, credentials, catalog, new SqliteAgentRunProviderSelectionStore(harness.Options),
            (_, _) => Task.FromResult(false));

        var error = await Assert.ThrowsAsync<ProviderCatalogException>(() => factory.GetAsync(run.Run.RunId, default));
        Assert.Equal("model_unavailable", error.Code);
        Assert.Empty(Directory.GetFiles(_root, "provider-bootstrap-*.bin"));
    }

    /// <summary>A model the catalog does list still dispatches, so the check cannot false-block.</summary>
    private async Task<string> WriteModelsDevCacheAsync()
    {
        Directory.CreateDirectory(_root);
        var entries = Enumerable.Range(0, 60)
            .Select(i => $"\"prov{i:000}\":{{\"api\":\"https://prov{i:000}.example/v1\"}}");
        var providers = "{\"openrouter\":{\"api\":\"https://openrouter.ai/api/v1\"},"
            + string.Join(",", entries) + "}";
        var cacheFile = Path.Combine(_root, "models-dev.json");
        await File.WriteAllTextAsync(cacheFile,
            $$"""{"fetched_at":"2099-01-01T00:00:00+00:00","payload":{{providers}}}""");
        return cacheFile;
    }

    /// <summary>A catalog endpoint that answers with a status the service has codes for.</summary>
    private sealed class FailingCatalogHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    private static async Task<ProviderBootstrapData> ReadAsync(AgentProviderSnapshotFactory factory, RunId run)
    {
        return await factory.GetAsync(run, default);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class Credentials : ICredentialStore
    {
        public string Key = "key-one";
        public Task<string?> GetAsync(string serviceName, string accountName, CancellationToken cancellationToken)
        { Assert.Equal("custom_api_key", accountName); return Task.FromResult<string?>(Key); }
        public Task SetAsync(string serviceName, string accountName, string secret, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(string serviceName, string accountName, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                  {"data":[{"id":"a","context_length":222222,"supported_parameters":["reasoning_effort"],"reasoning_efforts":["low","high"]},{"id":"b"}]}
                  """),
            });
    }

    /// <summary>Any network fetch is a fail: the missing key/model must throw first.</summary>
    private sealed class ExplodingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no network fetch may happen before the credential/model check");
    }

    private sealed class EmptyCredentials : ICredentialStore
    {
        public Task<string?> GetAsync(string serviceName, string accountName, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
        public Task SetAsync(string serviceName, string accountName, string secret, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(string serviceName, string accountName, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class KeyedCredentials : ICredentialStore
    {
        public Task<string?> GetAsync(string serviceName, string accountName, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("key-one");
        public Task SetAsync(string serviceName, string accountName, string secret, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(string serviceName, string accountName, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TrackingCredentials : ICredentialStore
    {
        public List<string> RequestedAccounts { get; } = [];

        public Task<string?> GetAsync(string serviceName, string accountName, CancellationToken cancellationToken)
        {
            RequestedAccounts.Add(accountName);
            return Task.FromResult<string?>(accountName switch
            {
                "openai_api_key" => "openai-secret",
                "anthropic_api_key" => "anthropic-secret",
                _ => null,
            });
        }

        public Task SetAsync(string serviceName, string accountName, string secret, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string serviceName, string accountName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TrackingCatalogHandler : HttpMessageHandler
    {
        public List<(Uri? Uri, string? Credential)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri, request.Headers.Authorization?.Parameter));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"gpt-5.6-sol"}]}"""),
            });
        }
    }
}
