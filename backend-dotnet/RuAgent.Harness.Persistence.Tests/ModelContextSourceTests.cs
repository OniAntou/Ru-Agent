using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Context;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class ModelContextSourceTests
{
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
        Assert.Equal(assistantStep.FinishReason, restoredStep.FinishReason);
        Assert.Equal(assistantStep.EstimatedInputTokens, restoredStep.EstimatedInputTokens);
        Assert.Equal(
            Assert.Single(assistantStep.ToolCalls).Arguments.GetRawText(),
            Assert.Single(restoredStep.ToolCalls).Arguments.GetRawText());
        Assert.Equal(ToolExecutionOutcome.Succeeded, first.Entries[4].ToolReceipt!.Outcome);
        Assert.Equal("file contents", first.Entries[4].Content);
        Assert.Equal(first.CanonicalUtf8.ToArray(), afterRestart.CanonicalUtf8.ToArray());
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
}
