using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Harness.Persistence.Workspace;
using RuBackend.Harness.Policy;
using RuBackend.Harness.Workspace;
using Xunit;

using RuAgent.Protocol.Harness;
namespace RuBackend.Tests.Harness;

internal sealed class BackendHarnessTestFixture : IAsyncDisposable
{
    private readonly FakeKeyStore _keyStore = new();

    private BackendHarnessTestFixture(string rootPath)
    {
        RootPath = rootPath;
        Options = new HarnessSqliteOptions(Path.Combine(rootPath, "agent_harness.db"));
        PayloadStore = new HarnessPayloadStore(
            Options,
            new HarnessPayloadCryptography(_keyStore),
            KnownSecretRedactor.Empty);
        EventStore = new SqliteHarnessEventStore(Options, PayloadStore,
            new SessionWorkStateStore(Options, new SessionWorkStateCryptography(_keyStore)));
    }

    public static readonly DateTimeOffset Timestamp =
        new(2026, 8, 24, 1, 2, 3, TimeSpan.Zero);

    public string RootPath { get; }

    public HarnessSqliteOptions Options { get; }

    public HarnessPayloadStore PayloadStore { get; }

    public SqliteHarnessEventStore EventStore { get; }

    /// <summary>
    /// The AES key payloads and workspace roots are protected with. Shared so a test can
    /// decrypt the same durable rows the production services would.
    /// </summary>
    public IHarnessKeyStore KeyStore => _keyStore;

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
        var promptPayloadId = payloads?
            .FirstOrDefault(payload => payload.Kind == HarnessPayloadKind.UserPrompt)?
            .PayloadId;
        var request = new NewAgentRun(
            run,
            [new HarnessEventIntent(
                new EventId("evt_11111111111111111111111111111111"),
                HarnessEventKind.RunStateChanged,
                payloadId: promptPayloadId)],
            payloads ?? [],
            outbox ?? [],
            new RunCreationIdentity(new string('c', 64), new string('d', 64), 1));
        var result = await EventStore.TryCreateRunAsync(request, CancellationToken.None);
        Assert.Equal(CreateRunOutcome.Created, result.Outcome);
        return request;
    }

    /// <summary>
    /// Seeds a run whose workspace is a real temp directory: the canonical root is
    /// encrypted with <see cref="KeyStore"/> exactly like production, and the run's
    /// policy contract matches the snapshot the loader recomputes after a restart.
    /// The startup recovery rehydrator only registers such a run, so a fixture with a
    /// placeholder ciphertext can never exercise that path.
    /// </summary>
    public async Task<NewAgentRun> SeedRunWithEncryptedWorkspaceAsync(
        HarnessRunStatus status,
        string runId,
        string sessionId,
        string? workspaceRoot = null)
    {
        workspaceRoot ??= Path.Combine(RootPath, "workspace-" + runId);
        Directory.CreateDirectory(workspaceRoot);
        var resolver = new WindowsWorkspaceIdentityResolver();
        var trustService = new WorkspaceTrustService(
            resolver,
            new SqliteWorkspaceStore(Options),
            new WorkspaceTrustRequestStore(Options),
            _keyStore);
        await trustService.EnsureTrustRequestAsync(workspaceRoot, CancellationToken.None);
        var identity = await resolver.ResolveAsync(workspaceRoot, CancellationToken.None);
        var snapshot = await new HarnessPolicySnapshotService(Options, _keyStore, new SystemTimeProvider())
            .LoadAsync(identity, AgentPermissionPreset.Standard, new RunId(runId), CancellationToken.None);
        var run = new AgentRun(
            new RunId(runId),
            new SessionId(sessionId),
            identity.WorkspaceId,
            status,
            0,
            1,
            1,
            0,
            0,
            AgentPermissionPreset.Standard,
            snapshot.Version,
            snapshot.Sha256,
            Timestamp,
            Timestamp);
        var request = new NewAgentRun(
            run,
            [new HarnessEventIntent(
                new EventId($"evt_{Guid.NewGuid():N}"),
                HarnessEventKind.RunStateChanged)],
            [],
            [],
            new RunCreationIdentity(new string('c', 64), new string('d', 64), 1));
        var result = await EventStore.TryCreateRunAsync(request, CancellationToken.None);
        Assert.Equal(CreateRunOutcome.Created, result.Outcome);
        return request;
    }

    public async ValueTask DisposeAsync()
    {
        // Only this fixture's harness pool: the process-global ClearAllPools() used
        // to race pooled connections held by tests in other collections.
        SqliteTestPools.ClearHarnessDatabase(Options.DatabasePath);

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
