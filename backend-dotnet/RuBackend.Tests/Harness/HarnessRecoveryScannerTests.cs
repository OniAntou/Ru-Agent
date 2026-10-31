using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using RuBackend.Business;
using RuBackend.Business.Sessions;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Persistence;
using RuBackend.Harness;
using RuBackend.Harness.Reconciliation;
using Xunit;

using RuAgent.Protocol.Harness;
namespace RuBackend.Tests.Harness;

public sealed class HarnessRecoveryScannerTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 24, 1, 2, 3, TimeSpan.Zero);

    private static readonly ExecutionId AmbiguousExecution =
        new("exec_11111111111111111111111111111111");

    private static readonly ToolCallId AmbiguousCall =
        new("call_11111111111111111111111111111111");

    [Fact]
    public async Task Retained_approval_rehydrates_the_complete_encrypted_batch_in_order()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var run = await SeedApprovalBatchAsync(fixture);
            var rehydrator = new RetainedApprovalRehydrator(
                fixture.EventStore,
                fixture.PayloadStore,
                new FixedTime(Timestamp.AddMinutes(1)));
            var step = await rehydrator.RehydrateAsync(run.RunId, CancellationToken.None);

            Assert.NotNull(step);
            Assert.Equal(3, step!.Proposals.Count);
            Assert.Equal(new[] { 1, 2, 3 }, step.Proposals.Select(item => item.ModelOrder));
            Assert.Equal(
                new[] { "provider_1", "provider_2", "provider_3" },
                step.Proposals.Select(item => item.ProviderCallId));
            Assert.Equal("read", step.Proposals[0].ToolId);
            Assert.Equal("1", step.Proposals[0].ToolVersion);
            Assert.Equal(
                "secret-argument-1",
                step.Proposals[0].Arguments.GetProperty("path").GetString());
            Assert.Equal("apr_" + new string('d', 32), step.ApprovalId.Value);
            Assert.Equal("provider_2", step.Proposals[1].ProviderCallId);
            Assert.Equal("step_11111111111111111111111111111111", step.ModelStepId.Value);

            await using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = fixture.Options.DatabasePath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString());
            await connection.OpenAsync();
            var argumentPayloadIds = await fixture.PayloadStore.ListPayloadIdsAsync(
                run.RunId,
                HarnessPayloadKind.ToolArguments,
                CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT protected_content FROM agent_payloads WHERE payload_id = $payload_id";
            command.Parameters.AddWithValue("$payload_id", argumentPayloadIds[0].Value);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var ciphertext = (byte[])reader[0];
            Assert.DoesNotContain(
                "secret-argument-1",
                System.Text.Encoding.UTF8.GetString(ciphertext),
                StringComparison.Ordinal);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Retained_approval_rehydration_returns_null_after_all_batch_receipts()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var waiting = await SeedApprovalBatchAsync(fixture);
            var current = await fixture.EventStore.GetRunAsync(waiting.RunId, CancellationToken.None);
            Assert.NotNull(current);
            var toolCalls = new[]
            {
                new ToolCallId("call_" + new string('1', 32)),
                new ToolCallId("call_" + new string('2', 32)),
                new ToolCallId("call_" + new string('3', 32)),
            };
            var events = new List<HarnessEventIntent>
            {
                new(
                    new EventId("evt_" + new string('f', 32)),
                    HarnessEventKind.RunStateChanged,
                    safeCode: "approval_resolved"),
            };
            for (var index = 0; index < toolCalls.Length; index++)
            {
                var executionId = new ExecutionId($"exec_{(index + 1).ToString("x32")}");
                events.Add(new HarnessEventIntent(
                    new EventId($"evt_{(100 + index).ToString("x32")}"),
                    HarnessEventKind.ToolExecutionRequested,
                    modelStepId: new ModelStepId("step_11111111111111111111111111111111"),
                    toolCallId: toolCalls[index],
                    providerCallId: $"provider_{index + 1}",
                    executionId: executionId));
                events.Add(new HarnessEventIntent(
                    new EventId($"evt_{(200 + index).ToString("x32")}"),
                    HarnessEventKind.ToolExecutionCompleted,
                    modelStepId: new ModelStepId("step_11111111111111111111111111111111"),
                    toolCallId: toolCalls[index],
                    providerCallId: $"provider_{index + 1}",
                    executionId: executionId,
                    safeCode: "ok",
                    toolOutcome: ToolExecutionOutcome.Succeeded,
                    sideEffectsPossible: false));
            }

            var next = new AgentRun(
                current!.RunId,
                current.SessionId,
                current.WorkspaceId,
                new HarnessRunStatus(HarnessRunState.Running),
                current.Version + 1,
                current.LastSequence + events.Count,
                current.BudgetWindow,
                current.ModelStepsInWindow,
                current.ToolCallsInWindow,
                current.Preset,
                current.PolicySnapshotVersion,
                current.PolicySnapshotSha256,
                current.CreatedAt,
                Timestamp.AddSeconds(2));
            var commit = await fixture.EventStore.CommitAsync(
                new HarnessCommit(
                    current.RunId,
                    current.Version,
                    next,
                    events,
                    Array.Empty<HarnessPayloadIntent>(),
                    Array.Empty<ConversationOutboxIntent>()),
                CancellationToken.None);
            Assert.Equal(HarnessCommitOutcome.Committed, commit.Outcome);

            var rehydrator = new RetainedApprovalRehydrator(
                fixture.EventStore,
                fixture.PayloadStore,
                new FixedTime(Timestamp.AddMinutes(1)));
            var step = await rehydrator.RehydrateAsync(waiting.RunId, CancellationToken.None);

            Assert.Null(step);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Retained_approval_rehydration_excludes_denials_around_the_ask()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var run = await SeedApprovalBatchAsync(fixture, BatchCorruption.DeniedAroundAsk);
            var rehydrator = new RetainedApprovalRehydrator(
                fixture.EventStore,
                fixture.PayloadStore,
                new FixedTime(Timestamp.AddMinutes(1)));
            var step = await rehydrator.RehydrateAsync(run.RunId, CancellationToken.None);

            Assert.NotNull(step);
            Assert.Single(step!.Proposals);
            Assert.Equal(2, step.Proposals[0].ModelOrder);
            Assert.Equal("provider_2", step.Proposals[0].ProviderCallId);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Retained_approval_rehydration_rejects_an_expired_request()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var run = await SeedApprovalBatchAsync(fixture);
            var rehydrator = new RetainedApprovalRehydrator(
                fixture.EventStore,
                fixture.PayloadStore,
                new FixedTime(Timestamp.AddHours(1)));

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                rehydrator.RehydrateAsync(run.RunId, CancellationToken.None));

            Assert.Equal("retained_approval_expired", error.Message);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(BatchCorruption.MissingOrder)]
    [InlineData(BatchCorruption.DuplicateOrder)]
    [InlineData(BatchCorruption.OutOfOrder)]
    [InlineData(BatchCorruption.MismatchedVersion)]
    [InlineData(BatchCorruption.MismatchedProvider)]
    [InlineData(BatchCorruption.MismatchedSafeCount)]
    [InlineData(BatchCorruption.MissingAssistant)]
    public async Task Retained_approval_rehydration_rejects_broken_identity_data(
        BatchCorruption corruption)
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var run = await SeedApprovalBatchAsync(fixture, corruption);
            var rehydrator = new RetainedApprovalRehydrator(
                fixture.EventStore,
                fixture.PayloadStore,
                new FixedTime(Timestamp.AddMinutes(1)));

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                rehydrator.RehydrateAsync(run.RunId, CancellationToken.None));

            Assert.DoesNotContain("secret-argument-1", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Retained_approval_rehydration_accepts_a_fully_denied_batch_without_approval()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var run = await SeedApprovalBatchAsync(fixture, BatchCorruption.AllDenied);
            var rehydrator = new RetainedApprovalRehydrator(
                fixture.EventStore,
                fixture.PayloadStore,
                new FixedTime(Timestamp.AddMinutes(1)));

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                rehydrator.RehydrateAsync(run.RunId, CancellationToken.None));

            Assert.Equal("retained_approval_missing", error.Message);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Retained_approval_rehydration_fails_closed_without_an_approval_event()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var run = await SeedApprovalBatchAsync(fixture, BatchCorruption.MissingApproval);
            var rehydrator = new RetainedApprovalRehydrator(
                fixture.EventStore,
                fixture.PayloadStore,
                new FixedTime(Timestamp.AddMinutes(1)));

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                rehydrator.RehydrateAsync(run.RunId, CancellationToken.None));

            Assert.Equal("retained_approval_missing", error.Message);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Retained_approval_rehydration_rejects_mismatched_execution_identity()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var run = await SeedApprovalBatchAsync(fixture, BatchCorruption.ExecutionIdMismatch);
            var rehydrator = new RetainedApprovalRehydrator(
                fixture.EventStore,
                fixture.PayloadStore,
                new FixedTime(Timestamp.AddMinutes(1)));

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                rehydrator.RehydrateAsync(run.RunId, CancellationToken.None));

            Assert.Equal("retained_approval_execution_mismatch", error.Message);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Retained_approval_rehydration_rejects_unknown_outcomes()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var run = await SeedApprovalBatchAsync(fixture, BatchCorruption.OutcomeUnknown);
            var rehydrator = new RetainedApprovalRehydrator(
                fixture.EventStore,
                fixture.PayloadStore,
                new FixedTime(Timestamp.AddMinutes(1)));

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                rehydrator.RehydrateAsync(run.RunId, CancellationToken.None));

            Assert.Equal("retained_approval_execution_unknown", error.Message);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Complete_allow_batch_is_skipped_when_run_is_running()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var run = await SeedApprovalBatchAsync(fixture, BatchCorruption.AllAllowComplete);
            var rehydrator = new RetainedApprovalRehydrator(
                fixture.EventStore,
                fixture.PayloadStore,
                new FixedTime(Timestamp.AddMinutes(1)));

            var step = await rehydrator.RehydrateAsync(run.RunId, CancellationToken.None);

            Assert.Null(step);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Current_waiting_batch_with_complete_execution_but_no_approval_fails_closed()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var run = await SeedApprovalBatchAsync(fixture, BatchCorruption.MissingApprovalComplete);
            var rehydrator = new RetainedApprovalRehydrator(
                fixture.EventStore,
                fixture.PayloadStore,
                new FixedTime(Timestamp.AddMinutes(1)));

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                rehydrator.RehydrateAsync(run.RunId, CancellationToken.None));

            Assert.Equal("retained_approval_missing", error.Message);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Completion_before_request_is_rejected()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var run = await SeedApprovalBatchAsync(fixture, BatchCorruption.CompletionBeforeRequest);
            var rehydrator = new RetainedApprovalRehydrator(
                fixture.EventStore,
                fixture.PayloadStore,
                new FixedTime(Timestamp.AddMinutes(1)));

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                rehydrator.RehydrateAsync(run.RunId, CancellationToken.None));

            Assert.Equal("retained_approval_execution_order", error.Message);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Orphan_execution_model_step_is_rejected()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var run = await SeedApprovalBatchAsync(fixture, BatchCorruption.OrphanExecutionStep);
            var rehydrator = new RetainedApprovalRehydrator(
                fixture.EventStore,
                fixture.PayloadStore,
                new FixedTime(Timestamp.AddMinutes(1)));

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                rehydrator.RehydrateAsync(run.RunId, CancellationToken.None));

            Assert.Equal("retained_approval_execution_model_mismatch", error.Message);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Duplicate_execution_id_is_rejected()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var run = await SeedApprovalBatchAsync(fixture, BatchCorruption.DuplicateExecutionId);
            var rehydrator = new RetainedApprovalRehydrator(
                fixture.EventStore,
                fixture.PayloadStore,
                new FixedTime(Timestamp.AddMinutes(1)));

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                rehydrator.RehydrateAsync(run.RunId, CancellationToken.None));

            Assert.Equal("retained_approval_execution_duplicate", error.Message);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Reconciliation_only_execution_identity_is_rejected()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var run = await SeedApprovalBatchAsync(fixture, BatchCorruption.ReconciliationOnlyExecution);
            var rehydrator = new RetainedApprovalRehydrator(
                fixture.EventStore,
                fixture.PayloadStore,
                new FixedTime(Timestamp.AddMinutes(1)));

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                rehydrator.RehydrateAsync(run.RunId, CancellationToken.None));

            Assert.Equal("retained_approval_execution_reconciliation_mismatch", error.Message);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(BatchCorruption.ResolutionExecutionMismatch)]
    [InlineData(BatchCorruption.ResolutionModelStepMismatch)]
    [InlineData(BatchCorruption.ResolutionToolCallMismatch)]
    [InlineData(BatchCorruption.ResolutionExecutionMissing)]
    [InlineData(BatchCorruption.ResolutionModelStepMissing)]
    [InlineData(BatchCorruption.ResolutionToolCallMissing)]
    public async Task Reconciliation_resolution_requires_exact_execution_identity(
        BatchCorruption corruption)
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var run = await SeedApprovalBatchAsync(fixture, corruption);
            var rehydrator = new RetainedApprovalRehydrator(
                fixture.EventStore,
                fixture.PayloadStore,
                new FixedTime(Timestamp.AddMinutes(1)));

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                rehydrator.RehydrateAsync(run.RunId, CancellationToken.None));

            Assert.Equal("retained_approval_reconciliation_resolution_mismatch", error.Message);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Restart_rehydrator_accepts_execution_specific_reconciliation_resolution()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var run = await SeedApprovalBatchAsync(
                fixture,
                BatchCorruption.ResolvedReconciliationHistory);
            var rehydrator = new RetainedApprovalRehydrator(
                fixture.EventStore,
                fixture.PayloadStore,
                new FixedTime(Timestamp.AddMinutes(1)));

            var step = await rehydrator.RehydrateAsync(run.RunId, CancellationToken.None);

            Assert.NotNull(step);
            Assert.Equal("step_11111111111111111111111111111111", step!.ModelStepId.Value);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Restart_rehydrator_accepts_run_level_reconciliation_resolution()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var run = await SeedApprovalBatchAsync(
                fixture,
                BatchCorruption.RunLevelReconciliationHistory);
            var rehydrator = new RetainedApprovalRehydrator(
                fixture.EventStore,
                fixture.PayloadStore,
                new FixedTime(Timestamp.AddMinutes(1)));

            var step = await rehydrator.RehydrateAsync(run.RunId, CancellationToken.None);

            Assert.NotNull(step);
            Assert.Equal("step_11111111111111111111111111111111", step!.ModelStepId.Value);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_complete_allow_batch_does_not_hide_a_new_incomplete_batch()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var current = await CreateDistinctRunAsync(
                fixture,
                new HarnessRunStatus(HarnessRunState.Running),
                "run_" + Guid.NewGuid().ToString("N"));
            var events = new List<HarnessEventIntent>();
            var payloads = new List<HarnessPayloadIntent>();

            void AddBatch(char marker, bool complete)
            {
                var step = new ModelStepId("step_" + new string(marker, 32));
                var call = new ModelToolCall(
                    "provider_" + marker,
                    "read",
                    "1",
                    JsonSerializer.SerializeToElement(new { path = "file-" + marker }));
                var assistantPayloadId = new PayloadId("payload_" + new string(marker, 31) + "a");
                var assistantBytes = AssistantStepJsonCodec.Serialize(
                    new AssistantStep(null, new[] { call }, AssistantFinishReason.ToolCalls, 1));
                payloads.Add(new HarnessPayloadIntent(
                    assistantPayloadId,
                    HarnessPayloadKind.AssistantStep,
                    HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                    HarnessPayloadEncoding.Json,
                    assistantBytes.Length,
                    assistantBytes));
                var argumentPayloadId = new PayloadId("payload_" + new string(marker, 31) + "b");
                var argumentBytes = System.Text.Encoding.UTF8.GetBytes(new JsonObject
                {
                    ["provider_call_id"] = call.ProviderCallId,
                    ["tool_id"] = call.ToolId,
                    ["tool_version"] = call.ToolVersion,
                    ["arguments"] = JsonNode.Parse(call.Arguments.GetRawText()),
                }.ToJsonString());
                payloads.Add(new HarnessPayloadIntent(
                    argumentPayloadId,
                    HarnessPayloadKind.ToolArguments,
                    HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                    HarnessPayloadEncoding.Json,
                    argumentBytes.Length,
                    argumentBytes));
                var toolCallId = new ToolCallId("call_" + new string(marker, 32));
                events.Add(new HarnessEventIntent(
                    NewEventId(),
                    HarnessEventKind.ModelStepCompleted,
                    modelStepId: step,
                    payloadId: assistantPayloadId,
                    safeCount: 1));
                events.Add(new HarnessEventIntent(
                    NewEventId(),
                    HarnessEventKind.ToolProposed,
                    modelStepId: step,
                    toolCallId: toolCallId,
                    providerCallId: call.ProviderCallId,
                    payloadId: argumentPayloadId,
                    safeCount: 1));
                if (complete)
                {
                    var executionId = new ExecutionId("exec_" + new string(marker, 32));
                    events.Add(new HarnessEventIntent(
                        NewEventId(),
                        HarnessEventKind.ToolExecutionRequested,
                        modelStepId: step,
                        toolCallId: toolCallId,
                        providerCallId: call.ProviderCallId,
                        executionId: executionId,
                        safeCode: "ok"));
                    events.Add(new HarnessEventIntent(
                        NewEventId(),
                        HarnessEventKind.ToolExecutionCompleted,
                        modelStepId: step,
                        toolCallId: toolCallId,
                        providerCallId: call.ProviderCallId,
                        executionId: executionId,
                        safeCode: "ok",
                        toolOutcome: ToolExecutionOutcome.Succeeded,
                        sideEffectsPossible: false));
                }
            }

            AddBatch('1', complete: true);
            AddBatch('2', complete: false);
            var next = new AgentRun(
                current.RunId,
                current.SessionId,
                current.WorkspaceId,
                new HarnessRunStatus(HarnessRunState.WaitingApproval),
                current.Version + 1,
                current.LastSequence + events.Count,
                current.BudgetWindow,
                current.ModelStepsInWindow,
                current.ToolCallsInWindow,
                current.Preset,
                current.PolicySnapshotVersion,
                current.PolicySnapshotSha256,
                current.CreatedAt,
                Timestamp.AddSeconds(1));
            var commit = await fixture.EventStore.CommitAsync(
                new HarnessCommit(
                    current.RunId,
                    current.Version,
                    next,
                    events,
                    payloads,
                    Array.Empty<ConversationOutboxIntent>()),
                CancellationToken.None);
            Assert.Equal(HarnessCommitOutcome.Committed, commit.Outcome);
            var rehydrator = new RetainedApprovalRehydrator(
                fixture.EventStore,
                fixture.PayloadStore,
                new FixedTime(Timestamp.AddMinutes(1)));

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                rehydrator.RehydrateAsync(current.RunId, CancellationToken.None));

            Assert.Equal("retained_approval_missing", error.Message);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task An_old_resolved_batch_does_not_hide_a_new_incomplete_batch()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var current = await CreateDistinctRunAsync(
                fixture,
                new HarnessRunStatus(HarnessRunState.Running),
                "run_" + Guid.NewGuid().ToString("N"));
            var events = new List<HarnessEventIntent>();
            var payloads = new List<HarnessPayloadIntent>();

            void AddBatch(char marker, bool includeApproval)
            {
                var step = new ModelStepId("step_" + new string(marker, 32));
                var call = new ModelToolCall(
                    "provider_" + marker,
                    "read",
                    "1",
                    JsonDocument.Parse($"{{\"path\":\"file-{marker}\"}}").RootElement.Clone());
                var assistantPayloadId = new PayloadId("payload_" + new string(marker, 31) + "a");
                var assistantBytes = AssistantStepJsonCodec.Serialize(
                    new AssistantStep(null, new[] { call }, AssistantFinishReason.ToolCalls, 1));
                payloads.Add(new HarnessPayloadIntent(
                    assistantPayloadId,
                    HarnessPayloadKind.AssistantStep,
                    HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                    HarnessPayloadEncoding.Json,
                    assistantBytes.Length,
                    assistantBytes));
                var argumentPayloadId = new PayloadId("payload_" + new string(marker, 31) + "b");
                var argumentNode = new JsonObject
                {
                    ["provider_call_id"] = call.ProviderCallId,
                    ["tool_id"] = call.ToolId,
                    ["tool_version"] = call.ToolVersion,
                    ["arguments"] = JsonNode.Parse(call.Arguments.GetRawText()),
                };
                var argumentBytes = System.Text.Encoding.UTF8.GetBytes(argumentNode.ToJsonString());
                payloads.Add(new HarnessPayloadIntent(
                    argumentPayloadId,
                    HarnessPayloadKind.ToolArguments,
                    HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                    HarnessPayloadEncoding.Json,
                    argumentBytes.Length,
                    argumentBytes));
                var toolCallId = new ToolCallId("call_" + new string(marker, 32));
                events.Add(new HarnessEventIntent(
                    NewEventId(),
                     HarnessEventKind.ModelStepCompleted,
                     modelStepId: step,
                     payloadId: assistantPayloadId,
                     safeCount: 1));
                events.Add(new HarnessEventIntent(
                    NewEventId(),
                    HarnessEventKind.ToolProposed,
                    modelStepId: step,
                    toolCallId: toolCallId,
                    providerCallId: call.ProviderCallId,
                    payloadId: argumentPayloadId,
                    safeCount: 1));
                if (includeApproval)
                {
                    events.Add(new HarnessEventIntent(
                        NewEventId(),
                        HarnessEventKind.ApprovalRequested,
                        modelStepId: step,
                        toolCallId: toolCallId,
                        providerCallId: call.ProviderCallId,
                        approvalId: new ApprovalId("apr_" + new string(marker, 32)),
                        safeCode: "approval_required"));
                }
            }

            AddBatch('1', includeApproval: true);
            events.Add(new HarnessEventIntent(
                NewEventId(),
                HarnessEventKind.RunStateChanged,
                safeCode: "approval_resolved"));
            AddBatch('2', includeApproval: false);
            var next = new AgentRun(
                current.RunId,
                current.SessionId,
                current.WorkspaceId,
                new HarnessRunStatus(HarnessRunState.WaitingApproval),
                current.Version + 1,
                current.LastSequence + events.Count,
                current.BudgetWindow,
                current.ModelStepsInWindow,
                current.ToolCallsInWindow,
                current.Preset,
                current.PolicySnapshotVersion,
                current.PolicySnapshotSha256,
                current.CreatedAt,
                Timestamp.AddSeconds(1));
            var commit = await fixture.EventStore.CommitAsync(
                new HarnessCommit(
                    current.RunId,
                    current.Version,
                    next,
                    events,
                    payloads,
                    Array.Empty<ConversationOutboxIntent>()),
                CancellationToken.None);
            Assert.Equal(HarnessCommitOutcome.Committed, commit.Outcome);
            var rehydrator = new RetainedApprovalRehydrator(
                fixture.EventStore,
                fixture.PayloadStore,
                new FixedTime(Timestamp.AddMinutes(1)));

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                rehydrator.RehydrateAsync(current.RunId, CancellationToken.None));

            Assert.Equal("retained_approval_missing", error.Message);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Resolved_approval_without_dispatch_stays_available_for_rehydration()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var waiting = await SeedApprovalBatchAsync(fixture);
            var current = await fixture.EventStore.GetRunAsync(waiting.RunId, CancellationToken.None);
            Assert.NotNull(current);
            var next = new AgentRun(
                current!.RunId,
                current.SessionId,
                current.WorkspaceId,
                new HarnessRunStatus(HarnessRunState.Running),
                current.Version + 1,
                current.LastSequence + 1,
                current.BudgetWindow,
                current.ModelStepsInWindow,
                current.ToolCallsInWindow,
                current.Preset,
                current.PolicySnapshotVersion,
                current.PolicySnapshotSha256,
                current.CreatedAt,
                Timestamp.AddSeconds(2));
            var commit = await fixture.EventStore.CommitAsync(
                new HarnessCommit(
                    current.RunId,
                    current.Version,
                    next,
                    new[]
                    {
                        new HarnessEventIntent(
                            new EventId("evt_" + new string('e', 32)),
                            HarnessEventKind.RunStateChanged,
                            safeCode: "approval_resolved"),
                    },
                    Array.Empty<HarnessPayloadIntent>(),
                    Array.Empty<ConversationOutboxIntent>()),
                CancellationToken.None);
            Assert.Equal(HarnessCommitOutcome.Committed, commit.Outcome);

            var summaries = await ScanAsync(
                fixture,
                new[] { waiting.RunId },
                Timestamp.AddMinutes(1));

            Assert.Empty(summaries);
            var after = await fixture.EventStore.GetRunAsync(waiting.RunId, CancellationToken.None);
            Assert.Equal(HarnessRunState.Running, after!.State);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Rebuilt_coordinator_resumes_durable_batch_without_reproposal()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var run = (await fixture.SeedRunAsync(
                new HarnessRunStatus(HarnessRunState.Running))).Run;
            var model = new RestartModelRunner();
            var gateway = new RestartGateway();
            var first = NewRestartCoordinator(
                model,
                gateway,
                fixture.EventStore,
                new InMemoryRetainedApprovalStash());

            var paused = await first.RunOneStepAsync(run.RunId, CancellationToken.None);
            Assert.Equal(HarnessRunState.WaitingApproval, paused.Run.State);
            await first.ResumeAfterApprovalAsync(run.RunId, CancellationToken.None);

            var rehydrator = new RetainedApprovalRehydrator(
                fixture.EventStore,
                fixture.PayloadStore);
            var retained = await rehydrator.RehydrateAsync(run.RunId, CancellationToken.None);
            Assert.NotNull(retained);
            var secondStash = new InMemoryRetainedApprovalStash();
            var second = NewRestartCoordinator(
                model,
                gateway,
                fixture.EventStore,
                secondStash);
            second.RestoreRetainedApproval(run.RunId, retained!);
            var resumed = await second.TryResumeRetainedApprovalAsync(
                run.RunId,
                CancellationToken.None);

            Assert.NotNull(resumed);
            Assert.Null(resumed!.ErrorCode);
            Assert.Equal(3, resumed.Receipts.Count);
            Assert.Equal(6, gateway.Proposals.Count);
            Assert.Equal(3, gateway.ExecuteAttempts.Count);
            Assert.Single(model.Requests);
            var events = new List<HarnessEvent>();
            await foreach (var envelope in fixture.EventStore.ReadEventsAsync(
                run.RunId,
                0,
                CancellationToken.None))
            {
                events.Add(envelope.Event);
            }

            Assert.Equal(3, events.Count(item => item.Kind == "tool.proposed"));
            Assert.Equal(3, events.Count(item => item.Kind == "tool.execution_completed"));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Runtime_approval_race_re_reads_running_state_after_null_refresh()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var run = await SeedApprovalBatchAsync(fixture);
            var rehydrator = new RetainedApprovalRehydrator(
                fixture.EventStore,
                fixture.PayloadStore,
                new FixedTime(Timestamp.AddMinutes(1)));
            var retained = await rehydrator.RehydrateAsync(run.RunId, CancellationToken.None);
            Assert.NotNull(retained);
            var stash = new InMemoryRetainedApprovalStash(new FixedTime(Timestamp));
            var first = NewRestartCoordinator(
                new RestartModelRunner(),
                new RestartGateway(),
                fixture.EventStore,
                stash);
            first.RestoreRetainedApproval(run.RunId, retained!);
            var second = NewRestartCoordinator(
                new RestartModelRunner(),
                new RestartGateway(),
                fixture.EventStore,
                stash,
                (_, _) => first.ResumeAfterApprovalAsync(run.RunId, CancellationToken.None));

            var refresh = await second.TryRefreshRetainedApprovalAsync(
                run.RunId,
                CancellationToken.None);
            Assert.Null(refresh);

            var sessionPaths = new DataPaths(
                Path.Combine(fixture.RootPath, "settings.json"),
                Path.Combine(fixture.RootPath, "sessions"),
                Path.Combine(fixture.RootPath, "activity.log"));
            var sessions = new SessionStore(sessionPaths, TimeProvider.System);
            var dispatcher = new AgentConversationOutboxDispatcher(
                new HarnessOutboxStore(fixture.Options),
                fixture.PayloadStore,
                sessions,
                new HarnessModelContextBootstrapper(
                    sessions,
                    fixture.PayloadStore,
                    new CryptographicIdGenerator(),
                    TimeProvider.System),
                new AgentRunHistoryTransitionSink(
                    new UnusedCoordinatorFactory(),
                    new AgentRuntimeRootRegistry()),
                TimeProvider.System);
            var service = new AgentRuntimeLoopService(
                dispatcher,
                fixture.EventStore,
                new UnusedCoordinatorFactory(),
                new AgentRuntimeRootRegistry(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentRuntimeLoopService>.Instance,
                workspaceCoordinatorStore: new RuAgent.Harness.Persistence.WorkspaceCoordinatorStore(fixture.Options),
                workspaceJournalProtector: new RuAgent.Harness.Persistence.WorkspaceJournalProtector(fixture.KeyStore),
                brokerGateways: new UnavailableToolGatewayFactory(),
                retainedApprovalRehydrator: rehydrator);
            var race = await service.ReconcileApprovalRefreshRaceAsync(
                run.RunId.Value,
                run.RunId,
                second,
                rehydrator,
                CancellationToken.None);

            Assert.True(race.Continue);
            Assert.Equal(
                HarnessRunState.Running,
                (await fixture.EventStore.GetRunAsync(run.RunId, CancellationToken.None))!.State);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execution_authority_restart_rehydrates_and_re_evaluates_without_reproposal(
        bool resolveBeforeRestart)
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        var workspaceRoot = Path.Combine(fixture.RootPath, "workspace");
        var externalFile = Path.Combine(fixture.RootPath, "two.txt");
        Directory.CreateDirectory(workspaceRoot);
        await File.WriteAllTextAsync(Path.Combine(workspaceRoot, "one.txt"), "one");
        await File.WriteAllTextAsync(Path.Combine(workspaceRoot, "three.txt"), "three");
        await File.WriteAllTextAsync(externalFile, "two");
        InProcessExecutionFixture? authorityA = null;
        InProcessExecutionFixture? authorityB = null;
        try
        {
            var run = (await fixture.SeedRunAsync(
                new HarnessRunStatus(HarnessRunState.Running))).Run;
            var snapshot = PolicySnapshotCanonicalizer.Create(
                1,
                AgentPermissionPreset.Standard,
                new RuAgent.Harness.Core.Workspace.WorkspaceIdentity(
                    new WorkspaceId("ws_11111111111111111111111111111111"),
                    "volume-1",
                    "root-file-1",
                    new string('a', 64)),
                [],
                DateTimeOffset.UtcNow);
            authorityA = new InProcessExecutionFixture(workspaceRoot);
            var gatewayA = await authorityA.Factory.CreateAsync(workspaceRoot, snapshot, run.RunId, run.SessionId.Value, CancellationToken.None);
            var model = new RestartModelRunner(externalFile);
            var first = NewRestartCoordinator(
                model,
                gatewayA,
                fixture.EventStore,
                new InMemoryRetainedApprovalStash());

            var paused = await first.RunOneStepAsync(run.RunId, CancellationToken.None);
            Assert.Equal(HarnessRunState.WaitingApproval, paused.Run.State);
            var firstEvents = await ReadEventsAsync(fixture, run.RunId);
            var oldApproval = Assert.Single(
                firstEvents,
                item => item.Event.Kind == "approval.requested");
            var oldApprovalId = oldApproval.Event.ApprovalId!.Value.Value;

            if (resolveBeforeRestart)
            {
                var authorized = await authorityA.Factory.RespondApprovalAsync(
                    run.RunId.Value,
                    oldApprovalId,
                    "allow_session_directory",
                    CancellationToken.None);
                Assert.True(authorized.Authorized);
                await first.ResumeAfterApprovalAsync(run.RunId, CancellationToken.None);
            }

            // A restart replaces the whole execution authority: the new runtime
            // shares no lease, grant or approval state with the old one.
            authorityA.Dispose();
            authorityA = null;
            authorityB = new InProcessExecutionFixture(workspaceRoot);
            var gatewayB = await authorityB.Factory.CreateAsync(workspaceRoot, snapshot, run.RunId, run.SessionId.Value, CancellationToken.None);
            var second = NewRestartCoordinator(
                model,
                gatewayB,
                fixture.EventStore,
                new InMemoryRetainedApprovalStash());
            var rehydrator = new RetainedApprovalRehydrator(
                fixture.EventStore,
                fixture.PayloadStore,
                new FixedTime(DateTimeOffset.UtcNow.AddMinutes(1)));
            var retained = await rehydrator.RehydrateAsync(run.RunId, CancellationToken.None);
            Assert.NotNull(retained);
            second.RestoreRetainedApproval(run.RunId, retained!);
            var refreshed = resolveBeforeRestart
                ? await second.TryResumeRetainedApprovalAsync(run.RunId, CancellationToken.None)
                : await second.TryRefreshRetainedApprovalAsync(run.RunId, CancellationToken.None);

            Assert.NotNull(refreshed);
            Assert.Equal("approval_required", refreshed!.ErrorCode);
            Assert.Equal(HarnessRunState.WaitingApproval, refreshed.Run.State);
            var secondEvents = await ReadEventsAsync(fixture, run.RunId);
            var approvals = secondEvents
                .Where(item => item.Event.Kind == "approval.requested")
                .ToArray();
            Assert.Equal(2, approvals.Length);
            Assert.NotEqual(oldApprovalId, approvals[1].Event.ApprovalId!.Value.Value);
            Assert.Equal(3, secondEvents.Count(item => item.Event.Kind == "tool.proposed"));
            Assert.Equal(0, secondEvents.Count(item => item.Event.Kind == "tool.execution_requested"));
            Assert.Equal(0, secondEvents.Count(item => item.Event.Kind == "tool.execution_completed"));
            Assert.Single(model.Requests);

            var freshApprovalId = approvals[1].Event.ApprovalId!.Value.Value;
            var freshAuthorized = await authorityB.Factory.RespondApprovalAsync(
                run.RunId.Value,
                freshApprovalId,
                "allow_session_directory",
                CancellationToken.None);
            Assert.True(freshAuthorized.Authorized);
            await second.ResumeAfterApprovalAsync(run.RunId, CancellationToken.None);
            var completed = await second.TryResumeRetainedApprovalAsync(
                run.RunId,
                CancellationToken.None);

            Assert.NotNull(completed);
            Assert.Null(completed!.ErrorCode);
            Assert.Equal(3, completed.Receipts.Count);
            var finalEvents = await ReadEventsAsync(fixture, run.RunId);
            Assert.Equal(3, finalEvents.Count(item => item.Event.Kind == "tool.proposed"));
            Assert.Equal(2, finalEvents.Count(item => item.Event.Kind == "approval.requested"));
            Assert.Single(model.Requests);
        }
        finally
        {
            authorityB?.Dispose();
            authorityA?.Dispose();
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Crash_before_any_dispatch_fails_the_run_safely_without_effects()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var tracked = new List<RunId>();
            var run = await SeedRunningAsync(fixture, tracked);

            var summaries = await ScanAsync(fixture, tracked);

            var summary = Assert.Single(summaries);
            Assert.Equal(run.RunId, summary.RunId);
            Assert.Equal("failed_pre_dispatch", summary.Action);
            var after = await fixture.EventStore.GetRunAsync(run.RunId, CancellationToken.None);
            Assert.Equal(HarnessRunState.Failed, after!.State);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Incomplete_read_only_execution_fails_safely()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var tracked = new List<RunId>();
            var run = await SeedRunningAsync(fixture, tracked);
            await CommitRequestedAsync(fixture, run, safeCode: "read_only");

            var summaries = await ScanAsync(fixture, tracked);

            Assert.Equal("failed_read_only_incomplete", Assert.Single(summaries).Action);
            var after = await fixture.EventStore.GetRunAsync(run.RunId, CancellationToken.None);
            Assert.Equal(HarnessRunState.Failed, after!.State);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Incomplete_mutation_execution_requires_reconciliation()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var tracked = new List<RunId>();
            var run = await SeedRunningAsync(fixture, tracked);
            await CommitRequestedAsync(fixture, run, safeCode: "side_effects_possible");

            var summaries = await ScanAsync(fixture, tracked);

            var summary = Assert.Single(summaries);
            Assert.Equal(run.RunId, summary.RunId);
            Assert.Equal("reconciliation_required", summary.Action);
            var after = await fixture.EventStore.GetRunAsync(run.RunId, CancellationToken.None);
            Assert.NotNull(after);
            Assert.Equal(HarnessRunState.ReconciliationRequired, after!.State);
            Assert.Contains(
                await ReadEventsAsync(fixture, run.RunId),
                item => item.Event.Kind == "reconciliation.required");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Fully_committed_executions_leave_a_running_run_untouched()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var tracked = new List<RunId>();
            var run = await SeedRunningAsync(fixture, tracked);
            await CommitRequestedAsync(fixture, run, safeCode: "read_only");
            await CommitCompletedAsync(fixture, run, AmbiguousExecution, AmbiguousCall);

            var summaries = await ScanAsync(fixture, tracked);

            Assert.Empty(summaries);
            var after = await fixture.EventStore.GetRunAsync(run.RunId, CancellationToken.None);
            Assert.Equal(HarnessRunState.Running, after!.State);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Model_step_interrupted_by_host_stop_fails_the_run_and_closes_the_step()
    {
        // A desktop host can be closed while the provider is still generating.
        // The reply was never committed, so leaving the run Running made the next
        // start open another step on top of the orphaned one and kept the session
        // busy, queueing every new prompt behind a model that was already gone.
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var tracked = new List<RunId>();
            var run = await SeedRunningAsync(fixture, tracked);
            var step = new ModelStepId("step_" + new string('a', 32));
            await CommitEventAsync(
                fixture,
                run,
                new HarnessEventIntent(
                    NewEventId(),
                    HarnessEventKind.ModelStepStarted,
                    modelStepId: step));

            var summaries = await ScanAsync(fixture, tracked);

            var summary = Assert.Single(summaries);
            Assert.Equal(run.RunId, summary.RunId);
            Assert.Equal("failed_model_step_incomplete", summary.Action);
            var after = await fixture.EventStore.GetRunAsync(run.RunId, CancellationToken.None);
            Assert.Equal(HarnessRunState.Failed, after!.State);
            Assert.Equal("recovery_model_step_incomplete", after.TerminalErrorCode);
            var events = await ReadEventsAsync(fixture, run.RunId);
            Assert.Contains(
                events,
                item => item.Event.Kind == "model.step_completed"
                    && item.Event.ModelStepId == step
                    && item.Event.SafeCode == "interrupted");
            Assert.Contains(
                events,
                item => item.Event.Kind == "run.failed"
                    && item.Event.SafeCode == "recovery_model_step_incomplete");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Completed_model_step_with_committed_execution_stays_running()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var tracked = new List<RunId>();
            var run = await SeedRunningAsync(fixture, tracked);
            var step = new ModelStepId("step_" + new string('b', 32));
            await CommitEventAsync(
                fixture,
                run,
                new HarnessEventIntent(
                    NewEventId(),
                    HarnessEventKind.ModelStepStarted,
                    modelStepId: step));
            await CommitEventAsync(
                fixture,
                (await fixture.EventStore.GetRunAsync(run.RunId, CancellationToken.None))!,
                new HarnessEventIntent(
                    NewEventId(),
                    HarnessEventKind.ModelStepCompleted,
                    modelStepId: step));
            await CommitRequestedAsync(
                fixture,
                (await fixture.EventStore.GetRunAsync(run.RunId, CancellationToken.None))!,
                safeCode: "read_only");
            await CommitCompletedAsync(fixture, run, AmbiguousExecution, AmbiguousCall);

            var summaries = await ScanAsync(fixture, tracked);

            Assert.Empty(summaries);
            var after = await fixture.EventStore.GetRunAsync(run.RunId, CancellationToken.None);
            Assert.Equal(HarnessRunState.Running, after!.State);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Fresh_approval_wait_and_terminal_runs_are_never_touched()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
                        // The fixture seeds a fixed run id; only the first insert succeeds, so reseed
            // distinct runs through direct creation with unique identifiers.
            var tracked = new List<RunId>();
            var waiting = await CreateDistinctRunAsync(
                fixture, new HarnessRunStatus(HarnessRunState.WaitingApproval), "run_22222222222222222222222222222222");
            var completed = await CreateDistinctRunAsync(
                fixture, new HarnessRunStatus(HarnessRunState.Completed), "run_33333333333333333333333333333333");
            tracked.Add(waiting.RunId);
            tracked.Add(completed.RunId);

            var summaries = await ScanAsync(fixture, tracked);

            Assert.DoesNotContain(summaries, item => item.RunId == waiting.RunId);
            Assert.DoesNotContain(summaries, item => item.RunId == completed.RunId);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Expired_approval_wait_is_failed_so_it_cannot_keep_the_workspace_locked()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var tracked = new List<RunId>();
            var run = await CreateDistinctRunAsync(
                fixture,
                new HarnessRunStatus(HarnessRunState.WaitingApproval),
                "run_55555555555555555555555555555555");
            tracked.Add(run.RunId);

            var summaries = await ScanAsync(fixture, tracked, Timestamp.AddMinutes(31));

            var summary = Assert.Single(summaries);
            Assert.Equal(run.RunId, summary.RunId);
            Assert.Equal("failed_approval_expired", summary.Action);
            var after = await fixture.EventStore.GetRunAsync(run.RunId, CancellationToken.None);
            Assert.Equal(HarnessRunState.Failed, after!.State);
            Assert.Equal("approval_expired", after.TerminalErrorCode);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Expired_workspace_trust_run_is_cancelled_so_it_cannot_keep_the_workspace_locked()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var tracked = new List<RunId>();
            var run = await CreateDistinctRunAsync(
                fixture,
                new HarnessRunStatus(HarnessRunState.WaitingWorkspaceTrust),
                "run_44444444444444444444444444444444");
            tracked.Add(run.RunId);

            var summaries = await ScanAsync(fixture, tracked, Timestamp.AddMinutes(11));

            var summary = Assert.Single(summaries);
            Assert.Equal("cancelled_workspace_trust_expired", summary.Action);
            var after = await fixture.EventStore.GetRunAsync(run.RunId, CancellationToken.None);
            Assert.Equal(HarnessRunState.Cancelled, after!.State);
            Assert.Equal("workspace_trust_expired", after.TerminalErrorCode);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Runtime_loop_cancels_an_expired_workspace_trust_wait_without_waiting_for_restart()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var run = await CreateDistinctRunAsync(
                fixture,
                new HarnessRunStatus(HarnessRunState.WaitingWorkspaceTrust),
                "run_66666666666666666666666666666666");

            var roots = new AgentRuntimeRootRegistry();
            roots.Register(run.RunId, fixture.RootPath);
            var service = NewRuntimeLoop(fixture, roots);

            await service.ReleaseExpiredWorkspaceTrustAsync(
                run.RunId.Value,
                run,
                CancellationToken.None);

            var after = await fixture.EventStore.GetRunAsync(run.RunId, CancellationToken.None);
            Assert.Equal(HarnessRunState.Cancelled, after!.State);
            Assert.Equal("workspace_trust_expired", after.TerminalErrorCode);
            Assert.False(roots.TryGetRoot(run.RunId, out _));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public void Workspace_trust_wait_is_only_expired_past_its_own_timeout()
    {
        var now = Timestamp;
        var workspace = new RuAgent.Harness.Core.Workspace.WorkspaceIdentity(
            new WorkspaceId($"ws_{Guid.NewGuid():N}"),
            "volume",
            "root-file",
            new string('e', 64));

        AgentRun Waiting(HarnessRunState state, DateTimeOffset updatedAt) => new(
            new RunId($"run_{Guid.NewGuid():N}"),
            new SessionId(Guid.NewGuid().ToString("N")),
            workspace.WorkspaceId,
            new HarnessRunStatus(state),
            0,
            1,
            1,
            0,
            0,
            AgentPermissionPreset.Standard,
            1,
            new string('f', 64),
            now.AddMinutes(-60),
            updatedAt,
            null);

        Assert.True(HarnessRecoveryScanner.IsExpiredWorkspaceTrustWait(
            Waiting(HarnessRunState.WaitingWorkspaceTrust, now.AddMinutes(-11)),
            now));
        Assert.False(HarnessRecoveryScanner.IsExpiredWorkspaceTrustWait(
            Waiting(HarnessRunState.WaitingWorkspaceTrust, now.AddMinutes(-9)),
            now));

        // An expired approval is the loop's other expiry. Each predicate must claim only
        // its own wait, or a live trust prompt would be cancelled out from under the user.
        Assert.False(HarnessRecoveryScanner.IsExpiredWorkspaceTrustWait(
            Waiting(HarnessRunState.WaitingApproval, now.AddMinutes(-31)),
            now));
        Assert.False(HarnessRecoveryScanner.IsExpiredApprovalWait(
            Waiting(HarnessRunState.WaitingWorkspaceTrust, now.AddMinutes(-31)),
            now));
    }

    private static AgentRuntimeLoopService NewRuntimeLoop(
        BackendHarnessTestFixture fixture,
        AgentRuntimeRootRegistry roots)
    {
        var sessions = new SessionStore(
            new DataPaths(
                Path.Combine(fixture.RootPath, "settings.json"),
                Path.Combine(fixture.RootPath, "sessions"),
                Path.Combine(fixture.RootPath, "activity.log")),
            TimeProvider.System);
        var dispatcher = new AgentConversationOutboxDispatcher(
            new HarnessOutboxStore(fixture.Options),
            fixture.PayloadStore,
            sessions,
            new HarnessModelContextBootstrapper(
                sessions,
                fixture.PayloadStore,
                new CryptographicIdGenerator(),
                TimeProvider.System),
            new AgentRunHistoryTransitionSink(
                new UnusedCoordinatorFactory(),
                roots),
            TimeProvider.System);
        return new AgentRuntimeLoopService(
            dispatcher,
            fixture.EventStore,
            new UnusedCoordinatorFactory(),
            roots,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentRuntimeLoopService>.Instance,
            new RuAgent.Harness.Persistence.WorkspaceCoordinatorStore(fixture.Options),
            new RuAgent.Harness.Persistence.WorkspaceJournalProtector(fixture.KeyStore),
            new UnavailableToolGatewayFactory());
    }

    [Fact]
    public async Task Migrated_active_run_with_old_permission_profile_fails_closed()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var tracked = new List<RunId>();
            var run = await CreateDistinctRunAsync(
                fixture,
                new HarnessRunStatus(HarnessRunState.Running),
                "run_66666666666666666666666666666666");
            await SetLegacyPermissionProfileVersionAsync(fixture, run.RunId);
            run = await fixture.EventStore.GetRunAsync(run.RunId, CancellationToken.None)
                ?? throw new InvalidOperationException("run missing");
            tracked.Add(run.RunId);

            var summaries = await ScanAsync(fixture, tracked);

            var summary = Assert.Single(summaries);
            Assert.Equal(run.RunId, summary.RunId);
            Assert.Equal("failed_permission_profile_changed", summary.Action);
            var after = await fixture.EventStore.GetRunAsync(run.RunId, CancellationToken.None);
            Assert.Equal(HarnessRunState.Failed, after!.State);
            Assert.Equal("permission_profile_changed", after.TerminalErrorCode);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Recovery_never_reissues_an_old_mutation_execution_id()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var tracked = new List<RunId>();
            var run = await SeedRunningAsync(fixture, tracked);
            await CommitRequestedAsync(fixture, run, safeCode: "side_effects_possible");

            await ScanAsync(fixture, tracked);
            await ScanAsync(fixture, tracked);

            var events = await ReadEventsAsync(fixture, run.RunId);
            var requested = events.Where(item => item.Event.Kind == "tool.execution_requested").ToList();
            Assert.Single(requested);
            Assert.Equal(
                AmbiguousExecution.Value,
                Assert.Single(requested).Event.ExecutionId?.Value);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static AgentRunCoordinator NewRestartCoordinator(
        IModelStepRunner model,
        IToolGateway gateway,
        SqliteHarnessEventStore store,
        IRetainedApprovalStash stash,
        Func<RunId, CancellationToken, Task>? approvalResolutionRaceHook = null) =>
        new(
            model,
            gateway,
            store,
            new AgentContextAssembler(new RestartContextSource()),
            new CryptographicIdGenerator(),
            new SystemTimeProvider(),
            new ToolCallScheduler(),
            new TransientFrameBuffer(4096),
            promptContextProvider: new RestartPromptProvider(),
            retainedStash: stash,
            approvalResolutionRaceHook: approvalResolutionRaceHook);

    private sealed class UnusedCoordinatorFactory : IAgentRuntimeCoordinatorFactory
    {
        public Task<AgentRunCoordinator> CreateAsync(
            string workspaceRoot,
            PolicySnapshot? policySnapshot = null,
            RunId? runId = null,
            string? agentSessionId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<AgentRunCoordinator>(new NotSupportedException());

        public void DropRetainedApproval(RunId runId)
        {
        }
    }

    private sealed class RestartModelRunner : IModelStepRunner
    {
        private readonly string? _externalPath;

        public RestartModelRunner(string? externalPath = null) => _externalPath = externalPath;

        public List<ModelStepRequest> Requests { get; } = new();

        public Task<ModelStepResult> RunStepAsync(
            ModelStepRequest request,
            Func<AssistantDelta, ValueTask> publishDelta,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var step = new AssistantStep(
                null,
                new[]
                {
                    new ModelToolCall(
                        "provider_restart_1",
                        "read",
                        "1",
                        JsonDocument.Parse("{\"path\":\"one.txt\"}").RootElement.Clone()),
                    new ModelToolCall(
                        "provider_restart_2",
                        "read",
                        "1",
                        JsonSerializer.SerializeToElement(new
                        {
                            path = _externalPath ?? "two.txt",
                        }).Clone()),
                    new ModelToolCall(
                        "provider_restart_3",
                        "read",
                        "1",
                        JsonDocument.Parse("{\"path\":\"three.txt\"}").RootElement.Clone()),
                },
                AssistantFinishReason.ToolCalls,
                1);
            return Task.FromResult(new ModelStepResult(
                step,
                RuAgent.Harness.Core.Prompting.SystemPromptComposer
                    .Compose(request.PromptContext, nativeToolCalling: true)
                    .Receipt));
        }
    }

    private sealed class RestartGateway : IToolGateway
    {
        private bool _hasAsked;

        public List<ToolProposal> Proposals { get; } = new();
        public List<ExecutionId> ExecuteAttempts { get; } = new();

        public Task<ToolProposalDecision> EvaluateAsync(
            ToolProposal proposal,
            CancellationToken cancellationToken)
        {
            Proposals.Add(proposal);
            if (proposal.ModelOrder == 2 && !_hasAsked)
            {
                _hasAsked = true;
                return Task.FromResult(ToolProposalDecision.Ask(
                    ToolAccessKind.Mutation,
                    new ApprovalId("apr_" + new string('a', 32))));
            }

            return Task.FromResult(ToolProposalDecision.Allow(
                ToolAccessKind.ReadOnly,
                $"restart-{proposal.ModelOrder}"));
        }

        public Task<PreparedToolExecution> PrepareAsync(
            ApprovedToolExecution execution,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PreparedToolExecution(
                execution.ExecutionId,
                execution.Proposal.ToolCallId,
                execution.Proposal.ModelOrder,
                ToolAccessKind.ReadOnly,
                "restart-preparation",
                Array.Empty<PreparedPayload>()));

        public Task<ToolExecutionReceipt> ExecuteAsync(
            PreparedToolExecution execution,
            Func<ToolOutputChunk, ValueTask> publishDelta,
            CancellationToken cancellationToken)
        {
            ExecuteAttempts.Add(execution.ExecutionId);
            return Task.FromResult(new ToolExecutionReceipt(
                execution.ExecutionId,
                execution.ToolCallId,
                ToolExecutionOutcome.Succeeded,
                "ok",
                null,
                sideEffectsPossible: false,
                modelContent: "{\"ok\":true}"u8.ToArray()));
        }

        public Task CancelAsync(ExecutionId executionId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class RestartContextSource : IModelContextSource
    {
        public Task<ModelContextSourceSnapshot> LoadAsync(
            RunId runId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ModelContextSourceSnapshot(
                Array.Empty<ModelContextEntry>(),
                Array.Empty<ModelContextEntry>()));
    }

    private sealed class RestartPromptProvider : ISystemPromptContextProvider
    {
        public Task<SystemPromptContext> BuildAsync(
            AgentRun run,
            ModelContextSnapshot modelContext,
            CancellationToken cancellationToken) =>
            Task.FromResult(SystemPromptContext.Empty);
    }

    public enum BatchCorruption
    {
        None,
        MissingOrder,
        DuplicateOrder,
        OutOfOrder,
        MismatchedVersion,
        MismatchedProvider,
        MismatchedSafeCount,
        MissingAssistant,
        MissingApproval,
        MissingApprovalComplete,
        AllAllowComplete,
        CompletionBeforeRequest,
        OrphanExecutionStep,
        DuplicateExecutionId,
        ReconciliationOnlyExecution,
        ResolvedReconciliationHistory,
        RunLevelReconciliationHistory,
        ResolutionExecutionMismatch,
        ResolutionModelStepMismatch,
        ResolutionToolCallMismatch,
        ResolutionExecutionMissing,
        ResolutionModelStepMissing,
        ResolutionToolCallMissing,
        ExecutionIdMismatch,
        OutcomeUnknown,
        AllDenied,
        DeniedAroundAsk,
    }

    private static async Task<AgentRun> SeedApprovalBatchAsync(
        BackendHarnessTestFixture fixture,
        BatchCorruption corruption = BatchCorruption.None)
    {
        var request = await fixture.SeedRunAsync(
            new HarnessRunStatus(HarnessRunState.Running));
        var current = request.Run;
        var modelStepId = new ModelStepId("step_11111111111111111111111111111111");
        var calls = new[]
        {
            new ModelToolCall(
                "provider_1",
                "read",
                "1",
                JsonDocument.Parse("{\"path\":\"secret-argument-1\"}").RootElement.Clone()),
            new ModelToolCall(
                "provider_2",
                "read",
                "1",
                JsonDocument.Parse("{\"path\":\"secret-argument-2\"}").RootElement.Clone()),
            new ModelToolCall(
                "provider_3",
                "read",
                "1",
                JsonDocument.Parse("{\"path\":\"secret-argument-3\"}").RootElement.Clone()),
        };
        var assistantPayloadId = new PayloadId("payload_" + new string('a', 32));
        var assistantBytes = AssistantStepJsonCodec.Serialize(
            new AssistantStep(null, calls, AssistantFinishReason.ToolCalls, 1));
        var payloads = new List<HarnessPayloadIntent>
        {
            new(
                assistantPayloadId,
                HarnessPayloadKind.AssistantStep,
                HarnessPayloadAudience.AuthenticatedClient
                    | HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Json,
                assistantBytes.Length,
                assistantBytes),
        };
        var events = new List<HarnessEventIntent>();
        if (corruption is BatchCorruption.ResolvedReconciliationHistory
            or BatchCorruption.RunLevelReconciliationHistory
            or BatchCorruption.ResolutionExecutionMismatch
            or BatchCorruption.ResolutionModelStepMismatch
            or BatchCorruption.ResolutionToolCallMismatch
            or BatchCorruption.ResolutionExecutionMissing
            or BatchCorruption.ResolutionModelStepMissing
            or BatchCorruption.ResolutionToolCallMissing)
        {
            var oldStep = new ModelStepId("step_" + new string('e', 32));
            var oldCall = new ToolCallId("call_" + new string('e', 32));
            var oldExecution = new ExecutionId("exec_" + new string('e', 32));
            ModelStepId? resolutionModelStepId = oldStep;
            ToolCallId? resolutionToolCallId = oldCall;
            ExecutionId? resolutionExecutionId = oldExecution;
            if (corruption == BatchCorruption.ResolutionModelStepMismatch)
            {
                resolutionModelStepId = new ModelStepId("step_" + new string('d', 32));
            }

            if (corruption == BatchCorruption.ResolutionToolCallMismatch)
            {
                resolutionToolCallId = new ToolCallId("call_" + new string('d', 32));
            }

            if (corruption == BatchCorruption.ResolutionExecutionMismatch)
            {
                resolutionExecutionId = new ExecutionId("exec_" + new string('d', 32));
            }

            if (corruption == BatchCorruption.ResolutionModelStepMissing)
            {
                resolutionModelStepId = null;
            }

            if (corruption == BatchCorruption.ResolutionToolCallMissing)
            {
                resolutionToolCallId = null;
            }

            if (corruption == BatchCorruption.ResolutionExecutionMissing)
            {
                resolutionExecutionId = null;
            }
            var oldAssistantPayloadId = new PayloadId("payload_" + new string('e', 32));
            var oldAssistantBytes = AssistantStepJsonCodec.Serialize(
                new AssistantStep(
                    null,
                    new[]
                    {
                        new ModelToolCall(
                            "provider_history",
                            "read",
                            "1",
                            JsonDocument.Parse("{\"path\":\"history.txt\"}").RootElement.Clone()),
                    },
                    AssistantFinishReason.ToolCalls,
                    1));
            payloads.Add(new HarnessPayloadIntent(
                oldAssistantPayloadId,
                HarnessPayloadKind.AssistantStep,
                HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Json,
                oldAssistantBytes.Length,
                oldAssistantBytes));
            var oldArgumentPayloadId = new PayloadId("payload_" + new string('f', 32));
            var oldArgumentBytes = System.Text.Encoding.UTF8.GetBytes(new JsonObject
            {
                ["provider_call_id"] = "provider_history",
                ["tool_id"] = "read",
                ["tool_version"] = "1",
                ["arguments"] = JsonNode.Parse("{\"path\":\"history.txt\"}"),
            }.ToJsonString());
            payloads.Add(new HarnessPayloadIntent(
                oldArgumentPayloadId,
                HarnessPayloadKind.ToolArguments,
                HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Json,
                oldArgumentBytes.Length,
                oldArgumentBytes));
            events.Add(new HarnessEventIntent(
                NewEventId(),
                HarnessEventKind.ModelStepCompleted,
                modelStepId: oldStep,
                payloadId: oldAssistantPayloadId,
                safeCount: 1));
            events.Add(new HarnessEventIntent(
                NewEventId(),
                HarnessEventKind.ToolProposed,
                modelStepId: oldStep,
                toolCallId: oldCall,
                providerCallId: "provider_history",
                payloadId: oldArgumentPayloadId,
                safeCount: 1));
            events.Add(new HarnessEventIntent(
                NewEventId(),
                HarnessEventKind.ToolExecutionRequested,
                modelStepId: oldStep,
                toolCallId: oldCall,
                providerCallId: "provider_history",
                executionId: oldExecution,
                safeCode: "side_effects_possible"));
            if (corruption == BatchCorruption.RunLevelReconciliationHistory)
            {
                events.Add(new HarnessEventIntent(
                    NewEventId(),
                    HarnessEventKind.ToolExecutionCompleted,
                    modelStepId: oldStep,
                    toolCallId: oldCall,
                    providerCallId: "provider_history",
                    executionId: oldExecution,
                    safeCode: "ok",
                    toolOutcome: ToolExecutionOutcome.Succeeded,
                    sideEffectsPossible: false));
                events.Add(new HarnessEventIntent(
                    NewEventId(),
                    HarnessEventKind.ReconciliationResolved,
                    safeCode: "gate_cleared_by_user"));
            }
            else
            {
                events.Add(new HarnessEventIntent(
                    NewEventId(),
                    HarnessEventKind.ToolExecutionCompleted,
                    modelStepId: oldStep,
                    toolCallId: oldCall,
                    providerCallId: "provider_history",
                    executionId: oldExecution,
                    safeCode: "unknown",
                    toolOutcome: ToolExecutionOutcome.OutcomeUnknown,
                    sideEffectsPossible: true));
                events.Add(new HarnessEventIntent(
                    NewEventId(),
                    HarnessEventKind.ReconciliationRequired,
                    modelStepId: oldStep,
                    toolCallId: oldCall,
                    providerCallId: "provider_history",
                    executionId: oldExecution,
                    safeCode: "unknown"));
                events.Add(new HarnessEventIntent(
                    NewEventId(),
                    HarnessEventKind.ReconciliationResolved,
                    modelStepId: resolutionModelStepId,
                    toolCallId: resolutionToolCallId,
                    providerCallId: "provider_history",
                    executionId: resolutionExecutionId,
                    safeCode: "applied"));
            }
        }
        if (corruption != BatchCorruption.MissingAssistant)
        {
            events.Add(new HarnessEventIntent(
                new EventId("evt_" + new string('b', 32)),
                HarnessEventKind.ModelStepCompleted,
                modelStepId: modelStepId,
                payloadId: assistantPayloadId,
                safeCount: calls.Length));
        }
        var proposedToolCallIds = new[]
        {
            new ToolCallId("call_" + new string('1', 32)),
            new ToolCallId("call_" + new string('2', 32)),
            new ToolCallId("call_" + new string('3', 32)),
        };
        var orders = corruption switch
        {
            BatchCorruption.MissingOrder => new[] { 1, 3 },
            BatchCorruption.DuplicateOrder => new[] { 1, 1, 3 },
            BatchCorruption.OutOfOrder => new[] { 1, 3, 2 },
            _ => new[] { 1, 2, 3 },
        };
        for (var index = 0; index < orders.Length; index++)
        {
            var order = orders[index];
            var callIndex = order - 1;
            var payloadId = new PayloadId($"payload_{(index + 1).ToString("x32")}");
            var providerCallId = corruption == BatchCorruption.MismatchedProvider && index == 0
                ? "provider_bad"
                : calls[callIndex].ProviderCallId;
            var toolVersion = corruption == BatchCorruption.MismatchedVersion && index == 0
                ? "2"
                : calls[callIndex].ToolVersion;
            var node = new JsonObject
            {
                ["provider_call_id"] = calls[callIndex].ProviderCallId,
                ["tool_id"] = calls[callIndex].ToolId,
                ["tool_version"] = toolVersion,
                ["arguments"] = JsonNode.Parse(calls[callIndex].Arguments.GetRawText()),
            };
            var bytes = System.Text.Encoding.UTF8.GetBytes(node.ToJsonString());
            payloads.Add(new HarnessPayloadIntent(
                payloadId,
                HarnessPayloadKind.ToolArguments,
                HarnessPayloadAudience.AuthenticatedClient
                    | HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Json,
                bytes.Length,
                bytes));
            events.Add(new HarnessEventIntent(
                new EventId($"evt_{(index + 1).ToString("x32")}"),
                HarnessEventKind.ToolProposed,
                modelStepId: modelStepId,
                toolCallId: proposedToolCallIds[index],
                providerCallId: providerCallId,
                    payloadId: payloadId,
                    safeCount: corruption == BatchCorruption.MismatchedSafeCount && index == 0
                        ? 2
                        : order));
        }

        void AddExecutionPair(
            int index,
            ModelStepId step,
            ToolCallId toolCallId,
            string providerCallId,
            ExecutionId executionId,
            bool completionFirst = false,
            bool denied = false)
        {
            var request = new HarnessEventIntent(
                NewEventId(),
                HarnessEventKind.ToolExecutionRequested,
                modelStepId: step,
                toolCallId: toolCallId,
                providerCallId: providerCallId,
                executionId: executionId,
                safeCode: denied ? "policy_denied" : "ok");
            var completion = new HarnessEventIntent(
                NewEventId(),
                HarnessEventKind.ToolExecutionCompleted,
                modelStepId: step,
                toolCallId: toolCallId,
                providerCallId: providerCallId,
                executionId: executionId,
                safeCode: denied ? "policy_denied" : "ok",
                toolOutcome: denied ? ToolExecutionOutcome.Failed : ToolExecutionOutcome.Succeeded,
                sideEffectsPossible: false);
            if (completionFirst)
            {
                events.Add(completion);
                events.Add(request);
            }
            else
            {
                events.Add(request);
                events.Add(completion);
            }
        }

        if (corruption == BatchCorruption.AllDenied)
        {
            for (var index = 0; index < proposedToolCallIds.Length; index++)
            {
                AddExecutionPair(
                    index,
                    modelStepId,
                    proposedToolCallIds[index],
                    calls[index].ProviderCallId,
                    new ExecutionId($"exec_{(400 + index).ToString("x32")}"),
                    denied: true);
            }
        }

        if (corruption == BatchCorruption.DeniedAroundAsk)
        {
            foreach (var index in new[] { 0, 2 })
            {
                AddExecutionPair(
                    index,
                    modelStepId,
                    proposedToolCallIds[index],
                    calls[index].ProviderCallId,
                    new ExecutionId($"exec_{(300 + index).ToString("x32")}"),
                    denied: true);
            }
        }

        if (corruption is BatchCorruption.MissingApprovalComplete
            or BatchCorruption.AllAllowComplete
            or BatchCorruption.CompletionBeforeRequest
            or BatchCorruption.OrphanExecutionStep
            or BatchCorruption.DuplicateExecutionId
            or BatchCorruption.ReconciliationOnlyExecution)
        {
            for (var index = 0; index < proposedToolCallIds.Length; index++)
            {
                var executionId = corruption == BatchCorruption.DuplicateExecutionId && index < 2
                    ? new ExecutionId("exec_" + new string('5', 32))
                    : new ExecutionId($"exec_{(500 + index).ToString("x32")}");
                AddExecutionPair(
                    index,
                    modelStepId,
                    proposedToolCallIds[index],
                    calls[index].ProviderCallId,
                    executionId,
                    completionFirst: corruption == BatchCorruption.CompletionBeforeRequest && index == 0);
            }
        }

        if (corruption == BatchCorruption.OrphanExecutionStep)
        {
            AddExecutionPair(
                0,
                new ModelStepId("step_" + new string('9', 32)),
                new ToolCallId("call_" + new string('9', 32)),
                "provider_orphan",
                new ExecutionId("exec_" + new string('9', 32)));
        }

        if (corruption == BatchCorruption.ReconciliationOnlyExecution)
        {
            events.Add(new HarnessEventIntent(
                NewEventId(),
                HarnessEventKind.ReconciliationRequired,
                modelStepId: new ModelStepId("step_" + new string('8', 32)),
                toolCallId: new ToolCallId("call_" + new string('8', 32)),
                executionId: new ExecutionId("exec_" + new string('8', 32)),
                safeCode: "reconciliation_orphan"));
        }

        if (corruption is not BatchCorruption.MissingApproval
            and not BatchCorruption.MissingApprovalComplete
            and not BatchCorruption.AllAllowComplete
            and not BatchCorruption.CompletionBeforeRequest
            and not BatchCorruption.OrphanExecutionStep
            and not BatchCorruption.DuplicateExecutionId
            and not BatchCorruption.ReconciliationOnlyExecution
            and not BatchCorruption.AllDenied)
        {
            events.Add(new HarnessEventIntent(
                new EventId("evt_" + new string('c', 32)),
                HarnessEventKind.ApprovalRequested,
                modelStepId: modelStepId,
                toolCallId: proposedToolCallIds[1],
                providerCallId: calls[1].ProviderCallId,
                approvalId: new ApprovalId("apr_" + new string('d', 32)),
                safeCode: "approval_required"));
        }

        if (corruption is BatchCorruption.ExecutionIdMismatch or BatchCorruption.OutcomeUnknown)
        {
            events.Add(new HarnessEventIntent(
                NewEventId(),
                HarnessEventKind.RunStateChanged,
                safeCode: "approval_resolved"));
        }

        if (corruption == BatchCorruption.ExecutionIdMismatch)
        {
            events.Add(new HarnessEventIntent(
                new EventId("evt_" + new string('d', 32)),
                HarnessEventKind.ToolExecutionRequested,
                modelStepId: modelStepId,
                toolCallId: proposedToolCallIds[0],
                providerCallId: calls[0].ProviderCallId,
                executionId: new ExecutionId("exec_" + new string('1', 32))));
            events.Add(new HarnessEventIntent(
                new EventId("evt_" + new string('e', 32)),
                HarnessEventKind.ToolExecutionCompleted,
                modelStepId: modelStepId,
                toolCallId: proposedToolCallIds[0],
                providerCallId: calls[0].ProviderCallId,
                executionId: new ExecutionId("exec_" + new string('2', 32)),
                safeCode: "ok",
                toolOutcome: ToolExecutionOutcome.Succeeded,
                sideEffectsPossible: false));
        }

        if (corruption == BatchCorruption.OutcomeUnknown)
        {
            events.Add(new HarnessEventIntent(
                new EventId("evt_" + new string('f', 32)),
                HarnessEventKind.ToolExecutionRequested,
                modelStepId: modelStepId,
                toolCallId: proposedToolCallIds[1],
                providerCallId: calls[1].ProviderCallId,
                executionId: new ExecutionId("exec_" + new string('3', 32))));
            events.Add(new HarnessEventIntent(
                new EventId("evt_" + new string('0', 32)),
                HarnessEventKind.ToolExecutionCompleted,
                modelStepId: modelStepId,
                toolCallId: proposedToolCallIds[1],
                providerCallId: calls[1].ProviderCallId,
                executionId: new ExecutionId("exec_" + new string('3', 32)),
                safeCode: "unknown",
                toolOutcome: ToolExecutionOutcome.OutcomeUnknown,
                sideEffectsPossible: true));
        }

        var next = new AgentRun(
            current.RunId,
            current.SessionId,
            current.WorkspaceId,
            new HarnessRunStatus(
                corruption is BatchCorruption.CompletionBeforeRequest
                    or BatchCorruption.AllAllowComplete
                    ? HarnessRunState.Running
                    : HarnessRunState.WaitingApproval),
            current.Version + 1,
            current.LastSequence + events.Count,
            current.BudgetWindow,
            current.ModelStepsInWindow,
            current.ToolCallsInWindow,
            current.Preset,
            current.PolicySnapshotVersion,
            current.PolicySnapshotSha256,
            current.CreatedAt,
            Timestamp.AddSeconds(1));
        var result = await fixture.EventStore.CommitAsync(
            new HarnessCommit(
                current.RunId,
                current.Version,
                next,
                events,
                payloads,
                Array.Empty<ConversationOutboxIntent>()),
            CancellationToken.None);
        Assert.Equal(HarnessCommitOutcome.Committed, result.Outcome);
        return next;
    }

    // ------------------------------------------------------------------
    // Fixtures.
    // ------------------------------------------------------------------

    private static async Task<AgentRun> SeedRunningAsync(BackendHarnessTestFixture fixture, List<RunId> tracked)
    {
        var run = await CreateDistinctRunAsync(
            fixture,
            new HarnessRunStatus(HarnessRunState.Running),
            $"run_{Guid.NewGuid():N}");
        tracked.Add(run.RunId);
        return run;
    }

    private static async Task<AgentRun> CreateDistinctRunAsync(
        BackendHarnessTestFixture fixture,
        HarnessRunStatus status,
        string rawRunId)
    {
        var workspace = new RuAgent.Harness.Core.Workspace.WorkspaceIdentity(
            new WorkspaceId($"ws_{Guid.NewGuid():N}"),
            "volume-recovery",
            "root-file-recovery",
            new string('e', 64));
        await new RuAgent.Harness.Persistence.Workspace.SqliteWorkspaceStore(fixture.Options).RegisterAsync(
            new RuAgent.Harness.Persistence.Workspace.WorkspaceRegistration(
                workspace,
                new RuAgent.Harness.Persistence.Workspace.WorkspaceRootCiphertext(new byte[12], new byte[] { 1 }, new byte[16])),
            new RuAgent.Harness.Core.Workspace.WorkspaceTrustRequest(
                new TrustRequestId($"trust_{Guid.NewGuid():N}"),
                workspace,
                Timestamp,
                Timestamp.AddMinutes(10)),
            CancellationToken.None);
        var updatedAt = Timestamp;
        DateTimeOffset? terminalAt = null;
        if (status.State.IsTerminal())
        {
            updatedAt = Timestamp.AddMinutes(1);
            terminalAt = updatedAt;
        }

        var run = new AgentRun(
            new RunId(rawRunId),
            new SessionId(Guid.NewGuid().ToString("N")),
            workspace.WorkspaceId,
            status,
            0,
            1,
            1,
            0,
            0,
            AgentPermissionPreset.Standard,
            1,
            new string('f', 64),
            Timestamp,
            updatedAt,
            terminalAt);
        var request = new NewAgentRun(
            run,
            new[]
            {
                new HarnessEventIntent(
                    new EventId($"evt_{Guid.NewGuid():N}"),
                    HarnessEventKind.RunStateChanged),
            },
            Array.Empty<HarnessPayloadIntent>(),
            Array.Empty<ConversationOutboxIntent>(),
            new RunCreationIdentity(new string('7', 64), new string('8', 64), 1));
        var result = await fixture.EventStore.TryCreateRunAsync(request, CancellationToken.None);
        Assert.Equal(CreateRunOutcome.Created, result.Outcome);
        return result.Run!;
    }

    private static async Task SetLegacyPermissionProfileVersionAsync(
        BackendHarnessTestFixture fixture,
        RunId runId)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fixture.Options.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE agent_runs
            SET permission_profile_version = 0
            WHERE run_id = $run_id
            """;
        command.Parameters.AddWithValue("$run_id", runId.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task CommitEventAsync(
        BackendHarnessTestFixture fixture,
        AgentRun current,
        HarnessEventIntent intent)
    {
        var next = new AgentRun(
            current.RunId,
            current.SessionId,
            current.WorkspaceId,
            current.Status,
            current.Version + 1,
            current.LastSequence + 1,
            current.BudgetWindow,
            current.ModelStepsInWindow,
            current.ToolCallsInWindow,
            current.Preset,
            current.PolicySnapshotVersion,
            current.PolicySnapshotSha256,
            current.CreatedAt,
            Timestamp,
            terminalAt: null,
            permissionProfileVersion: current.PermissionProfileVersion);
        var result = await fixture.EventStore.CommitAsync(
            new HarnessCommit(
                current.RunId,
                current.Version,
                next,
                new[] { intent },
                Array.Empty<HarnessPayloadIntent>(),
                Array.Empty<ConversationOutboxIntent>()),
            CancellationToken.None);
        Assert.Equal(HarnessCommitOutcome.Committed, result.Outcome);
    }

    private static async Task CommitRequestedAsync(
        BackendHarnessTestFixture fixture,
        AgentRun run,
        string safeCode) =>
        await CommitEventAsync(
            fixture,
            run,
            new HarnessEventIntent(
                NewEventId(),
                HarnessEventKind.ToolExecutionRequested,
                toolCallId: AmbiguousCall,
                executionId: AmbiguousExecution,
                safeCode: safeCode));

    private static async Task CommitCompletedAsync(
        BackendHarnessTestFixture fixture,
        AgentRun run,
        ExecutionId executionId,
        ToolCallId toolCallId)
    {
        var current = await fixture.EventStore.GetRunAsync(run.RunId, CancellationToken.None)
            ?? throw new InvalidOperationException("run missing");
        await CommitEventAsync(
            fixture,
            current,
            new HarnessEventIntent(
                NewEventId(),
                HarnessEventKind.ToolExecutionCompleted,
                toolCallId: toolCallId,
                executionId: executionId,
                safeCode: "ok",
                toolOutcome: ToolExecutionOutcome.Succeeded,
                sideEffectsPossible: false));
    }

    private static async Task<IReadOnlyList<HarnessEventEnvelope>> ReadEventsAsync(
        BackendHarnessTestFixture fixture,
        RunId runId)
    {
        var events = new List<HarnessEventEnvelope>();
        await foreach (var envelope in fixture.EventStore.ReadEventsAsync(runId, 0, CancellationToken.None))
        {
            events.Add(envelope);
        }

        return events;
    }

    [Fact]
    public async Task Poisoned_run_does_not_abort_recovery_of_other_runs()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var tracked = new List<RunId>();
            var poison = await SeedRunningAsync(fixture, tracked);
            var good = await SeedRunningAsync(fixture, tracked);

            var store = new FaultingReadStore(fixture.EventStore, poison.RunId);
            var sweep = await new HarnessRecoveryScanner(
                store,
                new FixedTime(Timestamp),
                _ => Task.FromResult<IReadOnlyList<RunId>>(tracked)).SweepPendingRunsAsync(CancellationToken.None);

            var summary = Assert.Single(sweep.Recovered);
            Assert.Equal(good.RunId, summary.RunId);
            Assert.Equal("failed_pre_dispatch", summary.Action);
            Assert.Equal(1, sweep.ParkedCount);
            Assert.False(sweep.AllHandled);
            var after = await fixture.EventStore.GetRunAsync(good.RunId, CancellationToken.None);
            Assert.Equal(HarnessRunState.Failed, after!.State);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private sealed class FaultingReadStore(IHarnessEventStore inner, RunId poison) : IHarnessEventStore
    {
        public Task<CreateRunResult> TryCreateRunAsync(NewAgentRun request, CancellationToken cancellationToken) =>
            inner.TryCreateRunAsync(request, cancellationToken);

        public Task<AgentRun?> GetRunAsync(RunId runId, CancellationToken cancellationToken) =>
            inner.GetRunAsync(runId, cancellationToken);

        public Task<HarnessCommitResult> CommitAsync(HarnessCommit commit, CancellationToken cancellationToken) =>
            inner.CommitAsync(commit, cancellationToken);

        public async IAsyncEnumerable<HarnessEventEnvelope> ReadEventsAsync(
            RunId runId,
            long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (runId == poison)
            {
                throw new InvalidOperationException("poisoned event stream");
            }
            await foreach (var envelope in inner.ReadEventsAsync(runId, afterSequence, cancellationToken).ConfigureAwait(false))
            {
                yield return envelope;
            }
        }
    }

    private static async Task<IReadOnlyList<RecoveredRunSummary>> ScanAsync(
        BackendHarnessTestFixture fixture,
        IReadOnlyList<RunId> tracked,
        DateTimeOffset? now = null)
    {
        var sweep = await new HarnessRecoveryScanner(
            fixture.EventStore,
            new FixedTime(now ?? Timestamp),
            _ => Task.FromResult<IReadOnlyList<RunId>>(tracked)).SweepPendingRunsAsync(CancellationToken.None);
        return sweep.Recovered;
    }

    private static EventId NewEventId() =>
        new($"evt_{Guid.NewGuid():N}".Replace("-", "").PadRight(36)[..36]);

        private sealed class FixedTime(DateTimeOffset now) : ITimeProvider
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
