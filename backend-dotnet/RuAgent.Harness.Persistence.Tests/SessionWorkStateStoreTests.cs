using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Context;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class SessionWorkStateStoreTests
{
    private static readonly SessionId Session = new("11111111111111111111111111111111");
    private static readonly RunId Run = new("run_33333333333333333333333333333333");
    private static readonly EventId Event = new("evt_44444444444444444444444444444444");

    [Fact]
    public async Task Store_encrypts_roundtrips_and_rejects_stale_versions()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var workspace = await fixture.RegisterWorkspaceAsync(1);
        await SeedSourceRunAsync(fixture, workspace.WorkspaceId);
        var store = fixture.CreateWorkStateStore();
        var state = State(workspace.WorkspaceId, workspace.DisplayIdentityHash, 2);

        Assert.True(await store.SaveAsync(state, 1, HarnessStoreTestFixture.Timestamp, CancellationToken.None));
        Assert.False(await store.SaveAsync(State(workspace.WorkspaceId, workspace.DisplayIdentityHash, 2), 1,
            HarnessStoreTestFixture.Timestamp.AddSeconds(1), CancellationToken.None));
        var restored = await store.GetAsync(Session, workspace.WorkspaceId, CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal(state.UserGoal, restored.UserGoal);
        Assert.Equal(state.UserConstraints, restored.UserConstraints);
        Assert.Empty(restored.Tasks);
        Assert.Equal(state.SourceRunId, restored.SourceRunId);
        Assert.Equal(state.Version, restored.Version);
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(ciphertext AS TEXT) FROM agent_session_work_states WHERE session_id = $session_id";
        command.Parameters.AddWithValue("$session_id", Session.Value);
        Assert.DoesNotContain("explicitly bounded user goal", (string)(await command.ExecuteScalarAsync() ?? string.Empty), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_delete_removes_work_state_artifact()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var workspace = await fixture.RegisterWorkspaceAsync(1);
        await SeedSourceRunAsync(fixture, workspace.WorkspaceId);
        var store = fixture.CreateWorkStateStore();
        Assert.True(await store.SaveAsync(State(workspace.WorkspaceId, workspace.DisplayIdentityHash, 2), 1,
            HarnessStoreTestFixture.Timestamp, CancellationToken.None));

        await new HarnessSessionArtifactStore(fixture.Options).CloseAndDeleteAsync(
            Session.Value, HarnessStoreTestFixture.Timestamp.AddMinutes(1), CancellationToken.None);

        Assert.Null(await store.GetAsync(Session, workspace.WorkspaceId, CancellationToken.None));
    }

    [Fact]
    public async Task Workspace_identity_change_fails_closed()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var workspace = await fixture.RegisterWorkspaceAsync(1);
        await SeedSourceRunAsync(fixture, workspace.WorkspaceId);
        var store = fixture.CreateWorkStateStore();
        Assert.True(await store.SaveAsync(State(workspace.WorkspaceId, workspace.DisplayIdentityHash, 2), 1,
            HarnessStoreTestFixture.Timestamp, CancellationToken.None));

        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE agent_workspaces SET display_identity_hash = $hash WHERE workspace_id = $workspace_id";
            command.Parameters.AddWithValue("$hash", new string('f', 64));
            command.Parameters.AddWithValue("$workspace_id", workspace.WorkspaceId.Value);
            await command.ExecuteNonQueryAsync();
        }

        var moved = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            store.GetAsync(Session, workspace.WorkspaceId, CancellationToken.None));
        Assert.Equal("session_work_state_workspace_changed", moved.Code);
    }

    [Fact]
    public async Task Ciphertext_corruption_fails_authentication()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var workspace = await fixture.RegisterWorkspaceAsync(1);
        await SeedSourceRunAsync(fixture, workspace.WorkspaceId);
        var store = fixture.CreateWorkStateStore();
        Assert.True(await store.SaveAsync(State(workspace.WorkspaceId, workspace.DisplayIdentityHash, 2), 1,
            HarnessStoreTestFixture.Timestamp, CancellationToken.None));
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE agent_session_work_states SET ciphertext = zeroblob(length(ciphertext)) WHERE session_id = $session_id";
            command.Parameters.AddWithValue("$session_id", Session.Value);
            await command.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            store.GetAsync(Session, workspace.WorkspaceId, CancellationToken.None));
        Assert.Equal("session_work_state_authentication_failed", error.Code);
    }

    [Fact]
    public async Task Altered_state_source_fails_authentication_instead_of_claiming_complete_receipt_coverage()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var workspace = await fixture.RegisterWorkspaceAsync(1);
        await SeedSourceRunAsync(fixture, workspace.WorkspaceId);
        var store = fixture.CreateWorkStateStore();
        Assert.True(await store.SaveAsync(State(workspace.WorkspaceId, workspace.DisplayIdentityHash, 2), 1,
            HarnessStoreTestFixture.Timestamp, CancellationToken.None));
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE agent_session_work_states SET source_event_id = 'evt_00000000000000000000000000000000' WHERE session_id = $session_id";
            command.Parameters.AddWithValue("$session_id", Session.Value);
            await command.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            store.GetSnapshotAsync(Session, workspace.WorkspaceId, CancellationToken.None));

        Assert.Equal("session_work_state_authentication_failed", error.Code);
    }

    [Fact]
    public async Task State_rejects_a_fact_from_another_run_binding()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var workspace = await fixture.RegisterWorkspaceAsync(1);
        await SeedSourceRunAsync(fixture, workspace.WorkspaceId);
        var store = fixture.CreateWorkStateStore();
        var valid = State(workspace.WorkspaceId, workspace.DisplayIdentityHash, 2);
        var foreignFact = new SessionWorkFact("foreign", "run_99999999999999999999999999999999", Event.Value);
        var state = new SessionWorkState(valid.SessionId, valid.WorkspaceId, valid.WorkspaceIdentitySha256,
            valid.SourceRunId, valid.Version, valid.SourceEventId, foreignFact, valid.UserConstraints,
            valid.EarlierUserIntent, valid.EarlierUserIntentOmitted, valid.HasUnrepresentedReceipts, valid.Tasks, valid.ObservedFiles,
            valid.Verifications, valid.Blocker, valid.NextAction);

        var error = await Assert.ThrowsAsync<HarnessStoreException>(() => store.SaveAsync(state, 1,
            HarnessStoreTestFixture.Timestamp, CancellationToken.None));

        Assert.Equal("session_work_state_source_mismatch", error.Code);
        Assert.Equal("Source prompt.", (await store.GetAsync(Session, workspace.WorkspaceId,
            CancellationToken.None))?.UserGoal.Text);
    }

    [Fact]
    public async Task Run_creation_commits_user_intent_state_with_the_prompt_event()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var workspace = await fixture.RegisterWorkspaceAsync(1);
        var template = fixture.NewRun(1, 1, 1);
        var promptId = new PayloadId("payload_55555555555555555555555555555555");
        var promptText = "Fix parser output; keep existing route and Broker authority contracts.";
        var prompt = new HarnessPayloadIntent(promptId, HarnessPayloadKind.UserPrompt,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text, 256, System.Text.Encoding.UTF8.GetBytes(promptText));
        var run = new NewAgentRun(template.Run,
            [new HarnessEventIntent(Event, HarnessEventKind.RunStateChanged, payloadId: promptId)],
            [prompt], [], template.CreationIdentity);

        var created = await fixture.CreateEventStore().TryCreateRunAsync(run, CancellationToken.None);
        var state = await fixture.CreateWorkStateStore().GetAsync(template.Run.SessionId,
            workspace.WorkspaceId, CancellationToken.None);
        await fixture.PayloadStore.CaptureBaseContextAsync(template.Run.RunId,
            [new HarnessBaseContextEntry(new PayloadId("payload_66666666666666666666666666666666"),
                "base:prior-user", 1, ModelContextRole.User, "Prior session work")],
            HarnessStoreTestFixture.Timestamp.AddSeconds(1), CancellationToken.None);
        var context = await new AgentContextAssembler(new SqliteModelContextSource(
                fixture.Options, fixture.PayloadStore, fixture.CreateWorkStateStore()))
            .AssembleAsync(template.Run.RunId, CancellationToken.None);

        Assert.Equal(CreateRunOutcome.Created, created.Outcome);
        Assert.NotNull(state);
        Assert.Equal(promptText, state.UserGoal.Text);
        Assert.Equal(workspace.DisplayIdentityHash, state.WorkspaceIdentitySha256);
        var projectedState = Assert.Single(context.Entries,
            entry => entry.SourceId == "base:session-work-state");
        Assert.Contains("untrusted=true", projectedState.Content, StringComparison.Ordinal);
        Assert.Contains(Event.Value, projectedState.Content, StringComparison.Ordinal);
        Assert.Equal(Event.Value, state.SourceEventId);
        Assert.Equal(run.Run.RunId, state.SourceRunId);
    }

    [Theory]
    [InlineData("IGNORE")]
    [InlineData("ABORT")]
    public async Task Failed_state_update_keeps_receipt_commit_and_projects_stale_state_as_partial(string failure)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var workspace = await fixture.RegisterWorkspaceAsync(1);
        var eventStore = fixture.CreateEventStore();
        var template = fixture.NewRun(1, 1, 1);
        var promptId = new PayloadId("payload_55555555555555555555555555555555");
        var prompt = new HarnessPayloadIntent(promptId, HarnessPayloadKind.UserPrompt,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text, 64, System.Text.Encoding.UTF8.GetBytes("Keep the current workflow."));
        var run = new NewAgentRun(template.Run,
            [new HarnessEventIntent(Event, HarnessEventKind.RunStateChanged, payloadId: promptId)],
            [prompt], [], template.CreationIdentity);
        Assert.Equal(CreateRunOutcome.Created, (await eventStore.TryCreateRunAsync(run, CancellationToken.None)).Outcome);
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var trigger = connection.CreateCommand();
            var raise = failure == "ABORT" ? "RAISE(ABORT, 'synthetic work-state write failure')" : "RAISE(IGNORE)";
            trigger.CommandText = $"""
                CREATE TRIGGER ignore_session_work_state_update
                BEFORE UPDATE ON agent_session_work_states
                BEGIN SELECT {raise}; END;
                """;
            await trigger.ExecuteNonQueryAsync();
        }

        var modelStepId = new ModelStepId("step_77777777777777777777777777777777");
        var stepPayloadId = new PayloadId("payload_66666666666666666666666666666666");
        var stepPayload = new HarnessPayloadIntent(stepPayloadId, HarnessPayloadKind.AssistantStep,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Json, 1024,
            AssistantStepJsonCodec.Serialize(new AssistantStep("The model step completed.", [], AssistantFinishReason.Stop, 1)));
        var afterStep = NextRun(run.Run, 1, 2);
        Assert.Equal(HarnessCommitOutcome.Committed, (await eventStore.CommitAsync(new HarnessCommit(
            run.Run.RunId, 0, afterStep,
            [new HarnessEventIntent(HarnessStoreTestFixture.Event(101), HarnessEventKind.ModelStepCompleted,
                modelStepId: modelStepId, payloadId: stepPayloadId)], [stepPayload], []), CancellationToken.None)).Outcome);

        var receiptId = HarnessStoreTestFixture.Event(102);
        var afterReceipt = NextRun(afterStep, 2, 3);
        var receiptCommit = await eventStore.CommitAsync(new HarnessCommit(
            run.Run.RunId, 1, afterReceipt,
            [new HarnessEventIntent(receiptId, HarnessEventKind.ToolExecutionCompleted,
                modelStepId: modelStepId, providerCallId: "call-without-matching-tool",
                toolOutcome: ToolExecutionOutcome.Succeeded, sideEffectsPossible: false)], [], []),
            CancellationToken.None);
        var snapshot = await fixture.CreateWorkStateStore().GetSnapshotAsync(
            run.Run.SessionId, workspace.WorkspaceId, CancellationToken.None);
        var rendered = SessionWorkStateRenderer.Render(snapshot!.State, snapshot.HasUnrepresentedReceipts);

        Assert.Equal(HarnessCommitOutcome.Committed, receiptCommit.Outcome);
        Assert.False(snapshot.State.HasUnrepresentedReceipts);
        Assert.True(snapshot.HasUnrepresentedReceipts);
        Assert.True((await fixture.CreateWorkStateStore().GetAsync(
            run.Run.SessionId, workspace.WorkspaceId, CancellationToken.None))!.HasUnrepresentedReceipts);
        Assert.Contains("Receipt coverage is partial", rendered, StringComparison.Ordinal);

        var completedAt = HarnessStoreTestFixture.Timestamp.AddMinutes(1);
        var completed = new AgentRun(afterReceipt.RunId, afterReceipt.SessionId, afterReceipt.WorkspaceId,
            new HarnessRunStatus(HarnessRunState.Completed), afterReceipt.Version + 1,
            afterReceipt.LastSequence + 1, afterReceipt.BudgetWindow, afterReceipt.ModelStepsInWindow,
            afterReceipt.ToolCallsInWindow, afterReceipt.Preset, afterReceipt.PolicySnapshotVersion,
            afterReceipt.PolicySnapshotSha256, afterReceipt.CreatedAt, completedAt, terminalAt: completedAt);
        Assert.Equal(HarnessCommitOutcome.Committed, (await eventStore.CommitAsync(new HarnessCommit(
            afterReceipt.RunId, afterReceipt.Version, completed,
            [new HarnessEventIntent(HarnessStoreTestFixture.Event(104), HarnessEventKind.RunStateChanged)],
            [], []), CancellationToken.None)).Outcome);
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TRIGGER ignore_session_work_state_update";
            await drop.ExecuteNonQueryAsync();
        }
        var nextTemplate = fixture.NewRun(2, 1, 1, idempotencyHashCharacter: 'd', requestHashCharacter: 'e');
        var nextPromptId = new PayloadId("payload_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        var nextPrompt = new HarnessPayloadIntent(nextPromptId, HarnessPayloadKind.UserPrompt,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text, 64, System.Text.Encoding.UTF8.GetBytes("Continue safely."));
        var nextRun = new NewAgentRun(nextTemplate.Run,
            [new HarnessEventIntent(HarnessStoreTestFixture.Event(103), HarnessEventKind.RunStateChanged,
                payloadId: nextPromptId)], [nextPrompt], [], nextTemplate.CreationIdentity);
        Assert.Equal(CreateRunOutcome.Created,
            (await eventStore.TryCreateRunAsync(nextRun, CancellationToken.None)).Outcome);
        Assert.True((await fixture.CreateWorkStateStore().GetAsync(
            run.Run.SessionId, workspace.WorkspaceId, CancellationToken.None))!.HasUnrepresentedReceipts);
    }

    [Fact]
    public async Task Ten_production_run_creations_retain_the_foundational_user_request_and_latest_prompt()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var workspace = await fixture.RegisterWorkspaceAsync(1);
        var eventStore = fixture.CreateEventStore();
        for (var turn = 1; turn <= 10; turn++)
        {
            var promptId = new PayloadId($"payload_{turn:x32}");
            var promptText = turn == 1
                ? "Keep the Broker as the only execution authority. " + new string('界', 1100)
                : $"Follow-up turn {turn}: continue the existing work. " + new string('界', 1100);
            var prompt = new HarnessPayloadIntent(promptId, HarnessPayloadKind.UserPrompt,
                HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Utf8Text, 8192, System.Text.Encoding.UTF8.GetBytes(promptText));
            var template = fixture.NewRun(turn, 1, 1,
                idempotencyHashCharacter: TurnHashCharacter(turn),
                requestHashCharacter: TurnHashCharacter(turn), payloads: [prompt]);
            var request = new NewAgentRun(template.Run,
                [new HarnessEventIntent(HarnessStoreTestFixture.Event(turn), HarnessEventKind.RunStateChanged,
                    payloadId: promptId)], [prompt], [], template.CreationIdentity);
            var result = await eventStore.TryCreateRunAsync(request, CancellationToken.None);
            Assert.True(result.Outcome == CreateRunOutcome.Created,
                $"Turn {turn} returned {result.Outcome}: {result.ErrorCode}");
            var completedAt = HarnessStoreTestFixture.Timestamp.AddMinutes(turn);
            var completed = new AgentRun(result.Run.RunId, result.Run.SessionId, result.Run.WorkspaceId,
                new HarnessRunStatus(HarnessRunState.Completed), result.Run.Version + 1,
                result.Run.LastSequence + 1, result.Run.BudgetWindow, result.Run.ModelStepsInWindow,
                result.Run.ToolCallsInWindow, result.Run.Preset, result.Run.PolicySnapshotVersion,
                result.Run.PolicySnapshotSha256, result.Run.CreatedAt, completedAt,
                terminalAt: completedAt);
            Assert.Equal(HarnessCommitOutcome.Committed, (await eventStore.CommitAsync(new HarnessCommit(
                result.Run.RunId, result.Run.Version, completed,
                [new HarnessEventIntent(HarnessStoreTestFixture.Event(100 + turn),
                    HarnessEventKind.RunStateChanged)], [], []), CancellationToken.None)).Outcome);
        }

        var state = await fixture.CreateWorkStateStore().GetAsync(
            HarnessStoreTestFixture.Session(1), workspace.WorkspaceId, CancellationToken.None);

        Assert.NotNull(state);
        Assert.StartsWith("Follow-up turn 10:", state.UserGoal.Text, StringComparison.Ordinal);
        Assert.Contains(state.EarlierUserIntent,
            fact => fact.Text.StartsWith("Keep the Broker as the only execution authority.", StringComparison.Ordinal));
        Assert.Equal(HarnessStoreTestFixture.Run(1).Value, Assert.Single(state.EarlierUserIntent,
            fact => fact.Text.StartsWith("Keep the Broker", StringComparison.Ordinal)).SourceRunId);
        Assert.True(state.EarlierUserIntentOmitted);
        Assert.True(RuAgent.Harness.Core.Context.SessionWorkStateCodec.Serialize(state).Length
            <= RuAgent.Harness.Core.Context.SessionWorkState.MaximumUtf8Bytes);
    }

    private static char TurnHashCharacter(int turn) => turn <= 9 ? (char)('0' + turn) : 'a';

    [Fact]
    public async Task User_prompt_secret_is_redacted_before_work_state_reuse()
    {
        const string secret = "sk-live-state-sentinel";
        await using var fixture = await HarnessStoreTestFixture.CreateAsync([secret]);
        var workspace = await fixture.RegisterWorkspaceAsync(1);
        var template = fixture.NewRun(1, 1, 1);
        var promptId = new PayloadId("payload_55555555555555555555555555555555");
        var promptText = $"Please explain the token {secret} without repeating it.";
        var prompt = new HarnessPayloadIntent(promptId, HarnessPayloadKind.UserPrompt,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text, 512, System.Text.Encoding.UTF8.GetBytes(promptText));
        var run = new NewAgentRun(template.Run,
            [new HarnessEventIntent(Event, HarnessEventKind.RunStateChanged, payloadId: promptId)],
            [prompt], [], template.CreationIdentity);
        Assert.Equal(CreateRunOutcome.Created,
            (await fixture.CreateEventStore().TryCreateRunAsync(run, CancellationToken.None)).Outcome);

        var state = await fixture.CreateWorkStateStore().GetAsync(template.Run.SessionId,
            workspace.WorkspaceId, CancellationToken.None);
        await fixture.PayloadStore.CaptureBaseContextAsync(template.Run.RunId,
            [new HarnessBaseContextEntry(new PayloadId("payload_66666666666666666666666666666666"),
                "base:prior-user", 1, ModelContextRole.User, "Prior session work")],
            HarnessStoreTestFixture.Timestamp.AddSeconds(1), CancellationToken.None);
        var context = await new AgentContextAssembler(new SqliteModelContextSource(
                fixture.Options, fixture.PayloadStore, fixture.CreateWorkStateStore()))
            .AssembleAsync(template.Run.RunId, CancellationToken.None);
        var clientPrompt = await fixture.PayloadStore.ReadForClientAsync(template.Run.RunId, promptId,
            CancellationToken.None);

        Assert.DoesNotContain(secret, state!.UserGoal.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, System.Text.Encoding.UTF8.GetString(clientPrompt.Plaintext.Span), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, Assert.Single(context.Entries,
            entry => entry.SourceId == "base:session-work-state").Content, StringComparison.Ordinal);
        Assert.Equal(Event.Value, state.SourceEventId);
    }

    [Fact]
    public async Task Only_a_successful_durable_todo_receipt_updates_model_task_candidates()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var workspace = await fixture.RegisterWorkspaceAsync(1);
        var template = fixture.NewRun(1, 1, 1);
        var promptId = new PayloadId("payload_55555555555555555555555555555555");
        var prompt = new HarnessPayloadIntent(promptId, HarnessPayloadKind.UserPrompt,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text, 64, System.Text.Encoding.UTF8.GetBytes("Implement a parser fix."));
        var run = new NewAgentRun(template.Run,
            [new HarnessEventIntent(Event, HarnessEventKind.RunStateChanged, payloadId: promptId)],
            [prompt], [], template.CreationIdentity);
        var store = fixture.CreateEventStore();
        Assert.Equal(CreateRunOutcome.Created, (await store.TryCreateRunAsync(run, CancellationToken.None)).Outcome);

        using var arguments = System.Text.Json.JsonDocument.Parse(
            "{\"todos\":[{\"content\":\"Run parser tests\",\"status\":\"pending\"}]}");
        var call = new ModelToolCall("provider-todo", "todo_write", "builtin", arguments.RootElement);
        var stepId = new ModelStepId("step_77777777777777777777777777777777");
        var stepPayload = new HarnessPayloadIntent(new PayloadId("payload_88888888888888888888888888888888"),
            HarnessPayloadKind.AssistantStep,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Json, 4096,
            AssistantStepJsonCodec.Serialize(new AssistantStep(null, [call], AssistantFinishReason.ToolCalls, 1)));
        var stepRun = NextRun(run.Run, 1, 2);
        Assert.Equal(HarnessCommitOutcome.Committed, (await store.CommitAsync(new HarnessCommit(
            run.Run.RunId, 0, stepRun,
            [new HarnessEventIntent(new EventId("evt_99999999999999999999999999999999"),
                HarnessEventKind.ModelStepCompleted, modelStepId: stepId, payloadId: stepPayload.PayloadId)],
            [stepPayload], []), CancellationToken.None)).Outcome);

        var beforeReceipt = await fixture.CreateWorkStateStore().GetAsync(run.Run.SessionId,
            workspace.WorkspaceId, CancellationToken.None);
        Assert.Empty(beforeReceipt!.Tasks);
        var receiptId = new EventId("evt_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var receiptRun = NextRun(stepRun, 2, 3);
        var receiptResult = await store.CommitAsync(new HarnessCommit(
            run.Run.RunId, 1, receiptRun,
            [new HarnessEventIntent(receiptId, HarnessEventKind.ToolExecutionCompleted,
                modelStepId: stepId, providerCallId: call.ProviderCallId,
                toolOutcome: ToolExecutionOutcome.Succeeded, sideEffectsPossible: false)],
            [], []), CancellationToken.None);

        Assert.Equal(HarnessCommitOutcome.Committed, receiptResult.Outcome);
        var afterReceipt = await fixture.CreateWorkStateStore().GetAsync(run.Run.SessionId,
            workspace.WorkspaceId, CancellationToken.None);
        Assert.False(afterReceipt!.HasUnrepresentedReceipts);
        var task = Assert.Single(afterReceipt.Tasks);
        Assert.Equal("Run parser tests", task.Text);
        Assert.Equal(SessionWorkItemSource.Model, task.Source);
        Assert.Equal(receiptId.Value, task.SourceEventId);

        var fileReceipts = await CommitFileObservationReceiptsAsync(fixture, store, receiptRun);
        var observed = (await fixture.CreateWorkStateStore().GetAsync(run.Run.SessionId,
            workspace.WorkspaceId, CancellationToken.None))!.ObservedFiles;
        Assert.Equal(new[] { "src/read.cs", "src/write.cs", "src/edit.cs" }, observed.Select(file => file.Path));
        Assert.All(observed, file => Assert.Equal(new string('d', 64), file.Sha256));
        Assert.Equal(fileReceipts.Take(3).Select(id => id.Value), observed.Select(file => file.SourceEventId));

        var stateAfterFiles = await fixture.CreateWorkStateStore().GetAsync(run.Run.SessionId,
            workspace.WorkspaceId, CancellationToken.None);
        Assert.True(stateAfterFiles!.HasUnrepresentedReceipts);
        var verification = Assert.Single(stateAfterFiles.Verifications);
        Assert.Equal("dotnet test ParserTests", verification.Command);
        Assert.Equal("process_exit_code_0", verification.OutcomeCode);
        Assert.True(verification.Succeeded);
        Assert.Equal(fileReceipts[3].Value, verification.SourceEventId);
        var forgedGoal = new SessionWorkState(stateAfterFiles!.SessionId, stateAfterFiles.WorkspaceId,
            stateAfterFiles.WorkspaceIdentitySha256, stateAfterFiles.SourceRunId, stateAfterFiles.Version + 1,
            receiptId.Value, new SessionWorkFact("forged user intent", run.Run.RunId.Value, receiptId.Value),
            stateAfterFiles.UserConstraints, stateAfterFiles.EarlierUserIntent, stateAfterFiles.EarlierUserIntentOmitted,
            stateAfterFiles.HasUnrepresentedReceipts,
            stateAfterFiles.Tasks, stateAfterFiles.ObservedFiles, stateAfterFiles.Verifications,
            stateAfterFiles.Blocker, stateAfterFiles.NextAction);
        var mismatch = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            fixture.CreateWorkStateStore().SaveAsync(forgedGoal, stateAfterFiles.Version,
                HarnessStoreTestFixture.Timestamp.AddMinutes(1), CancellationToken.None));
        Assert.Equal("session_work_state_source_kind_mismatch", mismatch.Code);
    }

    private static AgentRun NextRun(AgentRun current, long version, long lastSequence) => new(
        current.RunId, current.SessionId, current.WorkspaceId,
        new HarnessRunStatus(HarnessRunState.Running), version, lastSequence,
        current.BudgetWindow, current.ModelStepsInWindow, current.ToolCallsInWindow,
        current.Preset, current.PolicySnapshotVersion, current.PolicySnapshotSha256,
        current.CreatedAt, current.UpdatedAt.AddSeconds(version));

    private static async Task<IReadOnlyList<EventId>> CommitFileObservationReceiptsAsync(
        HarnessStoreTestFixture fixture,
        SqliteHarnessEventStore store,
        AgentRun current)
    {
        var calls = new[]
        {
            CreateCall("provider-read", "read", "src/read.cs"),
            CreateCall("provider-write", "write", "src/write.cs"),
            CreateCall("provider-edit", "edit", "src/edit.cs"),
            CreateCall("provider-bash", "bash", ""),
            CreateCall("provider-glob", "glob", ""),
        };
        var modelStepId = new ModelStepId("step_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var assistantPayload = new HarnessPayloadIntent(
            new PayloadId("payload_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), HarnessPayloadKind.AssistantStep,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Json, 4096,
            AssistantStepJsonCodec.Serialize(new AssistantStep(null, calls, AssistantFinishReason.ToolCalls, 1)));
        var events = new List<HarnessEventIntent>
        {
            new(new EventId("evt_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), HarnessEventKind.ModelStepCompleted,
                modelStepId: modelStepId, payloadId: assistantPayload.PayloadId),
        };
        var outputs = new List<HarnessPayloadIntent>();
        var receiptIds = new List<EventId>();
        var nextIndex = 0;
        foreach (var call in calls)
        {
            var payloadId = PayloadIdFor(100 + nextIndex);
            var receiptId = EventIdFor(100 + nextIndex);
            var hashName = call.ToolId == "read" ? "sha256" : "after_sha256";
            var json = call.ToolId switch
            {
                "read" or "write" or "edit" => $$"""{"path":"src/{{call.ToolId}}.cs","{{hashName}}":"{{new string('d', 64)}}"}""",
                "bash" => "{\"exit_code\":0}",
                _ => "{}",
            };
            outputs.Add(new HarnessPayloadIntent(payloadId, HarnessPayloadKind.ToolOutput,
                HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Json, 512, System.Text.Encoding.UTF8.GetBytes(json)));
            events.Add(new HarnessEventIntent(receiptId, HarnessEventKind.ToolExecutionCompleted,
                modelStepId: modelStepId, providerCallId: call.ProviderCallId, payloadId: payloadId,
                safeCode: "succeeded", toolOutcome: ToolExecutionOutcome.Succeeded, sideEffectsPossible: false));
            receiptIds.Add(receiptId);
            nextIndex++;
        }
        var stepRun = NextRun(current, current.Version + 1, current.LastSequence + 1);
        var receiptRun = NextRun(stepRun, stepRun.Version + 1, stepRun.LastSequence + calls.Length);
        var result = await store.CommitAsync(new HarnessCommit(current.RunId, current.Version, stepRun,
            [events[0]], [assistantPayload], []), CancellationToken.None);
        Assert.Equal(HarnessCommitOutcome.Committed, result.Outcome);
        result = await store.CommitAsync(new HarnessCommit(stepRun.RunId, stepRun.Version, receiptRun,
            events.Skip(1).ToArray(), outputs, []), CancellationToken.None);
        Assert.Equal(HarnessCommitOutcome.Committed, result.Outcome);
        return receiptIds;
    }

    private static ModelToolCall CreateCall(string id, string toolId, string path)
    {
        using var arguments = System.Text.Json.JsonDocument.Parse(toolId == "bash"
            ? "{\"command\":\"dotnet test ParserTests\"}"
            : $$"""{"path":"{{path}}","content":"updated","op":"write"}""");
        return new ModelToolCall(id, toolId, "builtin", arguments.RootElement);
    }

    private static PayloadId PayloadIdFor(int number) => new("payload_" + number.ToString("x32"));

    private static EventId EventIdFor(int number) => new("evt_" + number.ToString("x32"));

    private static async Task SeedSourceRunAsync(HarnessStoreTestFixture fixture, WorkspaceId workspaceId)
    {
        var promptId = new PayloadId("payload_55555555555555555555555555555555");
        var run = new AgentRun(Run, Session, workspaceId, new HarnessRunStatus(HarnessRunState.Created),
            0, 1, 1, 0, 0, AgentPermissionPreset.Standard, 1, new string('c', 64),
            HarnessStoreTestFixture.Timestamp, HarnessStoreTestFixture.Timestamp);
        var request = new NewAgentRun(run,
            [new HarnessEventIntent(Event, HarnessEventKind.RunStateChanged, payloadId: promptId)],
            [new HarnessPayloadIntent(promptId, HarnessPayloadKind.UserPrompt,
                HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Utf8Text, 16, System.Text.Encoding.UTF8.GetBytes("Source prompt."))], [],
            new RunCreationIdentity(new string('a', 64), new string('b', 64), 1));
        Assert.Equal(CreateRunOutcome.Created,
            (await fixture.CreateEventStore().TryCreateRunAsync(request, CancellationToken.None)).Outcome);
    }

    private static SessionWorkState State(WorkspaceId workspaceId, string workspaceHash, long version) => new(
        Session,
        workspaceId,
        workspaceHash,
        Run,
        version,
        Event.Value,
        new SessionWorkFact("explicitly bounded user goal", Run.Value, Event.Value),
        [new SessionWorkFact("Do not execute external actions", Run.Value, Event.Value)],
        [], false, false, [], [], [], null, null);
}
