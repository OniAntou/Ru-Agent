using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Persistence.Context;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

// ADR 0039: verification projection reads only the run's own receipts. Journal changes, compaction and
// the supersedes argument never change obligations, and warm and cold projections agree.
public sealed partial class ModelContextSourceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Passing_check_then_source_edit_and_a_later_check_project_identically_warm_and_cold(bool incremental)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var workspace = await fixture.RegisterWorkspaceAsync(92);
        await new WorkspaceCoordinatorStore(fixture.Options).GetOrCreateAsync(workspace.WorkspaceId, default);
        var store = fixture.CreateEventStore();
        var run = await CreateVerificationRunAsync(fixture, store, 92, 92, 92);
        var active = new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore));
        if (incremental) await active.AssembleAsync(run.RunId, default);
        Task<ModelContextSnapshot> WarmOrFresh() => (incremental ? active
            : new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore)))
            .AssembleAsync(run.RunId, default);
        Task<ModelContextSnapshot> Cold() => new AgentContextAssembler(new SqliteModelContextSource(
            fixture.Options, fixture.PayloadStore, enableEntryCache: false)).AssembleAsync(run.RunId, default);

        // A journal change after the passing check is not an obligation: the projection never reads it.
        await AppendWorkspaceChangeAsync(fixture, workspace.WorkspaceId, runId: null, "chg_92_after",
            DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Null((await WarmOrFresh()).VerificationEvidence.BlockingCode);

        var edited = await CommitToolExchangeAsync(store, run, 93, "write_file",
            "{\"path\":\"src/Widget.cs\",\"content\":\"class Widget {}\"}", ToolExecutionOutcome.Succeeded, "ok", "{}");
        var warmEdit = await WarmOrFresh();
        var coldEdit = await Cold();
        Assert.Null(warmEdit.VerificationEvidence.BlockingCode);
        Assert.True(warmEdit.VerificationEvidence.HasSourceEdits);
        Assert.True(warmEdit.VerificationEvidence.SourceEditAwaitingVerification);
        Assert.Equal(coldEdit.VerificationEvidence.Checks, warmEdit.VerificationEvidence.Checks);
        Assert.Equal(coldEdit.VerificationEvidence.HasSourceEdits, warmEdit.VerificationEvidence.HasSourceEdits);
        Assert.Equal(coldEdit.VerificationEvidence.SourceEditAwaitingVerification,
            warmEdit.VerificationEvidence.SourceEditAwaitingVerification);

        // A later declared check passes. Its supersedes argument is accepted and ignored.
        await CommitToolExchangeAsync(store, edited, 94, "run_process",
            "{\"executable\":\"python\",\"arguments\":[\"smoke.py\"],\"purpose\":\"verification\",\"supersedes\":[\"verify-freshness\"]}",
            ToolExecutionOutcome.Succeeded, "ok",
            "{\"exit_code\":0,\"verification_identity\":\"" + new string('B', 64) + "\"}");
        var warmPass = await WarmOrFresh();
        var coldPass = await Cold();
        Assert.Null(warmPass.VerificationEvidence.BlockingCode);
        Assert.False(warmPass.VerificationEvidence.SourceEditAwaitingVerification);
        Assert.Equal(coldPass.VerificationEvidence.Checks, warmPass.VerificationEvidence.Checks);
        Assert.Equal(coldPass.VerificationEvidence.SourceEditAwaitingVerification,
            warmPass.VerificationEvidence.SourceEditAwaitingVerification);
        Assert.Equal(coldPass.VerificationEvidence.HasSourceEdits, warmPass.VerificationEvidence.HasSourceEdits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Workspace_change_after_a_passing_check_is_not_an_obligation(bool warmCache)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var workspace = await fixture.RegisterWorkspaceAsync(70);
        var eventStore = fixture.CreateEventStore();
        var coordinator = new WorkspaceCoordinatorStore(fixture.Options);
        await coordinator.GetOrCreateAsync(workspace.WorkspaceId, default);
        var request = await CreateVerificationRunAsync(fixture, eventStore, 70, 70, 70);
        var context = new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore));
        var initial = await context.AssembleAsync(request.RunId, default);
        Assert.Equal(VerificationCheckStatus.Passed, Assert.Single(initial.VerificationEvidence.Checks).Status);

        await AppendWorkspaceChangeAsync(fixture, workspace.WorkspaceId, runId: null, "chg_70_after",
            DateTimeOffset.UtcNow.AddSeconds(1));
        var refreshedContext = warmCache ? context
            : new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore,
                enableEntryCache: false));
        var refreshed = await refreshedContext.AssembleAsync(request.RunId, default);

        Assert.Equal(VerificationCheckStatus.Passed, Assert.Single(refreshed.VerificationEvidence.Checks).Status);
        Assert.Null(refreshed.VerificationEvidence.BlockingCode);
    }

    [Fact]
    public async Task Passed_check_survives_compaction_and_restart_without_journal_input()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var workspace = await fixture.RegisterWorkspaceAsync(73);
        var eventStore = fixture.CreateEventStore();
        var coordinator = new WorkspaceCoordinatorStore(fixture.Options);
        await coordinator.GetOrCreateAsync(workspace.WorkspaceId, default);
        var request = await CreateVerificationRunAsync(fixture, eventStore, 73, 73, 73);
        await AppendWorkspaceChangeAsync(fixture, workspace.WorkspaceId, runId: null, "chg_73_later",
            DateTimeOffset.UtcNow.AddSeconds(1));

        var compacted = NextRun(request, 4, 5);
        await CommitCompactionAsync(eventStore, request.RunId, compacted, 3, 76, "compact-verification-73",
            "The previous check passed", $"event:{HarnessStoreTestFixture.Event(733).Value}", compacted.UpdatedAt, 4);

        var restarted = await new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore,
            enableEntryCache: false)).AssembleAsync(request.RunId, default);
        Assert.Null(restarted.VerificationEvidence.BlockingCode);
        Assert.Equal(VerificationCheckStatus.Passed, Assert.Single(restarted.VerificationEvidence.Checks).Status);
    }

    private static async Task<AgentRun> CommitToolExchangeAsync(
        SqliteHarnessEventStore store,
        AgentRun current,
        int number,
        string toolId,
        string argumentsJson,
        ToolExecutionOutcome outcome,
        string safeCode,
        string output)
    {
        using var arguments = JsonDocument.Parse(argumentsJson);
        var providerCallId = "provider-" + number;
        var modelStepId = new ModelStepId("step_" + number.ToString("x32"));
        var callId = new ToolCallId("call_" + number.ToString("x32"));
        var executionId = new ExecutionId("exec_" + number.ToString("x32"));
        var proposal = new HarnessPayloadIntent(HarnessStoreTestFixture.Payload(number * 10 + 1),
            HarnessPayloadKind.AssistantStep,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Json, 4096, AssistantStepJsonCodec.Serialize(new AssistantStep(null,
                [new ModelToolCall(providerCallId, toolId, "1.0.0", arguments.RootElement)],
                AssistantFinishReason.ToolCalls, 1)));
        var requested = NextRun(current, current.Version + 1, current.LastSequence + 2);
        var requestCommit = await store.CommitAsync(new HarnessCommit(current.RunId, current.Version, requested,
            [new HarnessEventIntent(HarnessStoreTestFixture.Event(number * 10 + 1), HarnessEventKind.ModelStepCompleted,
                modelStepId: modelStepId, payloadId: proposal.PayloadId),
             new HarnessEventIntent(HarnessStoreTestFixture.Event(number * 10 + 2), HarnessEventKind.ToolExecutionRequested,
                modelStepId: modelStepId, toolCallId: callId, executionId: executionId, providerCallId: providerCallId)],
            [proposal], []), default);
        Assert.Equal(HarnessCommitOutcome.Committed, requestCommit.Outcome);

        var result = TextPayload(number * 10 + 3, HarnessPayloadKind.ToolOutput, output);
        var completed = NextRun(requested, requested.Version + 1, requested.LastSequence + 1);
        var completeCommit = await store.CommitAsync(new HarnessCommit(current.RunId, requested.Version, completed,
            [new HarnessEventIntent(HarnessStoreTestFixture.Event(number * 10 + 3), HarnessEventKind.ToolExecutionCompleted,
                toolCallId: callId, providerCallId: providerCallId, executionId: executionId, payloadId: result.PayloadId,
                safeCode: safeCode, toolOutcome: outcome, sideEffectsPossible: false)],
            [result], []), default);
        Assert.Equal(HarnessCommitOutcome.Committed, completeCommit.Outcome);
        return completed;
    }

    private static async Task<AgentRun> CreateVerificationRunAsync(
        HarnessStoreTestFixture fixture,
        SqliteHarnessEventStore store,
        int runNumber,
        int sessionNumber,
        int workspaceNumber)
    {
        var request = fixture.NewRun(runNumber, sessionNumber, workspaceNumber);
        Assert.Equal(CreateRunOutcome.Created, (await store.TryCreateRunAsync(request, default)).Outcome);
        await fixture.PayloadStore.CaptureBaseContextAsync(request.Run.RunId, [], HarnessStoreTestFixture.Timestamp, default);

        const string providerCallId = "verify-freshness";
        var callId = new ToolCallId("call_" + runNumber.ToString("x32"));
        var executionId = new ExecutionId("exec_" + runNumber.ToString("x32"));
        var modelStepId = new ModelStepId("step_" + (runNumber * 10 + 1).ToString("x32"));
        using var arguments = JsonDocument.Parse("""{"mode":"shell","script":"dotnet test","purpose":"verification"}""");
        var step = new AssistantStep(null,
            [new ModelToolCall(providerCallId, "bash", "1.0.0", arguments.RootElement)], AssistantFinishReason.ToolCalls, 1);
        var proposal = new HarnessPayloadIntent(HarnessStoreTestFixture.Payload(runNumber * 10 + 1),
            HarnessPayloadKind.AssistantStep,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Json, 4096, AssistantStepJsonCodec.Serialize(step));
        var proposed = NextRun(request.Run, 1, 2);
        Assert.Equal(HarnessCommitOutcome.Committed, (await store.CommitAsync(new HarnessCommit(request.Run.RunId, 0,
            proposed, [new HarnessEventIntent(HarnessStoreTestFixture.Event(runNumber * 10 + 1),
                HarnessEventKind.ModelStepCompleted,
                modelStepId: modelStepId, payloadId: proposal.PayloadId)],
            [proposal], []), default)).Outcome);

        var requested = NextRun(proposed, 2, 3);
        Assert.Equal(HarnessCommitOutcome.Committed, (await store.CommitAsync(new HarnessCommit(request.Run.RunId, 1,
            requested, [new HarnessEventIntent(HarnessStoreTestFixture.Event(runNumber * 10 + 2),
                HarnessEventKind.ToolExecutionRequested, modelStepId: modelStepId, toolCallId: callId,
                executionId: executionId, providerCallId: providerCallId)], [], []), default)).Outcome);

        var result = new HarnessPayloadIntent(HarnessStoreTestFixture.Payload(runNumber * 10 + 3),
            HarnessPayloadKind.ToolOutput,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text, 4096,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            { exit_code = 0, verification_identity = new string('A', 64) })));
        var completed = NextRun(requested, 3, 4);
        Assert.Equal(HarnessCommitOutcome.Committed, (await store.CommitAsync(new HarnessCommit(request.Run.RunId, 2,
            completed, [new HarnessEventIntent(HarnessStoreTestFixture.Event(runNumber * 10 + 3),
                HarnessEventKind.ToolExecutionCompleted, toolCallId: callId, providerCallId: providerCallId,
                executionId: executionId, payloadId: result.PayloadId, safeCode: "ok",
                toolOutcome: ToolExecutionOutcome.Succeeded, sideEffectsPossible: false)], [result], []), default)).Outcome);
        return completed;
    }

    private static async Task AppendWorkspaceChangeAsync(
        HarnessStoreTestFixture fixture,
        WorkspaceId workspaceId,
        RunId? runId,
        string changeId,
        DateTimeOffset createdAtUtc)
    {
        var coordinator = new WorkspaceCoordinatorStore(fixture.Options);
        var protector = new WorkspaceJournalProtector(new FakeHarnessKeyStore());
        var encrypted = await protector.EncryptAsync(workspaceId, changeId, "src/changed.cs",
            Encoding.UTF8.GetBytes("workspace change"), default);
        await coordinator.AppendChangeAsync(workspaceId, runId, changeId, "src/changed.cs", null,
            new string('b', 64), encrypted, "committed", createdAtUtc, default);
    }
}
