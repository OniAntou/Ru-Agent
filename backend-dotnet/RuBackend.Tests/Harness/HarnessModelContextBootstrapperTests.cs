using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Persistence.Context;
using RuBackend.Business;
using RuBackend.Business.Sessions;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class HarnessModelContextBootstrapperTests
{
    [Fact]
    public async Task Captures_one_coherent_loaded_session_snapshot()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var request = await fixture.SeedRunAsync();
        var session = new SessionDocument(
            request.Run.SessionId.Value,
            "Context",
            BackendHarnessTestFixture.Timestamp,
            BackendHarnessTestFixture.Timestamp,
            [
                new SessionMessage("user", "snapshot prompt"),
                new SessionMessage("ai", "snapshot reply"),
            ]);
        var sessions = new SnapshotSessionStore(session);
        var bootstrapper = new HarnessModelContextBootstrapper(
            sessions,
            fixture.PayloadStore,
            new CryptographicIdGenerator(),
            TimeProvider.System);

        var outcome = await bootstrapper.CaptureAsync(
            request.Run.RunId,
            request.Run.SessionId,
            CancellationToken.None);
        var snapshot = await new SqliteModelContextSource(fixture.Options, fixture.PayloadStore)
            .LoadAsync(request.Run.RunId, CancellationToken.None);

        Assert.Equal(ContextBootstrapOutcome.Captured, outcome);
        Assert.Equal(1, sessions.GetCalls);
        Assert.Equal(0, sessions.GetContextCalls);
        Assert.Equal("snapshot prompt", snapshot.SessionBase[0].Content);
        Assert.Equal("snapshot reply", snapshot.SessionBase[1].AssistantStep!.Text);
    }

    [Fact]
    public async Task Captures_the_existing_eight_turn_context_once_and_ignores_source_metadata()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var request = await fixture.SeedRunAsync();
        var root = Path.Combine(Path.GetTempPath(), "ru-context-bootstrap", Guid.NewGuid().ToString("N"));
        var paths = new DataPaths(
            Path.Combine(root, "memory.db"),
            Path.Combine(root, "settings.json"),
            Path.Combine(root, "sessions"),
            Path.Combine(root, "activity.jsonl"));
        var sessions = new SessionStore(paths);
        Directory.CreateDirectory(paths.SessionDirectory);
        var sessionId = request.Run.SessionId.Value;
        var document = new SessionDocument(
            sessionId,
            "Context",
            BackendHarnessTestFixture.Timestamp,
            BackendHarnessTestFixture.Timestamp,
            Enumerable.Range(0, 10)
                .SelectMany(index => new[]
                {
                    new SessionMessage(
                        "user",
                        $"prompt-{index}",
                        $"source-user-{index}",
                        request.Run.RunId.Value),
                    new SessionMessage(
                        "ai",
                        $"reply-{index}",
                        $"source-ai-{index}",
                        request.Run.RunId.Value),
                })
                .Append(new SessionMessage(
                    "user",
                    "current incomplete prompt",
                    "current-source",
                    request.Run.RunId.Value))
                .ToArray());
        await File.WriteAllTextAsync(
            Path.Combine(paths.SessionDirectory, $"{sessionId}.json"),
            System.Text.Json.JsonSerializer.Serialize(document));
        var bootstrapper = new HarnessModelContextBootstrapper(
            sessions,
            fixture.PayloadStore,
            new CryptographicIdGenerator(),
            TimeProvider.System);

        var first = await bootstrapper.CaptureAsync(
            request.Run.RunId,
            request.Run.SessionId,
            CancellationToken.None);
        var repeated = await bootstrapper.CaptureAsync(
            request.Run.RunId,
            request.Run.SessionId,
            CancellationToken.None);
        var snapshot = await new SqliteModelContextSource(fixture.Options, fixture.PayloadStore)
            .LoadAsync(request.Run.RunId, CancellationToken.None);

        Assert.Equal(ContextBootstrapOutcome.Captured, first);
        Assert.Equal(ContextBootstrapOutcome.AlreadyCaptured, repeated);
        Assert.Equal(16, snapshot.SessionBase.Count);
        Assert.Equal("prompt-2", snapshot.SessionBase[0].Content);
        Assert.Equal("reply-9", snapshot.SessionBase[^1].AssistantStep!.Text);
        Assert.DoesNotContain(snapshot.SessionBase, entry =>
            entry.Content == "current incomplete prompt");
    }

    private sealed class SnapshotSessionStore(SessionDocument session) : ISessionStore
    {
        public int GetCalls { get; private set; }

        public int GetContextCalls { get; private set; }

        public Task<IReadOnlyList<SessionSummary>> ListAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SessionDocument?> GetAsync(
            string sessionId,
            CancellationToken cancellationToken)
        {
            GetCalls++;
            return Task.FromResult<SessionDocument?>(session);
        }

        public Task<string?> CreateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(
            string sessionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> AppendAsync(
            string sessionId,
            IReadOnlyList<SessionMessage> messages,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SessionAppendOutcome> AppendIfAbsentAsync(
            string sessionId,
            SessionMessage message,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ConversationTurn>> GetContextTurnsAsync(
            string sessionId,
            int maxTurns,
            CancellationToken cancellationToken)
        {
            GetContextCalls++;
            return Task.FromResult<IReadOnlyList<ConversationTurn>>(
                [new ConversationTurn("racy prompt", "racy reply")]);
        }
    }
}
