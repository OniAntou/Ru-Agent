using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Reconciliation;
using RuBackend.Harness.Git;
using RuBackend.Harness.Reconciliation;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Workspace;
using RuAgent.Harness.Core.Workspace;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentReconcileEndpointTests
{
    private const string ReconciledRunId = "run_44444444444444444444444444444444";

    [Fact]
    public async Task Reconcile_rejects_unknown_outcome_tokens()
    {
        var factory = await CreateHostAsync();
        try
        {
            using var client = factory.CreateAuthenticatedClient();
            using var response = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{ReconciledRunId}/reconcile",
                new { outcome = "sort_of_applied", snapshot_hash = new string('0', 64) });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("invalid_outcome", json.GetProperty("error_code").GetString());
        }
        finally
        {
            factory.Dispose();
        }
    }

    [Fact]
    public async Task Reconcile_returns_404_for_an_unknown_run()
    {
        var factory = await CreateHostAsync();
        try
        {
            using var client = factory.CreateAuthenticatedClient();
            using var response = await client.PostAsJsonAsync(
                "/agent/v1/runs/run_55555555555555555555555555555555/reconcile",
                new { outcome = "applied", snapshot_hash = new string('0', 64) });

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("run_not_found", json.GetProperty("error_code").GetString());
        }
        finally
        {
            factory.Dispose();
        }
    }

    [Fact]
    public async Task Reconcile_rejects_runs_that_are_not_awaiting_reconciliation()
    {
        var factory = await CreateHostAsync();
        try
        {
            await SeedRunAsync(factory, HarnessRunState.Running);
            using var client = factory.CreateAuthenticatedClient();
            using var response = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{ReconciledRunId}/reconcile",
                new { outcome = "applied", snapshot_hash = new string('0', 64) });

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("invalid_run_state", json.GetProperty("error_code").GetString());
        }
        finally
        {
            factory.Dispose();
        }
    }

    [Fact]
    public async Task Stale_snapshot_hash_returns_reconcile_stale_without_touching_the_run()
    {
        var factory = await CreateHostAsync();
        try
        {
            await SeedRunAsync(factory, HarnessRunState.ReconciliationRequired);
            using var client = factory.CreateAuthenticatedClient();
            using var response = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{ReconciledRunId}/reconcile",
                new { outcome = "applied", snapshot_hash = new string('9', 64) });

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("reconcile_stale", json.GetProperty("error_code").GetString());
            var store = factory.Services.GetRequiredService<IHarnessEventStore>();
            var run = await store.GetRunAsync(new RunId(ReconciledRunId), CancellationToken.None);
            Assert.Equal(HarnessRunState.ReconciliationRequired, run!.State);
        }
        finally
        {
            factory.Dispose();
        }
    }

    [Fact]
    public async Task Applied_resolution_returns_the_run_to_running()
    {
        var factory = await CreateHostAsync();
        try
        {
            await SeedRunAsync(factory, HarnessRunState.ReconciliationRequired);
            var service = factory.Services.GetRequiredService<WorkspaceStateSnapshotService>();
            var hash = await service.ComputeFreshHashAsync(new RunId(ReconciledRunId), CancellationToken.None);
            Assert.False(string.IsNullOrWhiteSpace(hash));

            using var client = factory.CreateAuthenticatedClient();
            using var response = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{ReconciledRunId}/reconcile",
                new { outcome = "applied", snapshot_hash = hash });


            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("applied", json.GetProperty("resolution").GetString());
            Assert.Equal("recovery_ambiguous", json.GetProperty("intent_code").GetString());
            Assert.Equal("running", json.GetProperty("state").GetString());

            var store = factory.Services.GetRequiredService<IHarnessEventStore>();
            var run = await store.GetRunAsync(new RunId(ReconciledRunId), CancellationToken.None);
            Assert.Equal(HarnessRunState.Running, run!.State);
        }
        finally
        {
            factory.Dispose();
        }
    }

    [Fact]
    public async Task NotApplied_resolution_returns_the_run_to_running_without_a_gate()
    {
        var factory = await CreateHostAsync();
        try
        {
            await SeedRunAsync(factory, HarnessRunState.ReconciliationRequired);
            var service = factory.Services.GetRequiredService<WorkspaceStateSnapshotService>();
            var hash = await service.ComputeFreshHashAsync(new RunId(ReconciledRunId), CancellationToken.None);

            using var client = factory.CreateAuthenticatedClient();
            using var response = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{ReconciledRunId}/reconcile",
                new { outcome = "not_applied", snapshot_hash = hash });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("not_applied", json.GetProperty("resolution").GetString());
            Assert.Equal("running", json.GetProperty("state").GetString());

            var gate = factory.Services.GetRequiredService<ReconciliationInspectionGate>();
            Assert.False(gate.IsMutationDenied(new RunId(ReconciledRunId)));
        }
        finally
        {
            factory.Dispose();
        }
    }

    [Fact]
    public async Task Unknown_continue_resumes_with_a_mutation_gate_until_inspection()
    {
        var factory = await CreateHostAsync();
        try
        {
            await SeedRunAsync(factory, HarnessRunState.ReconciliationRequired);
            var service = factory.Services.GetRequiredService<WorkspaceStateSnapshotService>();
            var hash = await service.ComputeFreshHashAsync(new RunId(ReconciledRunId), CancellationToken.None);

            using var client = factory.CreateAuthenticatedClient();
            using var response = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{ReconciledRunId}/reconcile",
                new { outcome = "unknown_continue", snapshot_hash = hash });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("unknown_continue", json.GetProperty("resolution").GetString());
            Assert.Equal("recovery_ambiguous", json.GetProperty("intent_code").GetString());
            Assert.Equal("running", json.GetProperty("state").GetString());

            var gate = factory.Services.GetRequiredService<ReconciliationInspectionGate>();
            Assert.True(gate.IsMutationDenied(new RunId(ReconciledRunId)));
        }
        finally
        {
            factory.Dispose();
        }
    }

    [Fact]
    public async Task Continue_on_a_parked_run_resolves_reconciliation_without_a_snapshot_hash()
    {
        // The desktop client exposes only Continue for a parked run (it has no
        // reconcile call and cannot obtain the workspace hash), so Continue must
        // be the decision instead of answering invalid_run_transition and
        // leaving the run stuck forever.
        var factory = await CreateHostAsync();
        try
        {
            await SeedRunAsync(factory, HarnessRunState.ReconciliationRequired);

            using var client = factory.CreateAuthenticatedClient();
            using var response = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{ReconciledRunId}/continue",
                new { });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            // Continue delegates to the reconciliation resolution, so the response
            // carries the reconcile shape rather than the plain run projection.
            Assert.Equal("unknown_continue", json.GetProperty("resolution").GetString());
            Assert.Equal("running", json.GetProperty("state").GetString());

            var store = factory.Services.GetRequiredService<IHarnessEventStore>();
            var events = new List<string>();
            await foreach (var envelope in store.ReadEventsAsync(new RunId(ReconciledRunId), 0, CancellationToken.None))
            {
                events.Add(envelope.Event.Kind);
            }

            Assert.Contains("reconciliation.resolved", events);
            var gate = factory.Services.GetRequiredService<ReconciliationInspectionGate>();
            Assert.True(gate.IsMutationDenied(new RunId(ReconciledRunId)));
        }
        finally
        {
            factory.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // Fixtures.
    // ------------------------------------------------------------------

    private static async Task<TestHost> CreateHostAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-reconcile-test-" + Guid.NewGuid().ToString("N"));
        var wsPath = Path.Combine(root, "ws");
        var dataRoot = Path.Combine(root, "data");
        Directory.CreateDirectory(wsPath);
        Directory.CreateDirectory(dataRoot);
        var dataPaths = RuBackend.Business.DataPaths.Resolve(root, root, root, dataRoot, Path.Combine(root, "logs"));
        var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        var options = factory.Services.GetRequiredService<HarnessSqliteOptions>();
        var health = await HarnessSqliteSchema.InitializeAsync(options, CancellationToken.None);
        Assert.True(health.IsAvailable, health.ErrorCode);
        return new TestHost(factory, root);
    }

    private static async Task SeedRunAsync(TestHost host, HarnessRunState state)
    {
        var workspaceStore = host.Factory.Services.GetRequiredService<RuAgent.Harness.Persistence.Workspace.SqliteWorkspaceStore>();
        var workspace = new WorkspaceIdentity(
            new WorkspaceId("ws_66666666666666666666666666666666"),
            "volume-reconcile",
            "root-file-reconcile",
            new string('a', 64));
        await workspaceStore.RegisterAsync(
            new WorkspaceRegistration(
                workspace,
                new WorkspaceRootCiphertext(new byte[12], new byte[] { 1 }, new byte[16])),
            new WorkspaceTrustRequest(
                new TrustRequestId("trust_66666666666666666666666666666666"),
                workspace,
                BackendHarnessTestFixture.Timestamp,
                BackendHarnessTestFixture.Timestamp.AddMinutes(10)),
            CancellationToken.None);

        // The durable Git state gives the snapshot service something fresh to hash.
        var options = host.Factory.Services.GetRequiredService<HarnessSqliteOptions>();
        var gitStateDirectory = Path.GetDirectoryName(options.DatabasePath)!;
        var gitStore = new SqliteRunGitStateStore(Path.Combine(gitStateDirectory, "agent_git_state.db"));
        await gitStore.SaveStartSnapshotAsync(
            ReconciledRunId,
            new RuAgent.Protocol.Harness.RunGitWorktreeSnapshot(
                RepositoryRoot: wsPlaceholder,
                HeadOid: new string('1', 40),
                BranchName: "main",
                IsDetached: false,
                IndexHash: new string('2', 64),
                HasStagedChanges: false,
                ManagedCommitEligible: true,
                Entries: Array.Empty<RuAgent.Protocol.Harness.RunGitStatusEntry>(),
                ObservedFileHashes: new Dictionary<string, string>(),
                StatusTruncated: false,
                FileHashesTruncated: false,
                CapturedAtUtc: BackendHarnessTestFixture.Timestamp),
            CancellationToken.None);

        var store = host.Factory.Services.GetRequiredService<IHarnessEventStore>();
        var run = new AgentRun(
            new RunId(ReconciledRunId),
            new SessionId("66666666666666666666666666666666"),
            workspace.WorkspaceId,
            new HarnessRunStatus(state),
            0,
            1,
            1,
            0,
            0,
            AgentPermissionPreset.Standard,
            1,
            new string('b', 64),
            BackendHarnessTestFixture.Timestamp,
            BackendHarnessTestFixture.Timestamp);
        var request = new NewAgentRun(
            run,
            new[]
            {
                new HarnessEventIntent(
                    new EventId("evt_66666666666666666666666666666666"),
                    HarnessEventKind.ReconciliationRequired,
                    safeCode: "recovery_ambiguous"),
            },
            Array.Empty<HarnessPayloadIntent>(),
            Array.Empty<ConversationOutboxIntent>(),
            new RunCreationIdentity(new string('c', 64), new string('d', 64), 1));
        var result = await store.TryCreateRunAsync(request, CancellationToken.None);
        Assert.Equal(CreateRunOutcome.Created, result.Outcome);
    }

    private const string wsPlaceholder = @"C:\workspace\placeholder";

    private sealed record TestHost(BackendWebApplicationFactory Factory, string Root)
    {
        public HttpClient CreateAuthenticatedClient() => Factory.CreateAuthenticatedClient();

        public IServiceProvider Services => Factory.Services;

        public void Dispose()
        {
            Factory.Dispose();
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }

            SqliteConnectionClear();
        }
    }

    private static void SqliteConnectionClear()
    {
        using var pool = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { Mode = Microsoft.Data.Sqlite.SqliteOpenMode.Memory }.ToString());
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pool);
    }
}
