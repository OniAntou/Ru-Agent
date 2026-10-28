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

public sealed partial class ModelContextSourceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Workspace_change_after_verification_launch_stales_pass_even_with_warm_cache(bool warmCache)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var workspace = await fixture.RegisterWorkspaceAsync(70);
        var eventStore = fixture.CreateEventStore();
        var coordinator = new WorkspaceCoordinatorStore(fixture.Options);
        await coordinator.GetOrCreateAsync(workspace.WorkspaceId, default);
        var request = await CreateVerificationRunAsync(fixture, eventStore, 70, 70, 70, includeRequest: true);
        var context = new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore));
        var initial = await context.AssembleAsync(request.RunId, default);
        Assert.Equal(VerificationCheckStatus.Passed, Assert.Single(initial.VerificationEvidence.Checks).Status);
        var startedAt = Assert.Single(initial.VerificationEvidence.Checks).ExecutionStartedAtUtc!.Value;

        await AppendWorkspaceChangeAsync(fixture, workspace.WorkspaceId, runId: null, "chg_70_after",
            startedAt.AddSeconds(1));
        var refreshedContext = warmCache ? context
            : new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore,
                enableEntryCache: false));
        var refreshed = await refreshedContext.AssembleAsync(request.RunId, default);

        var check = Assert.Single(refreshed.VerificationEvidence.Checks);
        Assert.Equal(VerificationCheckStatus.Stale, check.Status);
        Assert.Equal("verification_stale", check.Code);
        Assert.Equal("verification_stale", refreshed.VerificationEvidence.BlockingCode);
        Assert.Equal(startedAt, check.ExecutionStartedAtUtc);
    }

    [Fact]
    public async Task Workspace_change_before_check_start_does_not_stale_the_check()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var workspace = await fixture.RegisterWorkspaceAsync(71);
        var eventStore = fixture.CreateEventStore();
        var coordinator = new WorkspaceCoordinatorStore(fixture.Options);
        await coordinator.GetOrCreateAsync(workspace.WorkspaceId, default);
        var runB = fixture.NewRun(72, 72, 71);
        await eventStore.TryCreateRunAsync(runB, default);
        await AppendWorkspaceChangeAsync(fixture, workspace.WorkspaceId, runB.Run.RunId, "chg_71_before",
            DateTimeOffset.UtcNow.AddMinutes(-2));

        var runA = await CreateVerificationRunAsync(fixture, eventStore, 71, 71, 71, includeRequest: true);
        var context = await new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore))
            .AssembleAsync(runA.RunId, default);

        Assert.Equal(VerificationCheckStatus.Passed, Assert.Single(context.VerificationEvidence.Checks).Status);
        Assert.Null(context.VerificationEvidence.BlockingCode);
    }

    [Fact]
    public async Task Policy_update_and_membership_heartbeat_without_journal_change_do_not_stale_check()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var workspace = await fixture.RegisterWorkspaceAsync(82);
        var eventStore = fixture.CreateEventStore();
        var coordinator = new WorkspaceCoordinatorStore(fixture.Options);
        await coordinator.GetOrCreateAsync(workspace.WorkspaceId, default);
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        await AppendWorkspaceChangeAsync(fixture, workspace.WorkspaceId, runId: null, "chg_82_before",
            startedAt.AddMinutes(-1));
        var request = await CreateVerificationRunAsync(fixture, eventStore, 82, 82, 82, includeRequest: true,
            startedAtUtc: startedAt);

        var currentCoordinator = await coordinator.GetOrCreateAsync(workspace.WorkspaceId, default);
        await coordinator.UpdatePolicyAsync(workspace.WorkspaceId, currentCoordinator.Version, "shared", 32, 30,
            mergeTextConflicts: true, externalWriterDetection: true, cancellationToken: default);
        var heartbeatAt = DateTimeOffset.UtcNow;
        await coordinator.AcquireRunMembershipAsync(workspace.WorkspaceId, request.RunId, "ru_peer", heartbeatAt,
            TimeSpan.FromMinutes(1), default);
        Assert.True(await coordinator.RenewRunMembershipAsync(workspace.WorkspaceId, request.RunId,
            heartbeatAt.AddSeconds(1), TimeSpan.FromMinutes(1), default));
        Assert.Single(await coordinator.ListChangesAsync(workspace.WorkspaceId, afterSequence: 0, limit: 10,
            cancellationToken: default));

        var context = await new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore))
            .AssembleAsync(request.RunId, default);

        Assert.Equal(VerificationCheckStatus.Passed, Assert.Single(context.VerificationEvidence.Checks).Status);
        Assert.Null(context.VerificationEvidence.BlockingCode);
    }

    [Fact]
    public async Task Workspace_change_at_check_start_is_treated_as_stale()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var workspace = await fixture.RegisterWorkspaceAsync(81);
        var eventStore = fixture.CreateEventStore();
        var coordinator = new WorkspaceCoordinatorStore(fixture.Options);
        await coordinator.GetOrCreateAsync(workspace.WorkspaceId, default);
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await AppendWorkspaceChangeAsync(fixture, workspace.WorkspaceId, runId: null, "chg_81_boundary", startedAt);
        var request = await CreateVerificationRunAsync(fixture, eventStore, 81, 81, 81, includeRequest: true,
            startedAtUtc: startedAt);

        var context = await new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore))
            .AssembleAsync(request.RunId, default);

        var check = Assert.Single(context.VerificationEvidence.Checks);
        Assert.Equal(VerificationCheckStatus.Stale, check.Status);
        Assert.Equal("verification_stale", check.Code);
    }

    [Fact]
    public async Task Own_recorded_write_before_verification_start_does_not_stale_the_check()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var workspace = await fixture.RegisterWorkspaceAsync(77);
        var eventStore = fixture.CreateEventStore();
        var coordinator = new WorkspaceCoordinatorStore(fixture.Options);
        await coordinator.GetOrCreateAsync(workspace.WorkspaceId, default);
        var request = await CreateVerificationRunAsync(fixture, eventStore, 77, 77, 77, includeRequest: true,
            beforeProposal: runId => AppendWorkspaceChangeAsync(fixture, workspace.WorkspaceId, runId,
                "chg_77_before", DateTimeOffset.UtcNow.AddMinutes(-2)));

        var context = await new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore))
            .AssembleAsync(request.RunId, default);

        Assert.Equal(VerificationCheckStatus.Passed, Assert.Single(context.VerificationEvidence.Checks).Status);
        Assert.Null(context.VerificationEvidence.BlockingCode);
    }

    [Fact]
    public async Task Unchanged_workspace_keeps_passed_verification_fresh()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var workspace = await fixture.RegisterWorkspaceAsync(78);
        var eventStore = fixture.CreateEventStore();
        var coordinator = new WorkspaceCoordinatorStore(fixture.Options);
        await coordinator.GetOrCreateAsync(workspace.WorkspaceId, default);
        var request = await CreateVerificationRunAsync(fixture, eventStore, 78, 78, 78, includeRequest: true);

        var context = await new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore))
            .AssembleAsync(request.RunId, default);

        Assert.Equal(VerificationCheckStatus.Passed, Assert.Single(context.VerificationEvidence.Checks).Status);
        Assert.Null(context.VerificationEvidence.BlockingCode);
    }

    [Fact]
    public async Task Fresh_check_after_retention_ignores_pruned_changes_that_precede_its_start()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var workspace = await fixture.RegisterWorkspaceAsync(79);
        var eventStore = fixture.CreateEventStore();
        var coordinator = new WorkspaceCoordinatorStore(fixture.Options);
        await coordinator.GetOrCreateAsync(workspace.WorkspaceId, default);
        var now = DateTimeOffset.UtcNow;
        await AppendWorkspaceChangeAsync(fixture, workspace.WorkspaceId, runId: null, "chg_79_old",
            now.AddDays(-2));
        var pruned = await coordinator.PruneChangeJournalAsync(now.AddDays(-1), 1024 * 1024, default);
        Assert.Equal(1, pruned.Total);

        var request = await CreateVerificationRunAsync(fixture, eventStore, 79, 79, 79, includeRequest: true,
            startedAtUtc: now.AddDays(-1).AddMinutes(1));
        var context = await new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore))
            .AssembleAsync(request.RunId, default);

        Assert.Equal(VerificationCheckStatus.Passed, Assert.Single(context.VerificationEvidence.Checks).Status);
        Assert.Null(context.VerificationEvidence.BlockingCode);
    }

    [Fact]
    public async Task Retention_gap_after_check_start_keeps_the_check_stale()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var workspace = await fixture.RegisterWorkspaceAsync(80);
        var eventStore = fixture.CreateEventStore();
        var coordinator = new WorkspaceCoordinatorStore(fixture.Options);
        await coordinator.GetOrCreateAsync(workspace.WorkspaceId, default);
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var request = await CreateVerificationRunAsync(fixture, eventStore, 80, 80, 80, includeRequest: true,
            startedAtUtc: startedAt);
        await AppendWorkspaceChangeAsync(fixture, workspace.WorkspaceId, runId: null, "chg_80_during",
            startedAt.AddMinutes(1));
        var pruned = await coordinator.PruneChangeJournalAsync(DateTimeOffset.UtcNow.AddMinutes(1), 1024 * 1024, default);
        Assert.Equal(1, pruned.Total);

        var context = await new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore,
            enableEntryCache: false)).AssembleAsync(request.RunId, default);
        var check = Assert.Single(context.VerificationEvidence.Checks);
        Assert.Equal(VerificationCheckStatus.Stale, check.Status);
        Assert.Equal("verification_freshness_unknown", check.Code);
    }

    [Fact]
    public async Task Workspace_journal_gap_after_compaction_and_restart_stales_passed_check()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var workspace = await fixture.RegisterWorkspaceAsync(73);
        var eventStore = fixture.CreateEventStore();
        var coordinator = new WorkspaceCoordinatorStore(fixture.Options);
        await coordinator.GetOrCreateAsync(workspace.WorkspaceId, default);
        var request = await CreateVerificationRunAsync(fixture, eventStore, 73, 73, 73, includeRequest: true);
        var initial = await new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore))
            .AssembleAsync(request.RunId, default);
        var startedAt = Assert.Single(initial.VerificationEvidence.Checks).ExecutionStartedAtUtc!.Value;
        await AppendWorkspaceChangeAsync(fixture, workspace.WorkspaceId, runId: null, "chg_73_later",
            startedAt.AddSeconds(1));

        var compacted = NextRun(request, 4, 5);
        await CommitCompactionAsync(eventStore, request.RunId, compacted, 3, 76, "compact-freshness-73",
            "The previous check passed", $"event:{HarnessStoreTestFixture.Event(733).Value}", compacted.UpdatedAt, 4);

        var restarted = await new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore,
            enableEntryCache: false)).AssembleAsync(request.RunId, default);
        var check = Assert.Single(restarted.VerificationEvidence.Checks);
        Assert.Equal(VerificationCheckStatus.Stale, check.Status);
        Assert.Equal("verification_stale", check.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_execution_or_journal_provenance_cannot_certify_pass(bool missingExecutionRequest)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var workspace = await fixture.RegisterWorkspaceAsync(missingExecutionRequest ? 74 : 75);
        var eventStore = fixture.CreateEventStore();
        var coordinator = new WorkspaceCoordinatorStore(fixture.Options);
        await coordinator.GetOrCreateAsync(workspace.WorkspaceId, default);
        var runNumber = missingExecutionRequest ? 74 : 75;
        var request = await CreateVerificationRunAsync(fixture, eventStore, runNumber, runNumber, runNumber,
            includeRequest: !missingExecutionRequest);
        if (!missingExecutionRequest)
        {
            await AppendWorkspaceChangeAsync(fixture, workspace.WorkspaceId, runId: null, "chg_75_gap",
                DateTimeOffset.UtcNow.AddMinutes(1));
            await using var connection = fixture.CreateRawConnection();
            await connection.OpenAsync();
            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM workspace_change_journal WHERE workspace_id=$workspace";
            delete.Parameters.AddWithValue("$workspace", workspace.WorkspaceId.Value);
            await delete.ExecuteNonQueryAsync();
        }

        var context = await new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore))
            .AssembleAsync(request.RunId, default);
        var check = Assert.Single(context.VerificationEvidence.Checks);
        Assert.Equal(VerificationCheckStatus.Stale, check.Status);
        Assert.Equal("verification_freshness_unknown", check.Code);
    }

    private static async Task<AgentRun> CreateVerificationRunAsync(
        HarnessStoreTestFixture fixture,
        SqliteHarnessEventStore store,
        int runNumber,
        int sessionNumber,
        int workspaceNumber,
        bool includeRequest,
        Func<RunId, Task>? beforeProposal = null,
        DateTimeOffset? startedAtUtc = null)
    {
        var request = fixture.NewRun(runNumber, sessionNumber, workspaceNumber);
        Assert.Equal(CreateRunOutcome.Created, (await store.TryCreateRunAsync(request, default)).Outcome);
        await fixture.PayloadStore.CaptureBaseContextAsync(request.Run.RunId, [], HarnessStoreTestFixture.Timestamp, default);
        if (beforeProposal is not null) await beforeProposal(request.Run.RunId);

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
        HarnessEventIntent[] requestEvents = includeRequest
            ? [new HarnessEventIntent(HarnessStoreTestFixture.Event(runNumber * 10 + 2),
                HarnessEventKind.ToolExecutionRequested, modelStepId: modelStepId, toolCallId: callId,
                executionId: executionId, providerCallId: providerCallId)]
            : [new HarnessEventIntent(HarnessStoreTestFixture.Event(runNumber * 10 + 2),
                HarnessEventKind.RunNoteAdded, safeCode: "verification_test_note")];
        Assert.Equal(HarnessCommitOutcome.Committed, (await store.CommitAsync(new HarnessCommit(request.Run.RunId, 1,
            requested, requestEvents, [], []), default)).Outcome);
        if (includeRequest)
        {
            var startedAt = (startedAtUtc ?? DateTimeOffset.UtcNow.AddMinutes(-1)).ToString("O");
            await using var connection = fixture.CreateRawConnection();
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE agent_events SET occurred_at_utc=$started WHERE run_id=$run AND kind='tool.execution_requested'";
            update.Parameters.AddWithValue("$started", startedAt);
            update.Parameters.AddWithValue("$run", request.Run.RunId.Value);
            await update.ExecuteNonQueryAsync();
        }

        var result = new HarnessPayloadIntent(HarnessStoreTestFixture.Payload(runNumber * 10 + 3),
            HarnessPayloadKind.ToolOutput,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text, 4096,
            Encoding.UTF8.GetBytes("{\"exit_code\":0,\"verification_identity\":\"" + new string('a', 64) + "\"}"));
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
