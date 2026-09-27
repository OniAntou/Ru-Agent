using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence.Workspace;
using RuAgent.Harness.Persistence.Privacy;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

internal sealed class HarnessStoreTestFixture : IAsyncDisposable
{
    private HarnessStoreTestFixture(string rootPath)
    {
        RootPath = rootPath;
        Options = new HarnessSqliteOptions(Path.Combine(rootPath, "agent_harness.db"));
        PayloadStore = new HarnessPayloadStore(
            Options,
            new HarnessPayloadCryptography(new FakeHarnessKeyStore()),
            KnownSecretRedactor.Empty);
    }

    public string RootPath { get; }

    public HarnessSqliteOptions Options { get; }

    public HarnessPayloadStore PayloadStore { get; }

    public SqliteHarnessEventStore CreateEventStore() => new(Options, PayloadStore);

    public static async Task<HarnessStoreTestFixture> CreateAsync()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "ru-harness-persistence-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        var fixture = new HarnessStoreTestFixture(rootPath);
        var health = await HarnessSqliteSchema.InitializeAsync(
            fixture.Options,
            CancellationToken.None);
        Assert.True(health.IsAvailable, health.ErrorCode);
        return fixture;
    }

    public SqliteConnection CreateRawConnection() =>
        new($"Data Source={Options.DatabasePath};Mode=ReadWrite;Pooling=False");

    public async Task<WorkspaceIdentity> RegisterWorkspaceAsync(int number)
    {
        var identity = Workspace(number);
        var registration = new WorkspaceRegistration(
            identity,
            new WorkspaceRootCiphertext(
                Enumerable.Repeat((byte)number, 12).ToArray(),
                Enumerable.Repeat((byte)(number + 16), 48).ToArray(),
                Enumerable.Repeat((byte)(number + 32), 16).ToArray()));
        var request = new WorkspaceTrustRequest(
            Trust(number),
            identity,
            Timestamp,
            Timestamp.AddMinutes(10));
        var store = new SqliteWorkspaceStore(Options);
        await store.RegisterAsync(registration, request, CancellationToken.None);
        return identity;
    }

    public NewAgentRun NewRun(
        int runNumber,
        int sessionNumber,
        int workspaceNumber,
        char idempotencyHashCharacter = 'a',
        char requestHashCharacter = 'b',
        AgentPermissionPreset preset = AgentPermissionPreset.Standard,
        long selectionVersion = 1,
        IReadOnlyList<HarnessPayloadIntent>? payloads = null,
        IReadOnlyList<ConversationOutboxIntent>? outbox = null,
        HarnessRunStatus? status = null,
        int permissionProfileVersion = 1)
    {
        var run = new AgentRun(
            Run(runNumber),
            Session(sessionNumber),
            Workspace(workspaceNumber).WorkspaceId,
            status ?? new HarnessRunStatus(HarnessRunState.Created),
            version: 0,
            lastSequence: 1,
            budgetWindow: 1,
            modelStepsInWindow: 0,
            toolCallsInWindow: 0,
            preset,
            policySnapshotVersion: 1,
            policySnapshotSha256: Sha('c'),
            Timestamp,
            Timestamp,
            permissionProfileVersion: permissionProfileVersion);
        return new NewAgentRun(
            run,
            [new HarnessEventIntent(Event(runNumber), HarnessEventKind.RunStateChanged)],
            payloads ?? [],
            outbox ?? [],
            new RunCreationIdentity(
                Sha(idempotencyHashCharacter),
                Sha(requestHashCharacter),
                selectionVersion));
    }

    public static readonly DateTimeOffset Timestamp =
        new(2026, 8, 24, 1, 2, 3, TimeSpan.Zero);

    public static WorkspaceIdentity Workspace(int number) =>
        new(
            new WorkspaceId(Generated("ws_", number)),
            $"volume-{number}",
            $"root-file-{number}",
            Sha((char)('a' + (number % 6))));

    public static SessionId Session(int number) => new(number.ToString("x32"));

    public static RunId Run(int number) => new(Generated("run_", number));

    public static EventId Event(int number) => new(Generated("evt_", number));

    public static PayloadId Payload(int number) => new(Generated("payload_", number));

    public static TrustRequestId Trust(int number) => new(Generated("trust_", number));

    public static string Sha(char character) => new(character, 64);

    public async ValueTask DisposeAsync()
    {
        // Clear every pool, not one connection string: pools are keyed by the
        // full string, so clearing a single string misses every pool opened
        // with different options and leaves its file lock behind.
        SqliteConnection.ClearAllPools();

        await Task.Yield();
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }

    private static string Generated(string prefix, int number) =>
        prefix + number.ToString("x32");
}
