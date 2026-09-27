using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Harness.Persistence.Workspace;
using Xunit;

namespace RuBackend.Tests.Harness;

internal sealed class BackendHarnessTestFixture : IAsyncDisposable
{
    private BackendHarnessTestFixture(string rootPath)
    {
        RootPath = rootPath;
        Options = new HarnessSqliteOptions(Path.Combine(rootPath, "agent_harness.db"));
        PayloadStore = new HarnessPayloadStore(
            Options,
            new HarnessPayloadCryptography(new FakeKeyStore()),
            KnownSecretRedactor.Empty);
        EventStore = new SqliteHarnessEventStore(Options, PayloadStore);
    }

    public static readonly DateTimeOffset Timestamp =
        new(2026, 8, 24, 1, 2, 3, TimeSpan.Zero);

    public string RootPath { get; }

    public HarnessSqliteOptions Options { get; }

    public HarnessPayloadStore PayloadStore { get; }

    public SqliteHarnessEventStore EventStore { get; }

    public static async Task<BackendHarnessTestFixture> CreateAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ru-backend-harness-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var fixture = new BackendHarnessTestFixture(root);
        var health = await HarnessSqliteSchema.InitializeAsync(
            fixture.Options,
            CancellationToken.None);
        Assert.True(health.IsAvailable, health.ErrorCode);
        return fixture;
    }

    public async Task<NewAgentRun> SeedRunAsync(
        HarnessRunStatus? status = null,
        IReadOnlyList<HarnessPayloadIntent>? payloads = null,
        IReadOnlyList<ConversationOutboxIntent>? outbox = null)
    {
        var workspace = new WorkspaceIdentity(
            new WorkspaceId("ws_11111111111111111111111111111111"),
            "volume-1",
            "root-file-1",
            new string('a', 64));
        await new SqliteWorkspaceStore(Options).RegisterAsync(
            new WorkspaceRegistration(
                workspace,
                new WorkspaceRootCiphertext(new byte[12], new byte[] { 1 }, new byte[16])),
            new WorkspaceTrustRequest(
                new TrustRequestId("trust_11111111111111111111111111111111"),
                workspace,
                Timestamp,
                Timestamp.AddMinutes(10)),
            CancellationToken.None);
        var run = new AgentRun(
            new RunId("run_11111111111111111111111111111111"),
            new SessionId("11111111111111111111111111111111"),
            workspace.WorkspaceId,
            status ?? new HarnessRunStatus(HarnessRunState.Created),
            0,
            1,
            1,
            0,
            0,
            AgentPermissionPreset.Standard,
            1,
            new string('b', 64),
            Timestamp,
            Timestamp);
        var request = new NewAgentRun(
            run,
            [new HarnessEventIntent(
                new EventId("evt_11111111111111111111111111111111"),
                HarnessEventKind.RunStateChanged)],
            payloads ?? [],
            outbox ?? [],
            new RunCreationIdentity(new string('c', 64), new string('d', 64), 1));
        var result = await EventStore.TryCreateRunAsync(request, CancellationToken.None);
        Assert.Equal(CreateRunOutcome.Created, result.Outcome);
        return request;
    }

    public async ValueTask DisposeAsync()
    {
        // All pools, not one string: pools are keyed by the full connection
        // string, and the backend opens this file through several option sets.
        SqliteConnection.ClearAllPools();

        await Task.Yield();
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }

    private sealed class FakeKeyStore : IHarnessKeyStore
    {
        public ValueTask<byte[]> GetOrCreateKeyAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Enumerable.Repeat((byte)0x5a, 32).ToArray());
    }
}
