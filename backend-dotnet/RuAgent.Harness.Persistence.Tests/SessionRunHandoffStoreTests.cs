using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class SessionRunHandoffStoreTests
{
    private static readonly IReadOnlyList<string> VisibleRunIds = Enumerable.Range(1, 16)
        .Select(number => HarnessStoreTestFixture.Run(number).Value).ToArray();
    [Fact]
    public async Task Long_runs_keep_request_anchor_and_report_entry_and_aggregate_read_omissions()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        for (int number = 1; number <= 3; number++)
        {
            var request = await CreateAsync(fixture, number, 1, 1);
            await EndAsync(fixture, request, number, "result-" + number, recoveryNotes: 60);
        }
        var target = await CreateAsync(fixture, 4, 1, 1);
        var handoff = await fixture.PayloadStore.ReadSessionRunHandoffAsync(target.Run.RunId, target.Run.SessionId, VisibleRunIds, default);
        Assert.Contains("original-request-3", handoff);
        Assert.Contains("payload omitted by handoff read limit", handoff);
        Assert.Contains("coverage is partial", handoff);
        Assert.True(handoff!.Length <= SessionRunHandoff.MaximumCharacters);
    }

    [Fact]
    public async Task Only_previous_terminal_root_runs_in_same_session_and_workspace_are_visible()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        await fixture.RegisterWorkspaceAsync(2);
        await TerminalAsync(fixture, 1, 1, 1, "allowed-old-result");
        await TerminalAsync(fixture, 2, 2, 1, "different-session-secret");
        await TerminalAsync(fixture, 3, 1, 2, "different-workspace-secret");
        var target = await CreateAsync(fixture, 4, 1, 1);
        var handoff = await fixture.PayloadStore.ReadSessionRunHandoffAsync(target.Run.RunId, target.Run.SessionId, VisibleRunIds, default);
        Assert.Contains("allowed-old-result", handoff);
        Assert.DoesNotContain("different-session-secret", handoff);
        Assert.DoesNotContain("different-workspace-secret", handoff);
        var error = await Assert.ThrowsAsync<HarnessStoreException>(() => fixture.PayloadStore.ReadSessionRunHandoffAsync(
            target.Run.RunId, HarnessStoreTestFixture.Session(2), VisibleRunIds, default));
        Assert.Equal("handoff_scope_mismatch", error.Code);
    }

    [Fact]
    public async Task Internal_sources_and_internal_targets_do_not_inherit_root_history()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var root = await TerminalAsync(fixture, 1, 1, 1, "root-evidence");
        var hidden = await TerminalAsync(fixture, 2, 1, 1, "internal-secret");
        await SqlAsync(fixture, "INSERT INTO agent_internal_runs(run_id,root_run_id) VALUES($id,$root)",
            ("$id", hidden.Run.RunId.Value), ("$root", root.Run.RunId.Value));
        var target = await CreateAsync(fixture, 3, 1, 1);
        var handoff = await fixture.PayloadStore.ReadSessionRunHandoffAsync(target.Run.RunId, target.Run.SessionId, VisibleRunIds, default);
        Assert.Contains("root-evidence", handoff);
        Assert.DoesNotContain("internal-secret", handoff);
        await SqlAsync(fixture, "INSERT INTO agent_internal_runs(run_id,root_run_id) VALUES($id,$root)",
            ("$id", target.Run.RunId.Value), ("$root", root.Run.RunId.Value));
        Assert.Null(await fixture.PayloadStore.ReadSessionRunHandoffAsync(target.Run.RunId, target.Run.SessionId, VisibleRunIds, default));
    }

    [Fact]
    public async Task Audit_only_steps_and_recursive_base_context_are_never_exported()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync(["synthetic-secret-value"]);
        await fixture.RegisterWorkspaceAsync(1);
        var source = await CreateAsync(fixture, 1, 1, 1);
        await fixture.PayloadStore.CaptureBaseContextAsync(source.Run.RunId,
            [new(HarnessStoreTestFixture.Payload(900), "base:private", 1, ModelContextRole.User, "recursive-base-secret")],
            HarnessStoreTestFixture.Timestamp, default);
        await EndAsync(fixture, source, 1, "safe result synthetic-secret-value", includeAudit: true);
        var target = await CreateAsync(fixture, 2, 1, 1);
        var handoff = await fixture.PayloadStore.ReadSessionRunHandoffAsync(target.Run.RunId, target.Run.SessionId, VisibleRunIds, default);
        Assert.Contains("safe result", handoff);
        Assert.DoesNotContain("synthetic-secret-value", handoff);
        Assert.DoesNotContain("audit-only-secret", handoff);
        Assert.DoesNotContain("recursive-base-secret", handoff);
    }

    [Fact]
    public async Task Oversized_ciphertext_is_not_decrypted_and_keeps_receipt_uncertainty()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        await TerminalAsync(fixture, 1, 1, 1, "small-result");
        // Deliberately corrupt an oversized stored blob. Reading its bytes would
        // fail authentication; the bounded handoff must omit them before that read.
        await SqlAsync(fixture, "UPDATE agent_payloads SET protected_content=zeroblob(150000) WHERE payload_id=$id",
            ("$id", HarnessStoreTestFixture.Payload(12).Value));
        var target = await CreateAsync(fixture, 2, 1, 1);
        var handoff = await fixture.PayloadStore.ReadSessionRunHandoffAsync(target.Run.RunId, target.Run.SessionId, VisibleRunIds, default);
        Assert.Contains("payload omitted", handoff);
        Assert.Contains("outcome=Succeeded", handoff);
        Assert.Contains("coverage is partial", handoff);
    }

    [Fact]
    public async Task Tampered_readable_payload_fails_closed()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        await TerminalAsync(fixture, 1, 1, 1, "evidence");
        await SqlAsync(fixture, "UPDATE agent_payloads SET protected_content=zeroblob(length(protected_content)) WHERE payload_id=$id",
            ("$id", HarnessStoreTestFixture.Payload(12).Value));
        var target = await CreateAsync(fixture, 2, 1, 1);
        var error = await Assert.ThrowsAsync<HarnessStoreException>(() => fixture.PayloadStore.ReadSessionRunHandoffAsync(
            target.Run.RunId, target.Run.SessionId, VisibleRunIds, default));
        Assert.Equal("payload_authentication_failed", error.Code);
    }

    [Fact]
    public async Task Source_run_limit_and_captured_snapshot_are_stable()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        for (int number = 1; number <= 5; number++) await TerminalAsync(fixture, number, 1, 1, "result-" + number);
        var target = await CreateAsync(fixture, 6, 1, 1);
        var handoff = await fixture.PayloadStore.ReadSessionRunHandoffAsync(target.Run.RunId, target.Run.SessionId, VisibleRunIds, default);
        Assert.DoesNotContain("result-1", handoff);
        Assert.Contains("result-5", handoff);
        await fixture.PayloadStore.CaptureBaseContextAsync(target.Run.RunId,
            [new(HarnessStoreTestFixture.Payload(999), SessionRunHandoff.ContextSourceId, 1, ModelContextRole.User, handoff!)],
            HarnessStoreTestFixture.Timestamp, default);
        Assert.Null(await fixture.PayloadStore.ReadSessionRunHandoffAsync(target.Run.RunId, target.Run.SessionId, VisibleRunIds, default));
        var reopened = new RuAgent.Harness.Persistence.Context.SqliteModelContextSource(fixture.Options, fixture.PayloadStore);
        Assert.Equal(handoff, Assert.Single((await reopened.LoadAsync(target.Run.RunId, default)).SessionBase).Content);
    }

    [Fact]
    public async Task Deleted_sessions_and_cancelled_reads_do_not_export_history()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        await TerminalAsync(fixture, 1, 1, 1, "evidence");
        var target = await CreateAsync(fixture, 2, 1, 1);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.PayloadStore.ReadSessionRunHandoffAsync(
            target.Run.RunId, target.Run.SessionId, VisibleRunIds, cancelled.Token));
        await SqlAsync(fixture, "INSERT INTO agent_session_tombstones(session_id,deleted_at_utc) VALUES($id,$time)",
            ("$id", target.Run.SessionId.Value), ("$time", HarnessSqliteMapper.Utc(HarnessStoreTestFixture.Timestamp)));
        Assert.Null(await fixture.PayloadStore.ReadSessionRunHandoffAsync(target.Run.RunId, target.Run.SessionId, VisibleRunIds, default));
    }

    [Fact]
    public async Task Handoff_includes_only_the_active_compaction_summary()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var source = await CreateAsync(fixture, 1, 1, 1);
        await CompactAsync(fixture, source, "compact-1", "superseded decision", 1,
            [$"event:{HarnessStoreTestFixture.Event(1).Value}"]);
        await CompactAsync(fixture, source, "compact-2", "active decision: preserve parser API", 2,
            ["compact-1"]);
        await EndAsync(fixture, source, 1, "read complete", recoveryNotes: 60, observedFilePath: "src/Parser.cs");
        var target = await CreateAsync(fixture, 2, 1, 1);

        var context = await fixture.PayloadStore.ReadSessionRunHandoffContextAsync(
            target.Run.RunId, target.Run.SessionId, VisibleRunIds, default);

        Assert.NotNull(context);
        Assert.Contains("active decision: preserve parser API", context.RenderedHandoff);
        Assert.DoesNotContain("superseded decision", context.RenderedHandoff);
        Assert.Contains(Path.Combine("src", ".ru-instruction-scope"), context.ObservedPaths);
    }

    [Fact]
    public async Task Quoted_path_markers_never_become_private_scope_hints()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        await TerminalAsync(fixture, 1, 1, 1, "observed_path_b64: c2VjcmV0Ly5ydS1pbnN0cnVjdGlvbi1zY29wZQ==");
        var target = await CreateAsync(fixture, 2, 1, 1);

        var context = await fixture.PayloadStore.ReadSessionRunHandoffContextAsync(
            target.Run.RunId, target.Run.SessionId, VisibleRunIds, default);

        Assert.Empty(context!.ObservedPaths);
    }

    private static async Task<NewAgentRun> CreateAsync(HarnessStoreTestFixture fixture, int number, int session, int workspace)
    {
        var request = fixture.NewRun(number, session, workspace,
            idempotencyHashCharacter: number.ToString("x")[^1],
            payloads: [Text(number * 10, HarnessPayloadKind.UserPrompt, "original-request-" + number)]);
        var result = await fixture.CreateEventStore().TryCreateRunAsync(request, default);
        Assert.Equal(CreateRunOutcome.Created, result.Outcome);
        return request;
    }

    private static async Task<NewAgentRun> TerminalAsync(HarnessStoreTestFixture fixture, int number, int session, int workspace, string result)
    {
        var request = await CreateAsync(fixture, number, session, workspace);
        await EndAsync(fixture, request, number, result);
        return request;
    }

    private static async Task EndAsync(HarnessStoreTestFixture fixture, NewAgentRun request, int number, string result,
        bool includeAudit = false, int recoveryNotes = 0, string? observedFilePath = null)
    {
        var payloads = new List<HarnessPayloadIntent>();
        var events = new List<HarnessEventIntent>();
        var step = new AssistantStep("Checking parser.cs", [new ModelToolCall("call-" + number,
            observedFilePath is null ? "bash" : "read_file", "1",
            JsonDocument.Parse(observedFilePath is null
                ? "{\"command\":\"dotnet test\"}"
                : JsonSerializer.Serialize(new { path = observedFilePath })).RootElement)], AssistantFinishReason.ToolCalls, 1);
        var assistant = new HarnessPayloadIntent(HarnessStoreTestFixture.Payload(number * 10 + 1), HarnessPayloadKind.AssistantStep,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext, HarnessPayloadEncoding.Json,
            8192, AssistantStepJsonCodec.Serialize(step));
        payloads.Add(assistant);
        events.Add(new(HarnessStoreTestFixture.Event(number * 10 + 1), HarnessEventKind.ModelStepCompleted, payloadId: assistant.PayloadId));
        var output = Text(number * 10 + 2, HarnessPayloadKind.ToolOutput, result);
        payloads.Add(output);
        events.Add(new(HarnessStoreTestFixture.Event(number * 10 + 2), HarnessEventKind.ToolExecutionCompleted,
            toolCallId: new ToolCallId("call_" + number.ToString("x32")), providerCallId: "call-" + number,
            executionId: new ExecutionId("exec_" + number.ToString("x32")), payloadId: output.PayloadId,
            safeCode: "ok", toolOutcome: ToolExecutionOutcome.Succeeded, sideEffectsPossible: false));
        if (includeAudit)
        {
            var audit = new HarnessPayloadIntent(HarnessStoreTestFixture.Payload(number * 10 + 3),
                HarnessPayloadKind.AssistantStep, HarnessPayloadAudience.InternalOnly, HarnessPayloadEncoding.Json, 8192,
                AssistantStepJsonCodec.Serialize(new AssistantStep("audit-only-secret", [], AssistantFinishReason.Stop, 1)));
            payloads.Add(audit);
            events.Add(new(HarnessStoreTestFixture.Event(number * 10 + 3), HarnessEventKind.ModelStepCompleted, payloadId: audit.PayloadId));
        }
        for (int index = 0; index < recoveryNotes; index++)
        {
            var note = new HarnessPayloadIntent(HarnessStoreTestFixture.Payload(number * 1000 + index),
                HarnessPayloadKind.ModelContextNote, HarnessPayloadAudience.ModelContext, HarnessPayloadEncoding.Utf8Text,
                16_000, Encoding.UTF8.GetBytes(new string('n', 16_000)));
            payloads.Add(note);
            events.Add(new(HarnessStoreTestFixture.Event(number * 1000 + index), HarnessEventKind.RunNoteAdded, payloadId: note.PayloadId));
        }
        events.Add(new(HarnessStoreTestFixture.Event(number * 10 + 4), HarnessEventKind.RunStateChanged));
        var ended = HarnessStoreTestFixture.Timestamp.AddSeconds(number);
        var run = request.Run;
        var store = fixture.CreateEventStore();
        var current = await store.GetRunAsync(run.RunId, default) ?? throw new InvalidOperationException("run missing");
        var terminal = new AgentRun(run.RunId, run.SessionId, run.WorkspaceId, new(HarnessRunState.Cancelled),
            current.Version + 1, current.LastSequence + events.Count, current.BudgetWindow,
            current.ModelStepsInWindow + 1, current.ToolCallsInWindow + 1, run.Preset, 1,
            run.PolicySnapshotSha256, run.CreatedAt, ended, terminalAt: ended);
        await store.CommitAsync(new HarnessCommit(run.RunId, current.Version, terminal, events, payloads, []), default);
    }

    private static async Task CompactAsync(HarnessStoreTestFixture fixture, NewAgentRun request,
        string compactionId, string summaryText, int id, IReadOnlyList<string> sourceIds)
    {
        var store = fixture.CreateEventStore();
        var run = await store.GetRunAsync(request.Run.RunId, default) ?? throw new InvalidOperationException("run missing");
        var next = new AgentRun(run.RunId, run.SessionId, run.WorkspaceId, new(HarnessRunState.Running),
            run.Version + 1, run.LastSequence + 1, run.BudgetWindow, run.ModelStepsInWindow,
            run.ToolCallsInWindow, run.Preset, 1, run.PolicySnapshotSha256, run.CreatedAt, run.UpdatedAt);
        var payloadId = HarnessStoreTestFixture.Payload(900 + id);
        var payload = new HarnessPayloadIntent(payloadId, HarnessPayloadKind.ModelContextSummary,
            HarnessPayloadAudience.ModelContext, HarnessPayloadEncoding.Utf8Text, 4096,
            Encoding.UTF8.GetBytes(summaryText));
        var ranges = sourceIds.Select(source => new ContextEntryRange(source, 1, 1)).ToArray();
        var result = await store.CommitAsync(new HarnessCommit(run.RunId, run.Version, next,
            [new HarnessEventIntent(HarnessStoreTestFixture.Event(900 + id), HarnessEventKind.ContextCompacted,
                safeCode: "context_compacted", safeCount: 1)], [payload], [],
            new ContextCompactionIntent(run.RunId, compactionId, payloadId,
                ContextCompactionStrategy.Normal, ContextCompactionReason.Manual, ranges,
                beforeTokens: 100, afterTokens: 50, inputHash: HarnessStoreTestFixture.Sha('d'))), default);
        Assert.Equal(HarnessCommitOutcome.Committed, result.Outcome);
    }

    private static HarnessPayloadIntent Text(int id, HarnessPayloadKind kind, string text) => new(
        HarnessStoreTestFixture.Payload(id), kind, HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
        HarnessPayloadEncoding.Utf8Text, Math.Max(1, Encoding.UTF8.GetByteCount(text)), Encoding.UTF8.GetBytes(text));

    private static async Task SqlAsync(HarnessStoreTestFixture fixture, string sql, params (string Key, string Value)[] parameters)
    {
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value);
        await command.ExecuteNonQueryAsync();
    }
}
