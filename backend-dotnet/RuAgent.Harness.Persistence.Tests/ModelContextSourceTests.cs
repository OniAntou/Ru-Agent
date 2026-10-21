using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Persistence.Context;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class ModelContextSourceTests
{
    [Fact]
    public async Task Runtime_snapshot_restores_as_untrusted_user_context_and_is_not_client_readable()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var store = fixture.CreateEventStore();
        var request = fixture.NewRun(1, 1, 1);
        await store.TryCreateRunAsync(request, default);
        await fixture.PayloadStore.CaptureBaseContextAsync(request.Run.RunId, [], HarnessStoreTestFixture.Timestamp, default);
        var payload = new HarnessPayloadIntent(HarnessStoreTestFixture.Payload(40),
            HarnessPayloadKind.RuntimeContextSnapshot, HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text, 4096, Encoding.UTF8.GetBytes("snapshot"));
        Assert.False(HarnessPayloadStore.CanReadForClient(payload));
        await store.CommitAsync(new HarnessCommit(request.Run.RunId, 0,
            NextRun(request.Run, version: 1, lastSequence: 2),
            [new HarnessEventIntent(HarnessStoreTestFixture.Event(40), HarnessEventKind.RunNoteAdded,
                payloadId: payload.PayloadId, safeCode: "runtime_context_snapshot")], [payload], []), default);

        // A fresh source has no in-memory state to supply the snapshot.
        var context = await new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore))
            .AssembleAsync(request.Run.RunId, default);
        var entry = Assert.Single(context.Entries);
        Assert.Equal(ModelContextRole.User, entry.Role);
        Assert.Equal("snapshot", entry.Content);
        Assert.True(RuAgent.Harness.Core.Prompting.RuntimeContextSnapshot.IsSnapshot(entry));
    }

    [Fact]
    public async Task Restart_reconstructs_base_user_assistant_step_and_tool_result_in_order()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var eventStore = fixture.CreateEventStore();
        var userPayload = TextPayload(
            1,
            HarnessPayloadKind.UserPrompt,
            "current request");
        var requestBase = fixture.NewRun(1, 1, 1);
        var request = new NewAgentRun(
            requestBase.Run,
            [new HarnessEventIntent(
                requestBase.Events[0].EventId,
                HarnessEventKind.RunStateChanged,
                payloadId: userPayload.PayloadId)],
            [userPayload],
            [],
            requestBase.CreationIdentity);
        await eventStore.TryCreateRunAsync(request, CancellationToken.None);
        var baseResult = await fixture.PayloadStore.CaptureBaseContextAsync(
            request.Run.RunId,
            [
                new HarnessBaseContextEntry(
                    HarnessStoreTestFixture.Payload(10),
                    "base:1:user",
                    1,
                    ModelContextRole.User,
                    "old prompt"),
                new HarnessBaseContextEntry(
                    HarnessStoreTestFixture.Payload(11),
                    "base:1:assistant",
                    2,
                    ModelContextRole.Assistant,
                    "old response"),
            ],
            HarnessStoreTestFixture.Timestamp.AddSeconds(1),
            CancellationToken.None);
        var assistantStep = new AssistantStep(
            "checking",
            [
                new ModelToolCall(
                    "provider-call-1",
                    "read",
                    "1",
                    System.Text.Json.JsonDocument.Parse("{\"op\":\"read\",\"path\":\"README.md\"}").RootElement),
            ],
            AssistantFinishReason.ToolCalls,
            estimatedInputTokens: 42);
        var assistantPayload = new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(20),
            HarnessPayloadKind.AssistantStep,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Json,
            4096,
            AssistantStepJsonCodec.Serialize(assistantStep));
        var assistantRun = NextRun(request.Run, version: 1, lastSequence: 2);
        await eventStore.CommitAsync(
            new HarnessCommit(
                request.Run.RunId,
                0,
                assistantRun,
                [new HarnessEventIntent(
                    HarnessStoreTestFixture.Event(20),
                    HarnessEventKind.ModelStepCompleted,
                    modelStepId: new ModelStepId("step_11111111111111111111111111111111"),
                    payloadId: assistantPayload.PayloadId)],
                [assistantPayload],
                []),
            CancellationToken.None);
        var toolPayload = TextPayload(
            21,
            HarnessPayloadKind.ToolOutput,
            "file contents");
        var toolRun = NextRun(assistantRun, version: 2, lastSequence: 3);
        await eventStore.CommitAsync(
            new HarnessCommit(
                request.Run.RunId,
                1,
                toolRun,
                [new HarnessEventIntent(
                    HarnessStoreTestFixture.Event(21),
                    HarnessEventKind.ToolExecutionCompleted,
                    toolCallId: new ToolCallId("call_11111111111111111111111111111111"),
                    providerCallId: "provider-call-1",
                    executionId: new ExecutionId("exec_11111111111111111111111111111111"),
                    payloadId: toolPayload.PayloadId,
                    safeCode: "ok",
                    toolOutcome: ToolExecutionOutcome.Succeeded,
                    sideEffectsPossible: false)],
                [toolPayload],
                []),
            CancellationToken.None);

        var first = await new AgentContextAssembler(
                new SqliteModelContextSource(fixture.Options, fixture.PayloadStore))
            .AssembleAsync(request.Run.RunId, CancellationToken.None);
        var afterRestart = await new AgentContextAssembler(
                new SqliteModelContextSource(fixture.Options, fixture.PayloadStore))
            .AssembleAsync(request.Run.RunId, CancellationToken.None);

        Assert.Equal(BaseContextCaptureOutcome.Captured, baseResult);
        Assert.Equal(5, first.Entries.Count);
        Assert.Equal(
            [
                "base:1:user",
                "base:1:assistant",
                $"event:{request.Events[0].EventId.Value}",
                $"event:{HarnessStoreTestFixture.Event(20).Value}",
                $"event:{HarnessStoreTestFixture.Event(21).Value}",
            ],
            first.Entries.Select(entry => entry.SourceId));
        var restoredStep = first.Entries[3].AssistantStep!;
        Assert.Equal(assistantStep.Text, restoredStep.Text);
        Assert.Equal("provider-call-1", Assert.Single(restoredStep.ToolCalls).ProviderCallId);
        Assert.Equal("call_11111111111111111111111111111111", first.Entries[4].ToolReceipt!.ToolCallId.Value);
        Assert.Equal("provider-call-1", first.Entries[4].ToolReceipt!.ProviderCallId);
        Assert.Equal(assistantStep.FinishReason, restoredStep.FinishReason);
        Assert.Equal(assistantStep.EstimatedInputTokens, restoredStep.EstimatedInputTokens);
        Assert.Equal(
            Assert.Single(assistantStep.ToolCalls).Arguments.GetRawText(),
            Assert.Single(restoredStep.ToolCalls).Arguments.GetRawText());
        Assert.Equal(ToolExecutionOutcome.Succeeded, first.Entries[4].ToolReceipt!.Outcome);
        Assert.Equal("file contents", first.Entries[4].Content);
        Assert.Equal(first.CanonicalUtf8.ToArray(), afterRestart.CanonicalUtf8.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_replays_chained_compactions_by_run_version_when_clock_moves_back(bool warmCache)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var eventStore = fixture.CreateEventStore();
        var prompt = TextPayload(1, HarnessPayloadKind.UserPrompt, "source prompt");
        var requestBase = fixture.NewRun(1, 1, 1);
        var request = new NewAgentRun(
            requestBase.Run,
            [new HarnessEventIntent(
                requestBase.Events[0].EventId,
                HarnessEventKind.RunStateChanged,
                payloadId: prompt.PayloadId)],
            [prompt],
            [],
            requestBase.CreationIdentity);
        await eventStore.TryCreateRunAsync(request, CancellationToken.None);
        await fixture.PayloadStore.CaptureBaseContextAsync(
            request.Run.RunId, [], HarnessStoreTestFixture.Timestamp, CancellationToken.None);

        var assembler = new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore));
        if (warmCache) Assert.Equal("source prompt", Assert.Single((await assembler.AssembleAsync(request.Run.RunId, default)).Entries).Content);

        var firstTime = HarnessStoreTestFixture.Timestamp.AddSeconds(20);
        var afterFirst = RunAt(request.Run, 1, 2, firstTime);
        await CommitCompactionAsync(
            eventStore, request.Run.RunId, afterFirst, expectedVersion: 0, eventNumber: 20,
            "compact-a", "first summary", $"event:{request.Events[0].EventId.Value}", firstTime);
        if (warmCache) Assert.Equal("first summary", Assert.Single((await assembler.AssembleAsync(request.Run.RunId, default)).Entries).Content);
        var secondTime = HarnessStoreTestFixture.Timestamp.AddSeconds(10);
        var afterSecond = RunAt(afterFirst, 2, 3, secondTime);
        await CommitCompactionAsync(
            eventStore, request.Run.RunId, afterSecond, expectedVersion: 1, eventNumber: 21,
            "compact-b", "second summary", "compact-a", secondTime);

        var context = await (warmCache ? assembler : new AgentContextAssembler(
                new SqliteModelContextSource(fixture.Options, fixture.PayloadStore)))
            .AssembleAsync(request.Run.RunId, CancellationToken.None);

        var summary = Assert.Single(context.Entries);
        Assert.Equal("compact-b", summary.SourceId);
        Assert.Equal("second summary", summary.Content);
    }

    private static async Task CommitCompactionAsync(
        SqliteHarnessEventStore store,
        RunId runId,
        AgentRun next,
        long expectedVersion,
        int eventNumber,
        string compactionId,
        string summary,
        string coveredSourceId,
        DateTimeOffset occurredAt)
    {
        var bytes = Encoding.UTF8.GetBytes(summary);
        var payload = new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(eventNumber),
            HarnessPayloadKind.ModelContextSummary,
            HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            4096,
            bytes);
        var range = new ContextEntryRange(coveredSourceId, 1, 1);
        var outcome = await store.CommitAsync(
            new HarnessCommit(
                runId,
                expectedVersion,
                next,
                [new HarnessEventIntent(
                    HarnessStoreTestFixture.Event(eventNumber),
                    HarnessEventKind.ContextCompacted,
                    safeCount: 1,
                    safeCode: "context_compacted")],
                [payload],
                [],
                new ContextCompactionIntent(
                    runId,
                    compactionId,
                    payload.PayloadId,
                    ContextCompactionStrategy.Normal,
                    ContextCompactionReason.Manual,
                    [range],
                    beforeTokens: 200,
                    afterTokens: 100,
                    inputHash: new string('e', 64))),
            CancellationToken.None);

        Assert.Equal(HarnessCommitOutcome.Committed, outcome.Outcome);
        Assert.Equal(occurredAt, next.UpdatedAt);
    }

    [Theory]
    [InlineData("{\"text\":null,\"tool_calls\":[{\"provider_call_id\":\"provider-duplicate\",\"tool_id\":\"read\",\"tool_version\":\"1.0.0\",\"arguments\":{}},{\"provider_call_id\":\"provider-duplicate\",\"tool_id\":\"read\",\"tool_version\":\"1.0.0\",\"arguments\":{}}],\"finish_reason\":\"tool_calls\",\"estimated_input_tokens\":42}")]
    [InlineData("{\"text\":null,\"tool_calls\":[{\"provider_call_id\":\"provider/unsafe\",\"tool_id\":\"read\",\"tool_version\":\"1.0.0\",\"arguments\":{}}],\"finish_reason\":\"tool_calls\",\"estimated_input_tokens\":42}")]
    [InlineData("{\"text\":null,\"tool_calls\":[{\"provider_call_id\":null,\"tool_id\":\"read\",\"tool_version\":\"1.0.0\",\"arguments\":{}}],\"finish_reason\":\"tool_calls\",\"estimated_input_tokens\":42}")]
    [InlineData("{\"text\":null,\"tool_calls\":[{\"tool_id\":\"read\",\"tool_version\":\"1.0.0\",\"arguments\":{}}],\"finish_reason\":\"tool_calls\",\"estimated_input_tokens\":42}")]
    public async Task Restart_rejects_invalid_native_provider_call_ids(string assistantJson)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var eventStore = fixture.CreateEventStore();
        var request = fixture.NewRun(1, 1, 1);
        await eventStore.TryCreateRunAsync(request, CancellationToken.None);
        await fixture.PayloadStore.CaptureBaseContextAsync(
            request.Run.RunId,
            [],
            HarnessStoreTestFixture.Timestamp.AddSeconds(1),
            CancellationToken.None);
        var payload = new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(70),
            HarnessPayloadKind.AssistantStep,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Json,
            4096,
            Encoding.UTF8.GetBytes(assistantJson));
        var next = NextRun(request.Run, version: 1, lastSequence: 2);
        await eventStore.CommitAsync(
            new HarnessCommit(
                request.Run.RunId,
                0,
                next,
                [new HarnessEventIntent(
                    HarnessStoreTestFixture.Event(70),
                    HarnessEventKind.ModelStepCompleted,
                    modelStepId: new ModelStepId("step_11111111111111111111111111111111"),
                    payloadId: payload.PayloadId)],
                [payload],
                []),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            new SqliteModelContextSource(fixture.Options, fixture.PayloadStore)
                .LoadAsync(request.Run.RunId, CancellationToken.None));

        Assert.Equal("context_payload_invalid", exception.Code);
    }

    [Fact]
    public async Task Base_capture_is_once_only_and_tampered_payload_fails_closed()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var request = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(request, CancellationToken.None);
        var entry = new HarnessBaseContextEntry(
            HarnessStoreTestFixture.Payload(30),
            "base:1:user",
            1,
            ModelContextRole.User,
            "private context");

        var first = await fixture.PayloadStore.CaptureBaseContextAsync(
            request.Run.RunId,
            [entry],
            HarnessStoreTestFixture.Timestamp.AddSeconds(1),
            CancellationToken.None);
        var repeated = await fixture.PayloadStore.CaptureBaseContextAsync(
            request.Run.RunId,
            [new HarnessBaseContextEntry(
                entry.PayloadId,
                entry.SourceId,
                entry.Sequence,
                entry.Role,
                "different")],
            HarnessStoreTestFixture.Timestamp.AddSeconds(2),
            CancellationToken.None);
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE agent_payloads
                SET protected_content = zeroblob(length(protected_content))
                WHERE payload_id = $payload_id
                """;
            command.Parameters.AddWithValue("$payload_id", entry.PayloadId.Value);
            await command.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            new SqliteModelContextSource(fixture.Options, fixture.PayloadStore)
                .LoadAsync(request.Run.RunId, CancellationToken.None));

        Assert.Equal(BaseContextCaptureOutcome.Captured, first);
        Assert.Equal(BaseContextCaptureOutcome.AlreadyCaptured, repeated);
        Assert.Equal("payload_authentication_failed", exception.Code);
    }

    [Fact]
    public async Task Oversized_stored_payload_is_rejected_before_decryption()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var request = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(request, CancellationToken.None);
        var entry = new HarnessBaseContextEntry(
            HarnessStoreTestFixture.Payload(31),
            "base:1:user",
            1,
            ModelContextRole.User,
            "bounded context");
        await fixture.PayloadStore.CaptureBaseContextAsync(
            request.Run.RunId,
            [entry],
            HarnessStoreTestFixture.Timestamp.AddSeconds(1),
            CancellationToken.None);

        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE agent_payloads
                SET protected_content = zeroblob(4194305)
                WHERE payload_id = $payload_id
                """;
            command.Parameters.AddWithValue("$payload_id", entry.PayloadId.Value);
            await command.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            new SqliteModelContextSource(fixture.Options, fixture.PayloadStore)
                .LoadAsync(request.Run.RunId, CancellationToken.None));

        Assert.Equal("context_payload_invalid", exception.Code);
    }

    [Fact]
    public async Task Repetition_note_restores_as_a_system_text_entry()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var eventStore = fixture.CreateEventStore();
        var request = fixture.NewRun(1, 1, 1);
        await eventStore.TryCreateRunAsync(request, CancellationToken.None);
        await fixture.PayloadStore.CaptureBaseContextAsync(
            request.Run.RunId,
            [],
            HarnessStoreTestFixture.Timestamp.AddSeconds(1),
            CancellationToken.None);
        var noteBytes = Encoding.UTF8.GetBytes(
            "[REPETITION GUARD] change the arguments before repeating a call");
        var notePayload = new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(40),
            HarnessPayloadKind.ModelContextNote,
            HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            4096,
            noteBytes);
        var noteRun = NextRun(request.Run, version: 1, lastSequence: 2);
        await eventStore.CommitAsync(
            new HarnessCommit(
                request.Run.RunId,
                0,
                noteRun,
                [new HarnessEventIntent(
                    HarnessStoreTestFixture.Event(40),
                    HarnessEventKind.RunNoteAdded,
                    modelStepId: new ModelStepId("step_11111111111111111111111111111111"),
                    payloadId: notePayload.PayloadId,
                    safeCode: "repeated_tool_call")],
                [notePayload],
                []),
            CancellationToken.None);

        var context = await new AgentContextAssembler(
                new SqliteModelContextSource(fixture.Options, fixture.PayloadStore))
            .AssembleAsync(request.Run.RunId, CancellationToken.None);

        var entry = Assert.Single(context.Entries);
        Assert.Equal(ModelContextEntryKind.Text, entry.Kind);
        Assert.Equal(ModelContextRole.System, entry.Role);
        Assert.Equal(
            "[REPETITION GUARD] change the arguments before repeating a call",
            entry.Content);
        Assert.Equal($"event:{HarnessStoreTestFixture.Event(40).Value}", entry.SourceId);
    }

    [Fact]
    public async Task Recovery_note_restores_as_a_system_text_entry()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var eventStore = fixture.CreateEventStore();
        var request = fixture.NewRun(1, 1, 1);
        await eventStore.TryCreateRunAsync(request, CancellationToken.None);
        await fixture.PayloadStore.CaptureBaseContextAsync(
            request.Run.RunId,
            [],
            HarnessStoreTestFixture.Timestamp.AddSeconds(1),
            CancellationToken.None);
        var noteText = AgentRecoveryNotes.BuildUnverifiedNote("workspace_path_outside_root");
        var noteBytes = Encoding.UTF8.GetBytes(noteText);
        var notePayload = new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(41),
            HarnessPayloadKind.ModelContextNote,
            HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            4096,
            noteBytes);
        var noteRun = NextRun(request.Run, version: 1, lastSequence: 2);
        await eventStore.CommitAsync(
            new HarnessCommit(
                request.Run.RunId,
                0,
                noteRun,
                [new HarnessEventIntent(
                    HarnessStoreTestFixture.Event(41),
                    HarnessEventKind.RunNoteAdded,
                    modelStepId: new ModelStepId("step_11111111111111111111111111111111"),
                    payloadId: notePayload.PayloadId,
                    safeCode: "unverified_after_tool_failure")],
                [notePayload],
                []),
            CancellationToken.None);

        var context = await new AgentContextAssembler(
                new SqliteModelContextSource(fixture.Options, fixture.PayloadStore))
            .AssembleAsync(request.Run.RunId, CancellationToken.None);

        var entry = Assert.Single(context.Entries);
        Assert.Equal(ModelContextEntryKind.Text, entry.Kind);
        Assert.Equal(ModelContextRole.System, entry.Role);
        Assert.Equal(noteText, entry.Content);
        Assert.StartsWith(AgentRecoveryNotes.NotePrefix, entry.Content, StringComparison.Ordinal);
        Assert.Equal($"event:{HarnessStoreTestFixture.Event(41).Value}", entry.SourceId);
    }

    [Fact]
    public async Task Advised_recovery_step_assembles_note_and_leaves_internal_assistant_step_unscoped()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var eventStore = fixture.CreateEventStore();
        var request = fixture.NewRun(1, 1, 1);
        await eventStore.TryCreateRunAsync(request, CancellationToken.None);
        await fixture.PayloadStore.CaptureBaseContextAsync(
            request.Run.RunId,
            [],
            HarnessStoreTestFixture.Timestamp.AddSeconds(1),
            CancellationToken.None);
        const string toolErrorCode = "workspace_path_outside_root";
        var deadEnd = new AssistantStep(
            "I succeeded in running the command!",
            Array.Empty<ModelToolCall>(),
            AssistantFinishReason.Stop,
            estimatedInputTokens: 0);
        var assistantPayload = new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(50),
            HarnessPayloadKind.AssistantStep,
            HarnessPayloadAudience.InternalOnly,
            HarnessPayloadEncoding.Json,
            4096,
            AssistantStepJsonCodec.Serialize(deadEnd));
        var noteText = AgentRecoveryNotes.BuildUnverifiedNote(toolErrorCode);
        var noteBytes = Encoding.UTF8.GetBytes(noteText);
        var notePayload = new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(51),
            HarnessPayloadKind.ModelContextNote,
            HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            4096,
            noteBytes);
        var modelStepId = new ModelStepId("step_11111111111111111111111111111111");
        var advisedRun = NextRun(request.Run, version: 1, lastSequence: 3);
        await eventStore.CommitAsync(
            new HarnessCommit(
                request.Run.RunId,
                0,
                advisedRun,
                [
                    new HarnessEventIntent(
                        HarnessStoreTestFixture.Event(50),
                        HarnessEventKind.ModelStepCompleted,
                        modelStepId: modelStepId,
                        payloadId: assistantPayload.PayloadId,
                        safeCode: "unverified_after_tool_failure_advised"),
                    new HarnessEventIntent(
                        HarnessStoreTestFixture.Event(51),
                        HarnessEventKind.RunNoteAdded,
                        modelStepId: modelStepId,
                        payloadId: notePayload.PayloadId,
                        safeCode: "unverified_after_tool_failure"),
                ],
                [assistantPayload, notePayload],
                []),
            CancellationToken.None);

        var context = await new AgentContextAssembler(
                new SqliteModelContextSource(fixture.Options, fixture.PayloadStore))
            .AssembleAsync(request.Run.RunId, CancellationToken.None);

        var entry = Assert.Single(context.Entries);
        Assert.Equal(ModelContextEntryKind.Text, entry.Kind);
        Assert.Equal(ModelContextRole.System, entry.Role);
        Assert.Equal(noteText, entry.Content);
        Assert.Equal($"event:{HarnessStoreTestFixture.Event(51).Value}", entry.SourceId);
        Assert.Equal(0L, await ScopedCountAsync(fixture, assistantPayload.PayloadId));
        Assert.Equal(1L, await ScopedCountAsync(fixture, notePayload.PayloadId));
        var forbidden = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            fixture.PayloadStore.ReadForModelAsync(
                request.Run.RunId,
                assistantPayload.PayloadId,
                CancellationToken.None));
        Assert.Equal("payload_model_forbidden", forbidden.Code);
    }

    [Fact]
    public async Task Manually_scoped_internal_only_payload_still_fails_closed()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var eventStore = fixture.CreateEventStore();
        var request = fixture.NewRun(1, 1, 1);
        await eventStore.TryCreateRunAsync(request, CancellationToken.None);
        await fixture.PayloadStore.CaptureBaseContextAsync(
            request.Run.RunId,
            [],
            HarnessStoreTestFixture.Timestamp.AddSeconds(1),
            CancellationToken.None);
        var assistantPayload = new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(60),
            HarnessPayloadKind.AssistantStep,
            HarnessPayloadAudience.InternalOnly,
            HarnessPayloadEncoding.Json,
            4096,
            AssistantStepJsonCodec.Serialize(new AssistantStep(
                "suppressed",
                Array.Empty<ModelToolCall>(),
                AssistantFinishReason.Stop,
                estimatedInputTokens: 0)));
        var next = NextRun(request.Run, version: 1, lastSequence: 2);
        await eventStore.CommitAsync(
            new HarnessCommit(
                request.Run.RunId,
                0,
                next,
                [new HarnessEventIntent(
                    HarnessStoreTestFixture.Event(60),
                    HarnessEventKind.ModelStepCompleted,
                    modelStepId: new ModelStepId("step_11111111111111111111111111111111"),
                    payloadId: assistantPayload.PayloadId,
                    safeCode: "unverified_after_tool_failure_advised")],
                [assistantPayload],
                []),
            CancellationToken.None);

        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE agent_payloads
                SET context_scope = 1,
                    context_source_id = $source_id,
                    context_sequence = 2,
                    context_role = $role,
                    context_entry_kind = $entry_kind
                WHERE payload_id = $payload_id
                """;
            command.Parameters.AddWithValue(
                "$source_id",
                $"event:{HarnessStoreTestFixture.Event(60).Value}");
            command.Parameters.AddWithValue("$role", (int)ModelContextRole.Assistant);
            command.Parameters.AddWithValue("$entry_kind", (int)ModelContextEntryKind.AssistantStep);
            command.Parameters.AddWithValue("$payload_id", assistantPayload.PayloadId.Value);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var exception = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            new SqliteModelContextSource(fixture.Options, fixture.PayloadStore)
                .LoadAsync(request.Run.RunId, CancellationToken.None));

        Assert.Equal("context_payload_forbidden", exception.Code);
    }

    private static async Task<long> ScopedCountAsync(
        HarnessStoreTestFixture fixture,
        PayloadId payloadId)
    {
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM agent_payloads
            WHERE payload_id = $payload_id
              AND context_scope IS NOT NULL
            """;
        command.Parameters.AddWithValue("$payload_id", payloadId.Value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static HarnessPayloadIntent TextPayload(
        int number,
        HarnessPayloadKind kind,
        string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(number),
            kind,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            4096,
            bytes);
    }

    private static AgentRun NextRun(AgentRun current, long version, long lastSequence) =>
        new(
            current.RunId,
            current.SessionId,
            current.WorkspaceId,
            new HarnessRunStatus(HarnessRunState.Running),
            version,
            lastSequence,
            current.BudgetWindow,
            current.ModelStepsInWindow,
            current.ToolCallsInWindow,
            current.Preset,
            current.PolicySnapshotVersion,
            current.PolicySnapshotSha256,
            current.CreatedAt,
            current.UpdatedAt.AddSeconds(version));

    private static AgentRun RunAt(
        AgentRun current,
        long version,
        long lastSequence,
        DateTimeOffset updatedAt) =>
        new(
            current.RunId,
            current.SessionId,
            current.WorkspaceId,
            new HarnessRunStatus(HarnessRunState.Running),
            version,
            lastSequence,
            current.BudgetWindow,
            current.ModelStepsInWindow,
            current.ToolCallsInWindow,
            current.Preset,
            current.PolicySnapshotVersion,
            current.PolicySnapshotSha256,
            current.CreatedAt,
            updatedAt,
            permissionProfileVersion: current.PermissionProfileVersion);
}
