using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Core.Tests.Fakes;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class AgentRunCoordinatorTests
{
    [Fact]
    public async Task Null_provider_call_id_fails_before_model_step_commit()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(
            new NullProviderCallRunner(),
            new FakeToolGateway(),
            store);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            coordinator.RunOneStepAsync(RunId(), CancellationToken.None));

        Assert.Equal("providerCallId", error.ParamName);
        Assert.DoesNotContain(
            store.Commits.SelectMany(commit => commit.Payloads),
            payload => payload.Kind == HarnessPayloadKind.AssistantStep);
        Assert.DoesNotContain(
            store.Commits.SelectMany(commit => commit.Events),
            intent => intent.Kind == HarnessEventKind.ModelStepCompleted);
    }

    private static readonly DateTimeOffset Epoch =
        new(2026, 8, 24, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task User_history_ack_gates_first_step_and_final_history_ack_gates_completion()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("done"));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(
            HarnessRunState.WaitingHistory,
            HistoryWaitKind.UserBeforeFirstStep)));
        var coordinator = Coordinator(model, new FakeToolGateway(), store);

        var blockedBeforeUserHistory = await coordinator.RunOneStepAsync(
            RunId(),
            CancellationToken.None);
        Assert.Equal("run_not_runnable", blockedBeforeUserHistory.ErrorCode);
        Assert.Empty(model.Requests);

        var running = await coordinator.AcknowledgeHistoryAsync(
            RunId(),
            HistoryWaitKind.UserBeforeFirstStep,
            CancellationToken.None);
        Assert.Equal(HarnessRunState.Running, running.State);

        var final = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);
        Assert.Null(final.ErrorCode);
        Assert.Equal(HarnessRunState.WaitingHistory, final.Run.State);
        Assert.Equal(HistoryWaitKind.FinalBeforeCompletion, final.Run.HistoryWaitKind);
        Assert.Single(model.Requests);
        var finalCommit = Assert.Single(
            store.Commits,
            commit => commit.ConversationOutbox.Count == 1);
        Assert.Contains(finalCommit.Payloads, payload => payload.Kind == HarnessPayloadKind.PromptReceipt);
        Assert.Contains(finalCommit.Payloads, payload => payload.Kind == HarnessPayloadKind.AssistantText);

        var blockedBeforeFinalHistory = await coordinator.RunOneStepAsync(
            RunId(),
            CancellationToken.None);
        Assert.Equal("run_not_runnable", blockedBeforeFinalHistory.ErrorCode);
        Assert.Single(model.Requests);

        var completed = await coordinator.AcknowledgeHistoryAsync(
            RunId(),
            HistoryWaitKind.FinalBeforeCompletion,
            CancellationToken.None);
        Assert.Equal(HarnessRunState.Completed, completed.State);
    }

    [Fact]
    public async Task Final_reply_persists_policy_visible_text_and_runs_effects_after_commit()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("raw [[VTS:smile]]"));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var policy = new RecordingReplyPolicy(
            new AgentReplyPresentation("visible", emotion: "joy"));
        var effects = new RecordingReplyEffects();
        var coordinator = Coordinator(
            model,
            new FakeToolGateway(),
            store,
            replyPolicy: policy,
            replyEffects: effects);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.WaitingHistory, result.Run.State);
        var commit = Assert.Single(
            store.Commits,
            item => item.ConversationOutbox.Count == 1);
        var assistantPayload = Assert.Single(
            commit.Payloads,
            payload => payload.Kind == HarnessPayloadKind.AssistantText);
        Assert.Equal("visible", Encoding.UTF8.GetString(assistantPayload.Plaintext.Span));
        Assert.Equal("visible", effects.Reply?.VisibleText);
        Assert.Equal(2, effects.RunVersionAtCall);
    }

    [Fact]
    public async Task Final_reply_persists_provider_usage_with_the_assistant_step()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("done", new ModelUsage(12, 5, 17, 3, null)));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, new FakeToolGateway(), store);

        await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        var commit = Assert.Single(store.Commits, item => item.ConversationOutbox.Count == 1);
        var payload = Assert.Single(commit.Payloads, item => item.Kind == HarnessPayloadKind.ModelUsage);
        var usageEvent = Assert.Single(commit.Events, item => item.Kind == HarnessEventKind.ModelUsageRecorded);
        Assert.Equal(payload.PayloadId, usageEvent.PayloadId);
        using var document = JsonDocument.Parse(payload.Plaintext);
        Assert.Equal(12, document.RootElement.GetProperty("input_tokens").GetInt32());
        Assert.Equal(5, document.RootElement.GetProperty("output_tokens").GetInt32());
        Assert.Equal(17, document.RootElement.GetProperty("total_tokens").GetInt32());
        Assert.Equal(3, document.RootElement.GetProperty("cache_read_input_tokens").GetInt32());
    }

    [Fact]
    public async Task Final_reply_policy_failure_prevents_final_commit()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("raw"));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(
            model,
            new FakeToolGateway(),
            store,
            replyPolicy: new ThrowingReplyPolicy());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.RunOneStepAsync(RunId(), CancellationToken.None));

        Assert.DoesNotContain(
            store.Commits,
            commit => commit.ConversationOutbox.Count > 0);
    }

    [Fact]
    public async Task Final_reply_effect_failure_does_not_fail_committed_run()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("visible"));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(
            model,
            new FakeToolGateway(),
            store,
            replyEffects: new ThrowingReplyEffects());

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.WaitingHistory, result.Run.State);
        Assert.Single(
            store.Commits,
            commit => commit.ConversationOutbox.Count == 1);
    }

    [Fact]
    public async Task Permanent_history_failure_terminates_the_run_with_a_safe_error()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(
            HarnessRunState.WaitingHistory,
            HistoryWaitKind.UserBeforeFirstStep)));
        var coordinator = Coordinator(
            new FakeModelStepRunner(),
            new FakeToolGateway(),
            store);

        var failed = await coordinator.FailAsync(
            RunId(),
            "session_not_found",
            CancellationToken.None);

        Assert.Equal(HarnessRunState.Failed, failed.State);
        Assert.Equal("session_not_found", failed.TerminalErrorCode);
        var failure = Assert.Single(
            Assert.Single(store.Commits).Events,
            item => item.Kind == HarnessEventKind.RunFailed);
        Assert.Equal("session_not_found", failure.SafeCode);
    }

    [Fact]
    public async Task Preparation_payload_and_execution_request_commit_before_dispatch()
    {
        var trace = new List<string>();
        var model = new FakeModelStepRunner(trace);
        model.Enqueue(ToolStep());
        var gateway = new FakeToolGateway(trace: trace)
        {
            PreparationPayloads = new[]
            {
                new PreparedPayload(
                    HarnessPayloadKind.RecoveryOpaque,
                    HarnessPayloadAudience.InternalOnly,
                    HarnessPayloadEncoding.Opaque,
                    64,
                    new byte[] { 0, 1, 2 }),
            },
        };
        var store = new InMemoryHarnessEventStore(trace);
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, gateway, store);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.Running, result.Run.State);
        var modelCompletedCommit = Assert.Single(
            store.Commits,
            commit => commit.Events.Any(item =>
                item.Kind == HarnessEventKind.ModelStepCompleted));
        var assistantPayload = Assert.Single(
            modelCompletedCommit.Payloads,
            payload => payload.Kind == HarnessPayloadKind.AssistantStep);
        Assert.Equal(HarnessPayloadKind.AssistantStep, assistantPayload.Kind);
        Assert.Equal(
            "files.read",
            Assert.Single(AssistantStepJsonCodec.Deserialize(assistantPayload.Plaintext).ToolCalls).ToolId);
        var proposedCommit = Assert.Single(
            store.Commits,
            commit => commit.Events.Any(item => item.Kind == HarnessEventKind.ToolProposed));
        var arguments = Assert.Single(proposedCommit.Payloads);
        Assert.Equal(HarnessPayloadKind.ToolArguments, arguments.Kind);
        var proposedEvent = Assert.Single(proposedCommit.Events);
        Assert.Equal(arguments.PayloadId, proposedEvent.PayloadId);
        Assert.Equal("provider-call", proposedEvent.ProviderCallId);
        using (var document = JsonDocument.Parse(arguments.Plaintext))
        {
            Assert.Equal("provider-call", document.RootElement.GetProperty("provider_call_id").GetString());
            Assert.Equal("files.read", document.RootElement.GetProperty("tool_id").GetString());
            Assert.Equal("1", document.RootElement.GetProperty("tool_version").GetString());
            Assert.Equal(
                "README.md",
                document.RootElement.GetProperty("arguments").GetProperty("path").GetString());
        }
        var requestedCommit = Assert.Single(
            store.Commits,
            commit => commit.Events.Any(item =>
                item.Kind == HarnessEventKind.ToolExecutionRequested));
        Assert.Single(requestedCommit.Payloads);
        Assert.Equal("provider-call", Assert.Single(requestedCommit.Events).ProviderCallId);
        Assert.True(trace.IndexOf("prepare") < trace.IndexOf("commit:ToolExecutionRequested"));
        Assert.True(trace.IndexOf("commit:ToolExecutionRequested") < trace.IndexOf("execute"));
        Assert.True(trace.IndexOf("execute") < trace.IndexOf("commit:ToolExecutionCompleted"));
        var receiptCommit = Assert.Single(
            store.Commits,
            commit => commit.Events.Any(item =>
                item.Kind == HarnessEventKind.ToolExecutionCompleted));
        var resultPayload = Assert.Single(receiptCommit.Payloads);
        Assert.Equal(HarnessPayloadKind.ToolOutput, resultPayload.Kind);
        Assert.Equal(
            resultPayload.PayloadId,
            Assert.Single(result.Receipts).ResultPayloadId);
        Assert.Equal("provider-call", Assert.Single(receiptCommit.Events).ProviderCallId);
        Assert.Equal("provider-call", Assert.Single(result.Receipts).ProviderCallId);

        // Naming the payload must not empty the content. This assertion used to require
        // IsEmpty, which recorded a bug as intended behaviour: readers downstream consult
        // ModelContent to build the edit preview and to raise a Search row's match count,
        // so emptying it made a successful call look like it returned nothing. An empty
        // result is a legal thing for a tool to return, which is why nothing failed loudly.
        Assert.False(Assert.Single(result.Receipts).ModelContent.IsEmpty);
    }

    [Fact]
    public async Task Receipt_is_durable_before_the_next_model_step()
    {
        var trace = new List<string>();
        var model = new FakeModelStepRunner(trace);
        model.Enqueue(ToolStep());
        model.Enqueue(FinalStep("done"));
        var store = new InMemoryHarnessEventStore(trace);
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, new FakeToolGateway(trace: trace), store);

        await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);
        await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.True(trace.IndexOf("commit:ToolExecutionCompleted") < trace.IndexOf("model:2"));
        Assert.Equal(2, model.Requests.Count);
    }

    [Fact]
    public async Task Repeated_completed_tool_call_gets_a_repair_note_before_second_execution()
    {
        var prior = ToolStep();
        var source = new FixedContextSource(
            ModelContextEntry.Assistant("assistant-prior", 1, prior),
            ModelContextEntry.ToolResult(
                "tool-prior",
                2,
                new ToolExecutionReceipt(
                    new ExecutionId("exec_11111111111111111111111111111111"),
                    new ToolCallId("call_11111111111111111111111111111111"),
                    ToolExecutionOutcome.Failed,
                    "malformed_call",
                    null,
                    sideEffectsPossible: false,
                    providerCallId: "provider_call"),
                "{}"));
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep());
        var gateway = new FakeToolGateway();
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, gateway, store, contextSource: source);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.Running, result.Run.State);
        Assert.Null(result.ErrorCode);
        Assert.Empty(gateway.ExecuteAttempts);
        var events = store.Commits.SelectMany(commit => commit.Events).ToArray();
        Assert.Contains(events, item =>
            item.Kind == HarnessEventKind.ModelStepCompleted
            && item.SafeCode == "repeated_tool_call_advised");
        Assert.Contains(events, item =>
            item.Kind == HarnessEventKind.RunNoteAdded
            && item.SafeCode == "repeated_tool_call");
        Assert.DoesNotContain(events, item => item.Kind == HarnessEventKind.RunFailed);
        Assert.Contains(
            store.Commits.SelectMany(commit => commit.Payloads),
            payload => payload.Kind == HarnessPayloadKind.ModelContextNote);
    }

    [Fact]
    public async Task Repetition_after_the_repair_budget_parks_the_run_for_continue()
    {
        var prior = ToolStep();
        var notes = Enumerable.Range(0, ConsecutiveToolCallGuard.MaximumRepairNotes)
            .Select(index => ModelContextEntry.Text(
                $"note-{index}",
                index + 1,
                ModelContextRole.System,
                ConsecutiveToolCallGuard.BuildRepairNote("malformed_call")))
            .ToArray();
        var source = new FixedContextSource(
            [
                .. notes,
                ModelContextEntry.Assistant("assistant-prior", 10, prior),
                ModelContextEntry.ToolResult(
                    "tool-prior",
                    11,
                    new ToolExecutionReceipt(
                        new ExecutionId("exec_11111111111111111111111111111111"),
                        new ToolCallId("call_11111111111111111111111111111111"),
                        ToolExecutionOutcome.Failed,
                        "malformed_call",
                        null,
                        sideEffectsPossible: false,
                    providerCallId: "provider_call"),
                    "{}"),
            ]);
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep());
        var gateway = new FakeToolGateway();
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, gateway, store, contextSource: source);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.WaitingUser, result.Run.State);
        Assert.Equal("repeated_tool_call", result.ErrorCode);
        Assert.Null(result.Run.TerminalErrorCode);
        Assert.Empty(gateway.ExecuteAttempts);
        var events = store.Commits.SelectMany(commit => commit.Events).ToArray();
        Assert.Contains(events, item =>
            item.Kind == HarnessEventKind.ModelStepCompleted
            && item.SafeCode == "repeated_tool_call");
        Assert.Contains(events, item =>
            item.Kind == HarnessEventKind.ContinueRequested
            && item.SafeCode == "repeated_tool_call");
        Assert.DoesNotContain(events, item => item.Kind == HarnessEventKind.RunFailed);
    }

    [Fact]
    public async Task Changed_tool_arguments_execute_after_a_completed_call()
    {
        var source = new FixedContextSource(
            ModelContextEntry.Assistant("assistant-prior", 1, ToolStep()),
            ModelContextEntry.ToolResult(
                "tool-prior",
                2,
                new ToolExecutionReceipt(
                    new ExecutionId("exec_11111111111111111111111111111111"),
                    new ToolCallId("call_11111111111111111111111111111111"),
                    ToolExecutionOutcome.Succeeded,
                    "ok",
                    null,
                    sideEffectsPossible: false,
                    providerCallId: "provider_call"),
                "{}"));
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep(path: "docs/README.md"));
        var gateway = new FakeToolGateway();
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, gateway, store, contextSource: source);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.Running, result.Run.State);
        Assert.Single(gateway.ExecuteAttempts);
    }

    [Fact]
    public async Task Final_prose_after_failed_tool_gets_a_recovery_note_before_fail()
    {
        var prior = ToolStep();
        const string toolErrorCode = "workspace_path_outside_root";
        var source = new FixedContextSource(
            ModelContextEntry.Assistant("assistant-prior", 1, prior),
            ModelContextEntry.ToolResult(
                "tool-prior",
                2,
                new ToolExecutionReceipt(
                    new ExecutionId("exec_11111111111111111111111111111111"),
                    new ToolCallId("call_11111111111111111111111111111111"),
                    ToolExecutionOutcome.Failed,
                    toolErrorCode,
                    null,
                    sideEffectsPossible: false,
                    providerCallId: "provider_call"),
                "{}"));
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("I succeeded in running the command!"));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var activities = new RecordingActivitySink();
        var coordinator = Coordinator(
            model,
            new FakeToolGateway(),
            store,
            contextSource: source,
            activitySink: activities);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.Running, result.Run.State);
        Assert.Null(result.ErrorCode);
        Assert.Null(result.Run.TerminalErrorCode);
        Assert.Empty(store.Commits.SelectMany(commit => commit.ConversationOutbox));

        var commit = store.Commits.Last();
        var assistantPayload = Assert.Single(
            commit.Payloads,
            payload => payload.Kind == HarnessPayloadKind.AssistantStep);
        Assert.Equal(HarnessPayloadAudience.InternalOnly, assistantPayload.Audience);
        var notePayload = Assert.Single(
            commit.Payloads,
            payload => payload.Kind == HarnessPayloadKind.ModelContextNote);
        Assert.Equal(HarnessPayloadAudience.ModelContext, notePayload.Audience);
        Assert.Equal(HarnessPayloadEncoding.Utf8Text, notePayload.Encoding);
        var noteText = Encoding.UTF8.GetString(notePayload.Plaintext.Span);
        Assert.StartsWith(AgentRecoveryNotes.NotePrefix, noteText, StringComparison.Ordinal);
        Assert.Contains(toolErrorCode, noteText, StringComparison.Ordinal);

        Assert.Contains(commit.Events, item =>
            item.Kind == HarnessEventKind.ModelStepCompleted
            && item.SafeCode == "unverified_after_tool_failure_advised");
        Assert.Contains(commit.Events, item =>
            item.Kind == HarnessEventKind.RunNoteAdded
            && item.SafeCode == "unverified_after_tool_failure"
            && item.PayloadId == notePayload.PayloadId);
        Assert.DoesNotContain(commit.Events, item => item.Kind == HarnessEventKind.RunFailed);

        var retry = Assert.Single(
            activities.Activities,
            activity => activity.Kind == AgentActivityKind.Retry);
        Assert.Equal(1, retry.Count);
    }

    [Fact]
    public async Task Final_prose_after_failed_tool_fails_after_recovery_notes_are_spent()
    {
        var prior = ToolStep();
        const string toolErrorCode = "workspace_path_outside_root";
        var notes = Enumerable.Range(0, AgentRecoveryNotes.MaximumUnverifiedNotes)
            .Select(index => ModelContextEntry.Text(
                $"note-{index}",
                index + 1,
                ModelContextRole.System,
                AgentRecoveryNotes.BuildUnverifiedNote("malformed_call")))
            .ToArray();
        var source = new FixedContextSource(
            [
                .. notes,
                ModelContextEntry.Assistant("assistant-prior", 10, prior),
                ModelContextEntry.ToolResult(
                    "tool-prior",
                    11,
                    new ToolExecutionReceipt(
                        new ExecutionId("exec_11111111111111111111111111111111"),
                        new ToolCallId("call_11111111111111111111111111111111"),
                        ToolExecutionOutcome.Failed,
                        toolErrorCode,
                        null,
                        sideEffectsPossible: false,
                        providerCallId: "provider_call"),
                    "{}"),
            ]);
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("I succeeded in running the command!"));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, new FakeToolGateway(), store, contextSource: source);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.Failed, result.Run.State);
        Assert.Equal(toolErrorCode, result.ErrorCode);
        Assert.Equal(toolErrorCode, result.Run.TerminalErrorCode);

        Assert.Empty(store.Commits.SelectMany(commit => commit.ConversationOutbox));

        var commit = store.Commits.Last();
        var assistantPayload = Assert.Single(
            commit.Payloads,
            payload => payload.Kind == HarnessPayloadKind.AssistantStep);
        Assert.Equal(HarnessPayloadAudience.InternalOnly, assistantPayload.Audience);

        var stepCompleted = Assert.Single(
            commit.Events,
            e => e.Kind == HarnessEventKind.ModelStepCompleted);
        Assert.Equal("unverified_after_tool_failure", stepCompleted.SafeCode);

        var runFailed = Assert.Single(
            commit.Events,
            e => e.Kind == HarnessEventKind.RunFailed);
        Assert.Equal(toolErrorCode, runFailed.SafeCode);
        Assert.DoesNotContain(commit.Events, item => item.Kind == HarnessEventKind.RunNoteAdded);
        Assert.DoesNotContain(
            commit.Payloads,
            payload => payload.Kind == HarnessPayloadKind.ModelContextNote);
    }

    [Fact]
    public async Task Final_prose_after_failed_tool_fails_under_the_global_recovery_cap()
    {
        var prior = ToolStep();
        const string toolErrorCode = "workspace_path_outside_root";
        var notes = Enumerable.Range(0, AgentRecoveryNotes.MaximumNotesPerWindow)
            .Select(index => ModelContextEntry.Text(
                $"note-{index}",
                index + 1,
                ModelContextRole.System,
                $"{AgentRecoveryNotes.NotePrefix} tool_call_format: fixture note {index}"))
            .ToArray();
        var source = new FixedContextSource(
            [
                .. notes,
                ModelContextEntry.Assistant("assistant-prior", 10, prior),
                ModelContextEntry.ToolResult(
                    "tool-prior",
                    11,
                    new ToolExecutionReceipt(
                        new ExecutionId("exec_11111111111111111111111111111111"),
                        new ToolCallId("call_11111111111111111111111111111111"),
                        ToolExecutionOutcome.Failed,
                        toolErrorCode,
                        null,
                        sideEffectsPossible: false,
                        providerCallId: "provider_call"),
                    "{}"),
            ]);
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("I succeeded in running the command!"));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, new FakeToolGateway(), store, contextSource: source);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.Failed, result.Run.State);
        Assert.Equal(toolErrorCode, result.ErrorCode);
        Assert.Equal(toolErrorCode, result.Run.TerminalErrorCode);
        var commit = store.Commits.Last();
        Assert.Contains(commit.Events, item =>
            item.Kind == HarnessEventKind.RunFailed && item.SafeCode == toolErrorCode);
        Assert.DoesNotContain(commit.Events, item => item.Kind == HarnessEventKind.RunNoteAdded);
        Assert.DoesNotContain(
            commit.Payloads,
            payload => payload.Kind == HarnessPayloadKind.ModelContextNote);
    }

    [Fact]
    public async Task Empty_final_step_gets_a_recovery_note_before_fail()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep(string.Empty));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var activities = new RecordingActivitySink();
        var coordinator = Coordinator(
            model,
            new FakeToolGateway(),
            store,
            activitySink: activities);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.Running, result.Run.State);
        Assert.Null(result.ErrorCode);
        Assert.Null(result.Run.TerminalErrorCode);
        Assert.Empty(store.Commits.SelectMany(commit => commit.ConversationOutbox));

        var commit = store.Commits.Last();
        var notePayload = Assert.Single(
            commit.Payloads,
            payload => payload.Kind == HarnessPayloadKind.ModelContextNote);
        Assert.Equal(HarnessPayloadAudience.ModelContext, notePayload.Audience);
        Assert.Equal(HarnessPayloadEncoding.Utf8Text, notePayload.Encoding);
        var noteText = Encoding.UTF8.GetString(notePayload.Plaintext.Span);
        Assert.StartsWith(AgentRecoveryNotes.NotePrefix, noteText, StringComparison.Ordinal);
        Assert.Contains("empty_model_reply", noteText, StringComparison.Ordinal);

        Assert.Contains(commit.Events, item =>
            item.Kind == HarnessEventKind.ModelStepCompleted
            && item.SafeCode == "empty_model_reply_advised");
        Assert.Contains(commit.Events, item =>
            item.Kind == HarnessEventKind.RunNoteAdded
            && item.SafeCode == "empty_model_reply"
            && item.PayloadId == notePayload.PayloadId);
        Assert.DoesNotContain(commit.Events, item => item.Kind == HarnessEventKind.RunFailed);

        var retry = Assert.Single(
            activities.Activities,
            activity => activity.Kind == AgentActivityKind.Retry);
        Assert.Equal(1, retry.Count);
    }

    [Fact]
    public async Task Empty_final_step_fails_the_run_after_the_recovery_note_is_spent()
    {
        var source = new FixedContextSource(
            ModelContextEntry.Text(
                "note-0",
                1,
                ModelContextRole.System,
                AgentRecoveryNotes.BuildEmptyNote()));
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep(string.Empty));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, new FakeToolGateway(), store, contextSource: source);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.Failed, result.Run.State);
        Assert.Equal("empty_model_reply", result.ErrorCode);
        Assert.Equal("empty_model_reply", result.Run.TerminalErrorCode);

        var commit = store.Commits.Last();
        var stepCompleted = Assert.Single(
            commit.Events,
            e => e.Kind == HarnessEventKind.ModelStepCompleted);
        Assert.Equal("empty_model_reply", stepCompleted.SafeCode);
        var runFailed = Assert.Single(
            commit.Events,
            e => e.Kind == HarnessEventKind.RunFailed);
        Assert.Equal("empty_model_reply", runFailed.SafeCode);
        Assert.DoesNotContain(commit.Events, item => item.Kind == HarnessEventKind.RunNoteAdded);
        Assert.DoesNotContain(
            commit.Payloads,
            payload => payload.Kind == HarnessPayloadKind.ModelContextNote);
        Assert.Empty(commit.ConversationOutbox);
    }

    [Fact]
    public async Task Hard_model_step_failure_does_not_take_the_recovery_note_path()
    {
        var prior = ToolStep();
        var source = new FixedContextSource(
            ModelContextEntry.Assistant("assistant-prior", 1, prior),
            ModelContextEntry.ToolResult(
                "tool-prior",
                2,
                new ToolExecutionReceipt(
                    new ExecutionId("exec_11111111111111111111111111111111"),
                    new ToolCallId("call_11111111111111111111111111111111"),
                    ToolExecutionOutcome.Failed,
                    "workspace_path_outside_root",
                    null,
                    sideEffectsPossible: false,
                    providerCallId: "provider_call"),
                "{}"));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(
            new ThrowingModelStepRunner(),
            new FakeToolGateway(),
            store,
            contextSource: source);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            coordinator.RunOneStepAsync(RunId(), CancellationToken.None));

        var run = await store.GetRunAsync(RunId(), CancellationToken.None);
        Assert.Equal(HarnessRunState.Failed, run!.State);
        Assert.Equal("model_timeout", run.TerminalErrorCode);
        Assert.DoesNotContain(
            store.Commits.SelectMany(commit => commit.Events),
            item => item.Kind == HarnessEventKind.RunNoteAdded);
        Assert.DoesNotContain(
            store.Commits.SelectMany(commit => commit.Payloads),
            payload => payload.Kind == HarnessPayloadKind.ModelContextNote);
    }

    [Fact]
    public async Task Recovery_tool_call_after_failed_tool_executes_normally()
    {
        var prior = ToolStep();
        var source = new FixedContextSource(
            ModelContextEntry.Assistant("assistant-prior", 1, prior),
            ModelContextEntry.ToolResult(
                "tool-prior",
                2,
                new ToolExecutionReceipt(
                    new ExecutionId("exec_11111111111111111111111111111111"),
                    new ToolCallId("call_11111111111111111111111111111111"),
                    ToolExecutionOutcome.Failed,
                    "workspace_path_outside_root",
                    null,
                    sideEffectsPossible: false,
                    providerCallId: "provider_call"),
                "{}"));
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep(path: "recovery.txt"));
        var gateway = new FakeToolGateway();
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, gateway, store, contextSource: source);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.Running, result.Run.State);
        Assert.Single(gateway.ExecuteAttempts);
    }

    [Fact]
    public async Task Denied_tool_is_returned_to_the_model_without_execution()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep());
        var gateway = new FakeToolGateway
        {
            DecisionFactory = _ => ToolProposalDecision.Deny(
                ToolAccessKind.ReadOnly,
                "policy_denied"),
        };
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, gateway, store);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Empty(gateway.ExecuteAttempts);
        var receipt = Assert.Single(result.Receipts);
        Assert.Equal(ToolExecutionOutcome.Failed, receipt.Outcome);
        Assert.Equal("policy_denied", receipt.Code);
        Assert.False(receipt.SideEffectsPossible);
        var commit = Assert.Single(
            store.Commits,
            item => item.Events.Any(@event =>
                @event.Kind == HarnessEventKind.ToolExecutionCompleted));
        var @event = Assert.Single(commit.Events);
        Assert.Equal(ToolExecutionOutcome.Failed, @event.ToolOutcome);
        Assert.False(@event.SideEffectsPossible);
        var payload = Assert.Single(commit.Payloads);
        Assert.Equal(HarnessPayloadKind.ToolOutput, payload.Kind);
        Assert.Equal(payload.PayloadId, receipt.ResultPayloadId);
        Assert.Contains("policy_denied", Encoding.UTF8.GetString(payload.Plaintext.Span));
    }

    [Fact]
    public async Task Denied_tool_carries_the_repair_hint_to_the_model()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep());
        var gateway = new FakeToolGateway
        {
            DecisionFactory = _ => ToolProposalDecision.Deny(
                ToolAccessKind.ReadOnly,
                "malformed_call",
                "read requires op: \"list\", \"read\", or \"help\"."),
        };
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, gateway, store);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Single(result.Receipts);
        var commit = Assert.Single(
            store.Commits,
            item => item.Events.Any(@event =>
                @event.Kind == HarnessEventKind.ToolExecutionCompleted));
        var payload = Assert.Single(commit.Payloads);
        var content = Encoding.UTF8.GetString(payload.Plaintext.Span);
        Assert.Contains("malformed_call", content);
        Assert.Contains("read requires op", content);
    }

    [Fact]
    public async Task Mixed_allowed_and_denied_results_remain_in_model_order()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep(callCount: 2));
        var gateway = new FakeToolGateway
        {
            DecisionFactory = proposal => proposal.ModelOrder == 2
                ? ToolProposalDecision.Deny(ToolAccessKind.ReadOnly, "policy_denied")
                : ToolProposalDecision.Allow(
                    ToolAccessKind.ReadOnly,
                    $"canonical-{proposal.ModelOrder}"),
        };
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, gateway, store);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        var completed = store.Commits
            .SelectMany(commit => commit.Events)
            .Where(@event => @event.Kind == HarnessEventKind.ToolExecutionCompleted)
            .ToArray();
        Assert.Equal(2, completed.Length);
        Assert.Equal(gateway.Proposals[0].ToolCallId, completed[0].ToolCallId);
        Assert.Equal(gateway.Proposals[1].ToolCallId, completed[1].ToolCallId);
        Assert.Equal(
            gateway.Proposals.Select(proposal => proposal.ToolCallId),
            result.Receipts.Select(receipt => receipt.ToolCallId));
        Assert.Single(gateway.ExecuteAttempts);
    }

    [Fact]
    public async Task Approval_wait_discards_prepared_calls_and_durably_records_prior_denials()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep(callCount: 3));
        var gateway = new FakeToolGateway
        {
            DecisionFactory = proposal => proposal.ModelOrder switch
            {
                1 => ToolProposalDecision.Allow(ToolAccessKind.ReadOnly, "canonical-1"),
                2 => ToolProposalDecision.Deny(ToolAccessKind.ReadOnly, "policy_denied"),
                _ => ToolProposalDecision.Ask(
                    ToolAccessKind.Mutation,
                    new ApprovalId("apr_" + new string('a', 32))),
            },
        };
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, gateway, store);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.WaitingApproval, result.Run.State);
        Assert.Single(gateway.CancelAttempts);
        Assert.Empty(gateway.ExecuteAttempts);
        Assert.Single(result.Receipts);
        Assert.Equal("policy_denied", result.Receipts[0].Code);
        var events = store.Commits.SelectMany(commit => commit.Events).ToArray();
        Assert.Contains(events, @event => @event.Kind == HarnessEventKind.ToolExecutionCompleted);
        Assert.Contains(events, @event => @event.Kind == HarnessEventKind.ApprovalRequested);
        var deniedRequest = Assert.Single(
            events,
            @event => @event.Kind == HarnessEventKind.ToolExecutionRequested);
        var deniedCompletion = Assert.Single(
            events,
            @event => @event.Kind == HarnessEventKind.ToolExecutionCompleted);
        Assert.Equal(deniedRequest.ExecutionId, deniedCompletion.ExecutionId);
        Assert.True(events.IndexOf(deniedRequest) < events.IndexOf(deniedCompletion));
    }

    [Fact]
    public async Task Approval_batch_persists_every_proposal_and_re_evaluates_after_resolution()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep(callCount: 3));
        var approval = new ApprovalId("apr_" + new string('a', 32));
        const long gatewayGeneration = 41;
        var hasAsked = false;
        var gateway = new FakeToolGateway
        {
            DecisionFactory = proposal =>
            {
                if (proposal.ModelOrder == 2 && !hasAsked)
                {
                    hasAsked = true;
                    return ToolProposalDecision.Ask(ToolAccessKind.Mutation, approval);
                }

                return ToolProposalDecision.Allow(
                    ToolAccessKind.ReadOnly,
                    $"canonical-{proposal.ModelOrder}",
                    gatewayGeneration);
            },
        };
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, gateway, store);

        var paused = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.WaitingApproval, paused.Run.State);
        Assert.Equal(3, gateway.Proposals.Count);
        Assert.Equal(
            new[] { "provider-call-1", "provider-call-2", "provider-call-3" },
            gateway.Proposals.Select(proposal => proposal.ProviderCallId));
        Assert.Empty(gateway.ExecuteAttempts);
        Assert.Equal(2, gateway.CancelAttempts.Count);
        var proposed = store.Commits
            .SelectMany(commit => commit.Events)
            .Where(item => item.Kind == HarnessEventKind.ToolProposed)
            .ToArray();
        Assert.Equal(3, proposed.Length);
        Assert.Equal(
            new[] { 1, 2, 3 },
            proposed.Select(item => item.SafeCount!.Value).ToArray());
        Assert.Equal(
            gateway.Proposals.Select(proposal => proposal.ToolCallId.Value).ToArray(),
            proposed.Select(item => item.ToolCallId!.Value.Value).ToArray());
        var approvalEvent = Assert.Single(
            store.Commits.SelectMany(commit => commit.Events),
            item => item.Kind == HarnessEventKind.ApprovalRequested);
        Assert.Equal("provider-call-2", approvalEvent.ProviderCallId);
        Assert.Equal(gateway.Proposals[1].ToolCallId, approvalEvent.ToolCallId);
        Assert.Single(model.Requests);

        await coordinator.ResumeAfterApprovalAsync(RunId(), CancellationToken.None);
        var resumed = await coordinator.TryResumeRetainedApprovalAsync(
            RunId(),
            CancellationToken.None);

        Assert.NotNull(resumed);
        Assert.Null(resumed.ErrorCode);
        Assert.Equal(6, gateway.Proposals.Count);
        Assert.Equal(3, gateway.ExecuteAttempts.Count);
        Assert.Equal(3, resumed.Receipts.Count);
        Assert.Equal(5, gateway.ApprovedExecutions.Count);
        Assert.All(
            gateway.ApprovedExecutions,
            approved => Assert.Equal(gatewayGeneration, approved.GatewayGeneration));
        Assert.Equal(5, gateway.PreparedExecutions.Count);
        Assert.All(
            gateway.PreparedExecutions,
            prepared => Assert.Equal(gatewayGeneration, prepared.GatewayGeneration));
        Assert.Single(model.Requests);
        Assert.Equal(
            3,
            store.Commits
                .SelectMany(commit => commit.Events)
                .Count(item => item.Kind == HarnessEventKind.ToolProposed));
    }

    [Fact]
    public async Task Approval_batch_retains_only_non_denied_proposals_around_an_ask()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep(callCount: 3));
        var approval = new ApprovalId("apr_" + new string('b', 32));
        var hasAsked = false;
        var gateway = new FakeToolGateway
        {
            DecisionFactory = proposal =>
            {
                if (proposal.ModelOrder == 2 && !hasAsked)
                {
                    hasAsked = true;
                    return ToolProposalDecision.Ask(ToolAccessKind.Mutation, approval);
                }

                return proposal.ModelOrder is 1 or 3
                    ? ToolProposalDecision.Deny(ToolAccessKind.ReadOnly, "policy_denied")
                    : ToolProposalDecision.Allow(ToolAccessKind.ReadOnly, "canonical-2");
            },
        };
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var stash = new InMemoryRetainedApprovalStash(new IncrementingTimeProvider(Epoch));
        var coordinator = Coordinator(model, gateway, store, retainedStash: stash);

        var paused = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.WaitingApproval, paused.Run.State);
        Assert.Empty(gateway.ExecuteAttempts);
        Assert.Equal(3, gateway.Proposals.Count);
        Assert.Equal(2, paused.Receipts.Count);
        Assert.Equal(
            new[] { "policy_denied", "policy_denied" },
            paused.Receipts.Select(receipt => receipt.Code));
        var events = store.Commits.SelectMany(commit => commit.Events).ToArray();
        Assert.Equal(3, events.Count(item => item.Kind == HarnessEventKind.ToolProposed));
        Assert.Equal(2, events.Count(item => item.Kind == HarnessEventKind.ToolExecutionCompleted));
        Assert.Single(events, item => item.Kind == HarnessEventKind.ApprovalRequested);

        await coordinator.ResumeAfterApprovalAsync(RunId(), CancellationToken.None);
        var resumed = await coordinator.TryResumeRetainedApprovalAsync(
            RunId(),
            CancellationToken.None);

        Assert.Equal(4, gateway.Proposals.Count);
        Assert.NotNull(resumed);
        Assert.Equal(new[] { 2 }, gateway.Proposals.Skip(3).Select(proposal => proposal.ModelOrder));
        Assert.Single(gateway.ExecuteAttempts);
        Assert.Single(resumed.Receipts);
        Assert.Equal(
            3,
            store.Commits
                .SelectMany(commit => commit.Events)
                .Count(item => item.Kind == HarnessEventKind.ToolProposed));
    }

    [Fact]
    public async Task Resume_revalidation_failure_fails_closed_without_a_new_model_step()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep());
        var approval = new ApprovalId("apr_" + new string('c', 32));
        var firstEvaluation = true;
        var gateway = new FakeToolGateway
        {
            DecisionFactory = _ =>
            {
                if (firstEvaluation)
                {
                    firstEvaluation = false;
                    return ToolProposalDecision.Ask(ToolAccessKind.Mutation, approval);
                }

                throw new InvalidOperationException("intent_changed");
            },
        };
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, gateway, store);

        await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);
        await coordinator.ResumeAfterApprovalAsync(RunId(), CancellationToken.None);
        var result = await coordinator.TryResumeRetainedApprovalAsync(
            RunId(),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(HarnessRunState.Failed, result!.Run.State);
        Assert.Equal("retained_approval_resume_failed", result.ErrorCode);
        Assert.Empty(gateway.ExecuteAttempts);
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task Resume_revalidation_ask_creates_a_fresh_gate_without_execution()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep());
        var firstApproval = new ApprovalId("apr_" + new string('d', 32));
        var secondApproval = new ApprovalId("apr_" + new string('e', 32));
        var firstEvaluation = true;
        var gateway = new FakeToolGateway
        {
            DecisionFactory = _ =>
            {
                if (firstEvaluation)
                {
                    firstEvaluation = false;
                    return ToolProposalDecision.Ask(ToolAccessKind.Mutation, firstApproval);
                }

                return ToolProposalDecision.Ask(ToolAccessKind.Mutation, secondApproval);
            },
        };
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, gateway, store);

        await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);
        await coordinator.ResumeAfterApprovalAsync(RunId(), CancellationToken.None);
        var result = await coordinator.TryResumeRetainedApprovalAsync(
            RunId(),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("approval_required", result!.ErrorCode);
        Assert.Equal(HarnessRunState.WaitingApproval, result.Run.State);
        Assert.Empty(gateway.ExecuteAttempts);
        Assert.Single(model.Requests);
        Assert.Equal(
            2,
            store.Commits
                .SelectMany(commit => commit.Events)
                .Count(item => item.Kind == HarnessEventKind.ApprovalRequested));
    }

    [Fact]
    public async Task Approval_resolution_race_returns_null_after_run_becomes_running()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep());
        var approval = new ApprovalId("apr_" + new string('5', 32));
        var firstGateway = new FakeToolGateway
        {
            DecisionFactory = _ => ToolProposalDecision.Ask(ToolAccessKind.Mutation, approval),
        };
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var stash = new InMemoryRetainedApprovalStash(new IncrementingTimeProvider(Epoch));
        var first = Coordinator(model, firstGateway, store, retainedStash: stash);
        var paused = await first.RunOneStepAsync(RunId(), CancellationToken.None);
        Assert.Equal(HarnessRunState.WaitingApproval, paused.Run.State);
        var second = Coordinator(
            new FakeModelStepRunner(),
            new FakeToolGateway(),
            store,
            retainedStash: stash,
            approvalResolutionRaceHook: (_, _) => first.ResumeAfterApprovalAsync(RunId(), CancellationToken.None));

        var result = await second.TryRefreshRetainedApprovalAsync(
            RunId(),
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(
            HarnessRunState.Running,
            (await store.GetRunAsync(RunId(), CancellationToken.None))!.State);
        var resumed = await second.TryResumeRetainedApprovalAsync(
            RunId(),
            CancellationToken.None);
        Assert.NotNull(resumed);
        Assert.Null(resumed!.ErrorCode);
        Assert.Single(resumed.Receipts);
    }

    [Fact]
    public async Task Waiting_approval_recovery_re_evaluates_without_a_new_model_step()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep());
        var firstApproval = new ApprovalId("apr_" + new string('1', 32));
        var secondApproval = new ApprovalId("apr_" + new string('2', 32));
        var firstEvaluation = true;
        var gateway = new FakeToolGateway
        {
            DecisionFactory = _ => firstEvaluation
                ? FirstAsk(firstApproval, () => firstEvaluation = false)
                : ToolProposalDecision.Ask(ToolAccessKind.Mutation, secondApproval),
        };
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, gateway, store);

        var paused = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);
        var refreshed = await coordinator.TryRefreshRetainedApprovalAsync(
            RunId(),
            CancellationToken.None);

        Assert.Equal(HarnessRunState.WaitingApproval, paused.Run.State);
        Assert.NotNull(refreshed);
        Assert.Equal("approval_required", refreshed!.ErrorCode);
        Assert.Equal(HarnessRunState.WaitingApproval, refreshed.Run.State);
        Assert.Equal(2, gateway.Proposals.Count);
        Assert.Empty(gateway.ExecuteAttempts);
        Assert.Single(model.Requests);
        Assert.Equal(
            2,
            store.Commits.SelectMany(commit => commit.Events)
                .Count(item => item.Kind == HarnessEventKind.ApprovalRequested));

        static ToolProposalDecision FirstAsk(
            ApprovalId approvalId,
            Action markEvaluated)
        {
            markEvaluated();
            return ToolProposalDecision.Ask(ToolAccessKind.Mutation, approvalId);
        }
    }

    [Fact]
    public async Task Multiple_asks_revoke_extra_approval_before_persisting_one_gate()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep(callCount: 2));
        var firstApproval = new ApprovalId("apr_" + new string('3', 32));
        var secondApproval = new ApprovalId("apr_" + new string('4', 32));
        var gateway = new FakeToolGateway
        {
            DecisionFactory = proposal => ToolProposalDecision.Ask(
                ToolAccessKind.Mutation,
                proposal.ModelOrder == 1 ? firstApproval : secondApproval),
        };
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, gateway, store);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.WaitingApproval, result.Run.State);
        Assert.Equal(new[] { secondApproval }, gateway.RevokedApprovals);
        Assert.Single(
            store.Commits.SelectMany(commit => commit.Events),
            item => item.Kind == HarnessEventKind.ApprovalRequested);
        Assert.Empty(gateway.ExecuteAttempts);
    }

    [Fact]
    public async Task Read_only_transport_retry_uses_same_execution_id_and_at_most_two_attempts()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep());
        var gateway = new FakeToolGateway(
            ToolAccessKind.ReadOnly,
            transportFailures: 1);
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, gateway, store);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.Running, result.Run.State);
        Assert.Equal(2, gateway.ExecuteAttempts.Count);
        Assert.Single(gateway.ExecuteAttempts.Distinct());
        Assert.Equal(ToolExecutionOutcome.Succeeded, Assert.Single(result.Receipts).Outcome);
    }

    [Fact]
    public async Task Repeated_read_only_transport_failure_stops_after_second_attempt()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep());
        var gateway = new FakeToolGateway(
            ToolAccessKind.ReadOnly,
            transportFailures: 3);
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, gateway, store);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(2, gateway.ExecuteAttempts.Count);
        Assert.Single(gateway.ExecuteAttempts.Distinct());
        Assert.Equal(ToolExecutionOutcome.Failed, Assert.Single(result.Receipts).Outcome);
        Assert.Equal(HarnessRunState.Running, result.Run.State);
    }

    [Fact]
    public async Task Mutation_transport_failure_retries_once_then_reconciles_when_retry_fails()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep());
        var gateway = new FakeToolGateway(
            ToolAccessKind.Mutation,
            transportFailures: 2);
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, gateway, store);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(2, gateway.ExecuteAttempts.Count);
        var receipt = Assert.Single(result.Receipts);
        Assert.Equal(ToolExecutionOutcome.OutcomeUnknown, receipt.Outcome);
        Assert.True(receipt.SideEffectsPossible);
        Assert.Equal(HarnessRunState.ReconciliationRequired, result.Run.State);
        Assert.Contains(store.Commits.SelectMany(commit => commit.Events), item =>
            item.Kind == HarnessEventKind.ReconciliationRequired);

        var blocked = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);
        Assert.Equal("run_not_runnable", blocked.ErrorCode);
        Assert.Equal(2, gateway.ExecuteAttempts.Count);
    }

    [Fact]
    public async Task Unknown_mutation_outcome_records_later_prepared_calls_as_not_executed()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep(callCount: 2));
        var gateway = new FakeToolGateway(
            ToolAccessKind.Mutation,
            transportFailures: 2);
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, gateway, store);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.ReconciliationRequired, result.Run.State);
        Assert.Equal(2, gateway.ExecuteAttempts.Count);
        Assert.Single(gateway.CancelAttempts);
        Assert.Equal(2, result.Receipts.Count);
        Assert.Equal(ToolExecutionOutcome.OutcomeUnknown, result.Receipts[0].Outcome);
        Assert.Equal("not_executed_after_unknown", result.Receipts[1].Code);
        Assert.Equal(2, store.Commits.SelectMany(commit => commit.Events)
            .Count(@event => @event.Kind == HarnessEventKind.ToolExecutionCompleted));
    }

    [Fact]
    public async Task Full_transient_buffer_drops_frames_without_blocking_durable_final_commit()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("done"), "a", "b", "c");
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var frames = new TransientFrameBuffer(1);
        var coordinator = Coordinator(model, new FakeToolGateway(), store, frames);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(HarnessRunState.WaitingHistory, result.Run.State);
        Assert.Equal(2, frames.DroppedCount);
        var dropped = Assert.Single(
            store.Commits.SelectMany(commit => commit.Events),
            item => item.Kind == HarnessEventKind.TransientFramesDropped);
        Assert.Equal(2, dropped.SafeCount);
        var persisted = new List<HarnessEventEnvelope>();
        await foreach (var envelope in store.ReadEventsAsync(
                           RunId(),
                           0,
                           CancellationToken.None))
        {
            persisted.Add(envelope);
        }

        var persistedDropped = Assert.Single(
            persisted,
            envelope => envelope.Event.Kind == "transient.frames_dropped");
        Assert.Equal(2, persistedDropped.Event.SafeCount);
    }

    [Fact]
    public async Task Assistant_stream_redacts_a_known_secret_split_across_deltas()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("done"), "before split-", "secret after");
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var frames = new TransientFrameBuffer(4);
        var coordinator = Coordinator(
            model,
            new FakeToolGateway(),
            store,
            frames,
            new BufferedSecretRedactor("split-secret"));

        await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);
        var frame = await ReadOneFrameAsync(frames);

        Assert.Equal("before [REDACTED] after", frame.Content);
        Assert.DoesNotContain("split-secret", frame.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_stream_redacts_a_known_secret_split_across_chunks()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep());
        var gateway = new FakeToolGateway
        {
            OutputChunks = ["before split-", "secret after"],
        };
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var frames = new TransientFrameBuffer(4);
        var coordinator = Coordinator(
            model,
            gateway,
            store,
            frames,
            new BufferedSecretRedactor("split-secret"));

        await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);
        var frame = await ReadOneFrameAsync(frames);

        Assert.Equal("before [REDACTED] after", frame.Content);
        Assert.DoesNotContain("split-secret", frame.Content, StringComparison.Ordinal);
    }

    private static AgentRunCoordinator Coordinator(
        IModelStepRunner model,
        FakeToolGateway gateway,
        InMemoryHarnessEventStore store,
        TransientFrameBuffer? frames = null,
        ITransientContentRedactor? redactor = null,
        IAgentReplyPolicy? replyPolicy = null,
        IAgentReplyEffectSink? replyEffects = null,
        IModelContextSource? contextSource = null,
        IRetainedApprovalStash? retainedStash = null,
        Func<RunId, CancellationToken, Task>? approvalResolutionRaceHook = null,
        IAgentActivitySink? activitySink = null) =>
        new(
            model,
            gateway,
            store,
            new AgentContextAssembler(contextSource ?? new EmptyContextSource()),
            new SequenceIdGenerator(),
            new IncrementingTimeProvider(Epoch),
            new ToolCallScheduler(),
            frames ?? new TransientFrameBuffer(32),
            redactor,
            replyPolicy: replyPolicy,
            replyEffects: replyEffects,
            promptContextProvider: new FakeSystemPromptContextProvider(),
            retainedStash: retainedStash,
            approvalResolutionRaceHook: approvalResolutionRaceHook,
            activitySink: activitySink);

    private static async Task<HarnessTransientFrame> ReadOneFrameAsync(
        TransientFrameBuffer frames)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var enumerator = frames.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        return enumerator.Current;
    }

    private static AssistantStep FinalStep(string text, ModelUsage? usage = null) =>
        new(text, Array.Empty<ModelToolCall>(), AssistantFinishReason.Stop, 16, usage);

    private static AssistantStep ToolStep(int callCount = 1, string? path = null) =>
        new(
            "checking",
            Enumerable.Range(1, callCount)
                .Select(index =>
                new ModelToolCall(
                    callCount == 1 ? "provider-call" : $"provider-call-{index}",
                    "files.read",
                    "1",
                    JsonDocument.Parse(
                        callCount == 1
                            ? JsonSerializer.Serialize(new { path = path ?? "README.md" })
                            : $"{{\"path\":\"README-{index}.md\"}}")
                        .RootElement.Clone()))
                .ToArray(),
            AssistantFinishReason.ToolCalls,
            16);

    private static AgentRun Run(HarnessRunStatus status) =>
        new(
            RunId(),
            new SessionId("11111111111111111111111111111111"),
            new WorkspaceId("ws_11111111111111111111111111111111"),
            status,
            0,
            0,
            1,
            0,
            0,
            AgentPermissionPreset.Standard,
            1,
            new string('a', 64),
            Epoch,
            Epoch);

    private static RunId RunId() =>
        new("run_11111111111111111111111111111111");

    private sealed class NullProviderCallRunner : IModelStepRunner
    {
        public Task<ModelStepResult> RunStepAsync(
            ModelStepRequest request,
            Func<AssistantDelta, ValueTask> publishDelta,
            CancellationToken cancellationToken)
        {
            var call = new ModelToolCall(
                null!,
                "read",
                "1",
                JsonDocument.Parse("{}").RootElement.Clone());
            var step = new AssistantStep(
                null,
                [call],
                AssistantFinishReason.ToolCalls,
                estimatedInputTokens: 1);
            return Task.FromResult(new ModelStepResult(
                step,
                SystemPromptComposer.Compose(request.PromptContext).Receipt));
        }
    }

    private sealed class ThrowingModelStepRunner : IModelStepRunner
    {
        public Task<ModelStepResult> RunStepAsync(
            ModelStepRequest request,
            Func<AssistantDelta, ValueTask> publishDelta,
            CancellationToken cancellationToken) =>
            throw new TimeoutException("model timed out");
    }

    private sealed class RecordingActivitySink : IAgentActivitySink
    {
        public List<AgentActivity> Activities { get; } = new();

        public void Publish(RunId runId, AgentActivity activity) => Activities.Add(activity);
    }

    private sealed class EmptyContextSource : IModelContextSource
    {
        public Task<ModelContextSourceSnapshot> LoadAsync(
            RunId runId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ModelContextSourceSnapshot(
                Array.Empty<ModelContextEntry>(),
                Array.Empty<ModelContextEntry>()));
    }

    private sealed class FixedContextSource(params ModelContextEntry[] entries) : IModelContextSource
    {
        public Task<ModelContextSourceSnapshot> LoadAsync(
            RunId runId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ModelContextSourceSnapshot(
                Array.Empty<ModelContextEntry>(),
                entries));
    }

    private sealed class IncrementingTimeProvider : ITimeProvider
    {
        private DateTimeOffset _current;

        public IncrementingTimeProvider(DateTimeOffset current) => _current = current;

        public DateTimeOffset UtcNow => _current = _current.AddSeconds(1);
    }

    private sealed class BufferedSecretRedactor(string secret) : ITransientContentRedactor
    {
        public ITransientContentRedactionSession CreateSession() => new Session(secret);

        private sealed class Session(string secret) : ITransientContentRedactionSession
        {
            private readonly StringBuilder _content = new();

            public string Append(string content)
            {
                _content.Append(content);
                return string.Empty;
            }

            public string Complete() => _content.ToString().Replace(
                secret,
                "[REDACTED]",
                StringComparison.Ordinal);
        }
    }

    private sealed class RecordingReplyPolicy(AgentReplyPresentation reply) : IAgentReplyPolicy
    {
        public ValueTask<AgentReplyPresentation> PrepareAsync(
            AgentRun run,
            string rawText,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(reply);
    }

    private sealed class ThrowingReplyPolicy : IAgentReplyPolicy
    {
        public ValueTask<AgentReplyPresentation> PrepareAsync(
            AgentRun run,
            string rawText,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("reply_policy_failed");
    }

    private sealed class RecordingReplyEffects : IAgentReplyEffectSink
    {
        public AgentReplyPresentation? Reply { get; private set; }
        public long RunVersionAtCall { get; private set; }

        public ValueTask ApplyAsync(
            AgentRun run,
            AgentReplyPresentation reply,
            ModelStepId modelStepId,
            CancellationToken cancellationToken)
        {
            Reply = reply;
            RunVersionAtCall = run.Version;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingReplyEffects : IAgentReplyEffectSink
    {
        public ValueTask ApplyAsync(
            AgentRun run,
            AgentReplyPresentation reply,
            ModelStepId modelStepId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("reply_effect_failed");
    }

    private sealed class SequenceIdGenerator : IIdGenerator
    {
        private int _next;

        public RunId NewRunId() => new(Value("run_"));
        public ModelStepId NewModelStepId() => new(Value("step_"));
        public ToolCallId NewToolCallId() => new(Value("call_"));
        public ExecutionId NewExecutionId() => new(Value("exec_"));
        public ApprovalId NewApprovalId() => new(Value("apr_"));
        public TrustRequestId NewTrustRequestId() => new(Value("trust_"));
        public PolicyRuleId NewPolicyRuleId() => new(Value("rule_"));
        public EventId NewEventId() => new(Value("evt_"));
        public PayloadId NewPayloadId() => new(Value("payload_"));

        private string Value(string prefix) =>
            prefix + Interlocked.Increment(ref _next).ToString("x32");
    }
}
