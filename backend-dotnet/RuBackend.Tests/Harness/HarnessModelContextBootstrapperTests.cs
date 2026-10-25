using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Context;
using RuBackend.Business;
using RuBackend.Business.Sessions;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class HarnessModelContextBootstrapperTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task New_turn_keeps_cancelled_run_work_only_while_its_prompt_remains_visible(bool promptVisible)
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var prompt = new HarnessPayloadIntent(new PayloadId("payload_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            HarnessPayloadKind.UserPrompt, HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text, 4096, System.Text.Encoding.UTF8.GetBytes("Fix the parser and run its tests."));
        var previous = await fixture.SeedRunAsync(payloads: [prompt]);
        var selections = new SqliteAgentRunProviderSelectionStore(fixture.Options);
        var modelA = new AgentRunProviderSelection("Custom OpenAI", "custom", "chat_completions",
            "https://provider.invalid/v1", "model-a", "custom_api_key", "{}", 8192);
        await selections.PinAsync(previous.Run.RunId, modelA, default);
        var step = new AssistantStep("Updated parser.cs; now checking the tests.",
            [new ModelToolCall("old-call", "powershell", "1",
                System.Text.Json.JsonDocument.Parse("{\"script\":\"dotnet test Parser.Tests\"}").RootElement)],
            AssistantFinishReason.ToolCalls, 1);
        var assistant = new HarnessPayloadIntent(new PayloadId("payload_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            HarnessPayloadKind.AssistantStep, HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Json, 8192, AssistantStepJsonCodec.Serialize(step));
        var output = new HarnessPayloadIntent(new PayloadId("payload_cccccccccccccccccccccccccccccccc"),
            HarnessPayloadKind.ToolOutput, HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text, 4096, System.Text.Encoding.UTF8.GetBytes("Parser.Tests: 12 passed"));
        var ended = fixtureTime(3);
        var stopped = new AgentRun(previous.Run.RunId, previous.Run.SessionId, previous.Run.WorkspaceId,
            new HarnessRunStatus(HarnessRunState.Cancelled), 1, 4, 1, 1, 1,
            previous.Run.Preset, 1, previous.Run.PolicySnapshotSha256, previous.Run.CreatedAt, ended,
            terminalAt: ended);
        await fixture.EventStore.CommitAsync(new HarnessCommit(previous.Run.RunId, 0, stopped,
            [new HarnessEventIntent(new EventId("evt_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
                HarnessEventKind.ModelStepCompleted, payloadId: assistant.PayloadId),
             new HarnessEventIntent(new EventId("evt_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
                HarnessEventKind.ToolExecutionCompleted,
                toolCallId: new ToolCallId("call_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), providerCallId: "old-call",
                executionId: new ExecutionId("exec_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
                payloadId: output.PayloadId, safeCode: "ok", toolOutcome: ToolExecutionOutcome.Succeeded,
                sideEffectsPossible: false),
             new HarnessEventIntent(new EventId("evt_cccccccccccccccccccccccccccccccc"), HarnessEventKind.RunStateChanged)],
            [assistant, output], []), default);

        var nextRun = new AgentRun(new RunId("run_22222222222222222222222222222222"), previous.Run.SessionId,
            previous.Run.WorkspaceId, new HarnessRunStatus(HarnessRunState.Created), 0, 1, 1, 0, 0,
            previous.Run.Preset, 1, previous.Run.PolicySnapshotSha256, fixtureTime(4), fixtureTime(4));
        var nextPrompt = new HarnessPayloadIntent(new PayloadId("payload_dddddddddddddddddddddddddddddddd"),
            HarnessPayloadKind.UserPrompt, HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text, 4096, System.Text.Encoding.UTF8.GetBytes("Check the results and continue with model B."));
        await fixture.EventStore.TryCreateRunAsync(new NewAgentRun(nextRun,
            [new HarnessEventIntent(new EventId("evt_dddddddddddddddddddddddddddddddd"), HarnessEventKind.RunStateChanged,
                payloadId: nextPrompt.PayloadId)],
            [nextPrompt], [], new RunCreationIdentity(new string('e', 64), new string('f', 64), 1)), default);
        await selections.PinAsync(nextRun.RunId, modelA with { Model = "model-b" }, default);
        var session = new SessionDocument(nextRun.SessionId.Value, "Model switch", fixtureTime(0), fixtureTime(4),
            promptVisible
                ? [new SessionMessage("user", "Fix the parser and run its tests.", "source-old-user", previous.Run.RunId.Value)]
                : []); // Rewind removed the old prompt; never resurrect that work.
        var bootstrapper = new HarnessModelContextBootstrapper(new SnapshotSessionStore(session),
            fixture.PayloadStore, new CryptographicIdGenerator(), TimeProvider.System);

        await bootstrapper.CaptureAsync(nextRun.RunId, nextRun.SessionId, default);
        var snapshot = await new SqliteModelContextSource(fixture.Options, fixture.PayloadStore)
            .LoadAsync(nextRun.RunId, default);
        if (!promptVisible)
        {
            Assert.Empty(snapshot.SessionBase);
            return;
        }
        var handoff = Assert.Single(snapshot.SessionBase);
        Assert.Contains("Parser.Tests: 12 passed", handoff.Content);
        Assert.Contains("dotnet test Parser.Tests", handoff.Content);
        Assert.Contains(previous.Run.RunId.Value, handoff.Content);
        Assert.Contains("Cancelled", handoff.Content);
        Assert.Contains("untrusted", handoff.Content);
        Assert.Equal("model-a", (await selections.GetAsync(previous.Run.RunId, default))!.Selection!.Model);
        Assert.Equal("model-b", (await selections.GetAsync(nextRun.RunId, default))!.Selection!.Model);
        var context = await new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore))
            .AssembleAsync(nextRun.RunId, default);
        var (current, _, messages) = RuBackend.Harness.ModelRunner.InProcessModelStepRunner.BuildConversationForTests(context);
        Assert.Equal("Check the results and continue with model B.", current);
        Assert.Contains(messages!, message => message.Content.Contains("Parser.Tests: 12 passed", StringComparison.Ordinal));
        Assert.All(messages!, message => Assert.Empty(message.ToolCalls));

        static DateTimeOffset fixtureTime(int seconds) => BackendHarnessTestFixture.Timestamp.AddSeconds(seconds);
    }

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
    public async Task Captures_thirty_two_completed_turns_once_without_copying_source_metadata_into_entries()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var request = await fixture.SeedRunAsync();
        var root = Path.Combine(Path.GetTempPath(), "ru-context-bootstrap", Guid.NewGuid().ToString("N"));
        var paths = new DataPaths(Path.Combine(root, "settings.json"),
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
            Enumerable.Range(0, 40)
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
        Assert.Equal(64, snapshot.SessionBase.Count);
        Assert.Equal("prompt-8", snapshot.SessionBase[0].Content);
        Assert.Equal("reply-39", snapshot.SessionBase[^1].AssistantStep!.Text);
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
