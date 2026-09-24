using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentRunControllerTests
{
    private const string RunId = "run_12345678901234567890123456789012";

    private static AgentRunModel MakeRun() =>
        AgentRunModel.FromCreateResponse(new CreateAgentRunResponse(
            RunId, "sess_1", "ws_1", "running", "standard", "2026", "2026"));

    private static AgentEventModel DurableEvent(long seq) =>
        new(
            EventId: "e" + seq,
            RunId: RunId,
            SessionId: "sess_1",
            EventType: "run.state_changed",
            OccurredAt: "2026",
            Status: "running",
            SafeSummary: "step " + seq,
            PayloadRef: null,
            ApprovalId: null,
            Sequence: seq,
            IsTransient: false,
            TransientSequence: null,
            TransientData: null);

    private sealed class FakeClient : IAgentRunClient
    {
        public AgentRunModel? Run;
        public List<AgentEventModel> Stream { get; } = new();
        public List<AgentActivityModel> ActivityStream { get; } = new();
        public List<AgentRunSummaryModel> SummaryStream { get; } = new();
        public List<AgentAssistantDeltaModel> AssistantDeltaStream { get; } = new();
        public List<AgentToolOutputModel> ToolOutputStream { get; } = new();
        public bool CancelCalled;
        public bool ContinueCalled;
        public bool TrustCalled;
        public List<(string RunId, TrustAgentRunRequest Request)> TrustRequests { get; } = new();
        public bool PresetCalled;
        public bool ApproveCalled;
        public bool DenyCalled;
        public bool ThrowOnGetRun;
        public bool ThrowOnSubscribe;
        public string PayloadContent { get; set; } = "{}";
        public TaskCompletionSource<bool>? SubscriptionGate;
        public Task<AgentRunModel> CreateRunAsync(CreateAgentRunRequest r, CancellationToken ct = default)
            => Task.FromResult(Run!);

        public Task<AgentRunModel> GetRunAsync(string runId, CancellationToken ct = default)
        {
            if (ThrowOnGetRun)
            {
                throw new InvalidOperationException("run_refresh_failed");
            }

            return Task.FromResult(Run!);
        }

        public Task<AgentPayloadModel> GetPayloadAsync(string runId, string payloadId, CancellationToken ct = default)
            => Task.FromResult(new AgentPayloadModel(runId, payloadId, PayloadContent));

        public Task<long> SetSessionPresetAsync(
            string sessionId,
            string preset,
            long expectedVersion,
            bool interactive,
            CancellationToken ct = default)
        {
            PresetCalled = true;
            return Task.FromResult(expectedVersion + 1);
        }
        public Task TrustAsync(string runId, TrustAgentRunRequest request, CancellationToken ct = default)
        {
            TrustCalled = true;
            TrustRequests.Add((runId, request));
            return Task.CompletedTask;
        }

        public Task ApproveAsync(
            string runId,
            string approvalId,
            string scope,
            CancellationToken ct = default)
        {
            ApproveCalled = true;
            return Task.CompletedTask;
        }

        public Task DenyAsync(string runId, string approvalId, CancellationToken ct = default)
        {
            DenyCalled = true;
            return Task.CompletedTask;
        }


        public Task CancelAsync(string runId, CancellationToken ct = default)
        {
            CancelCalled = true;
            return Task.CompletedTask;
        }

        public Task ContinueAsync(string runId, CancellationToken ct = default)
        {
            ContinueCalled = true;
            return Task.CompletedTask;
        }

        public async Task SubscribeEventsAsync(
            string runId,
            long after,
            Func<AgentEventModel, Task> onEvent,
            CancellationToken ct = default,
            Func<AgentActivityModel, Task>? onActivity = null,
            Func<AgentRunSummaryModel, Task>? onSummary = null,
            Func<AgentAssistantDeltaModel, Task>? onAssistantDelta = null,
            Func<AgentToolOutputModel, Task>? onToolOutput = null)
        {
            if (ThrowOnSubscribe)
            {
                throw new InvalidOperationException("stream_failed");
            }

            if (SubscriptionGate is not null)
            {
                await SubscriptionGate.Task.ConfigureAwait(false);
            }

            foreach (var e in Stream)
            {
                await onEvent(e).ConfigureAwait(false);
            }

            if (onAssistantDelta is not null)
            {
                foreach (var delta in AssistantDeltaStream)
                {
                    await onAssistantDelta(delta).ConfigureAwait(false);
                }
            }

            if (onToolOutput is not null)
            {
                foreach (var output in ToolOutputStream)
                {
                    await onToolOutput(output).ConfigureAwait(false);
                }
            }

            if (onActivity is not null)
            {
                foreach (var activity in ActivityStream)
                {
                    await onActivity(activity).ConfigureAwait(false);
                }
            }

            if (onSummary is not null)
            {
                foreach (var summary in SummaryStream)
                {
                    await onSummary(summary).ConfigureAwait(false);
                }
            }

        }
    }

    [Fact]
    public async Task CreateAndRun_delivers_activity_separately_from_durable_events()
    {
        var fake = new FakeClient { Run = MakeRun() };
        fake.Stream.Add(DurableEvent(1));
        fake.ActivityStream.Add(new AgentActivityModel(
            "act_11111111111111111111111111111111",
            AgentActivityKind.Analyze,
            AgentActivityState.Completed,
            "ChatView.xaml.cs",
            150,
            200,
            null,
            null,
            null,
            null,
            5000,
            "succeeded",
            ToolDisplay: null,
            null,
            1));
        var received = new List<AgentActivityModel>();
        var controller = new AgentRunController(fake);
        controller.ActivityReceived += (_, activity) => received.Add(activity);

        await controller.CreateAndRunAsync(new CreateAgentRunRequest("sess_1", "C:\\work", "do it", "standard", null, "idem-activity"));

        var activity = Assert.Single(received);
        Assert.Equal("act_11111111111111111111111111111111", activity.ActivityId);
        Assert.Single(controller.Events);
        Assert.DoesNotContain(controller.Events, item => item.IsTransient);
    }

    [Fact]
    public async Task CreateAndRun_delivers_terminal_summary_separately_from_durable_events()
    {
        var fake = new FakeClient { Run = MakeRun() };
        fake.SummaryStream.Add(new AgentRunSummaryModel(
            "succeeded",
            "verified",
            "Test Agent",
            5,
            1,
            0,
            0,
            null,
            2));
        var received = new List<AgentRunSummaryModel>();
        var controller = new AgentRunController(fake);
        controller.SummaryReceived += (_, summary) => received.Add(summary);

        await controller.CreateAndRunAsync(new CreateAgentRunRequest("sess_1", "C:\\work", "do it", "standard", null, "idem-summary"));

        var summary = Assert.Single(received);
        Assert.Equal("verified", summary.VerificationState);
        Assert.Empty(controller.Events);
    }

    [Fact]
    public async Task CreateAndRun_keeps_the_run_cancellable_when_the_event_stream_fails()
    {
        var fake = new FakeClient { Run = MakeRun(), ThrowOnSubscribe = true };
        var controller = new AgentRunController(fake);

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.CreateAndRunAsync(
            new CreateAgentRunRequest("sess_1", "C:\\work", "do it", "standard", null, "idem-stream-failure")));

        // The host may still own the run, so Stop must stay available; the status
        // copy tells the user how to recover instead of leaving a silent dead end.
        Assert.True(controller.CanCancel);
        Assert.False(controller.IsBusy);
        Assert.NotNull(controller.CurrentRun);
        Assert.Contains("Stop", controller.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAndRun_delivers_typed_assistant_deltas_once_per_transient_frame()
    {
        var fake = new FakeClient { Run = MakeRun() };
        var delta = new AgentAssistantDeltaModel(
            "Xin chào",
            "step_0123456789abcdef0123456789abcdef",
            null,
            7);
        fake.AssistantDeltaStream.Add(delta);
        fake.AssistantDeltaStream.Add(delta);
        var received = new List<AgentAssistantDeltaModel>();
        var dispatches = 0;
        var controller = new AgentRunController(
            fake,
            synchronize: action =>
            {
                dispatches++;
                action();
            });
        controller.AssistantDeltaReceived += (_, item) => received.Add(item);

        await controller.CreateAndRunAsync(new CreateAgentRunRequest(
            "sess_1",
            "C:\\work",
            "do it",
            "standard",
            null,
            "idem-delta"));

        var item = Assert.Single(received);
        Assert.Equal("Xin chào", item.Text);
        Assert.Equal(7, item.TransientSequence);
        Assert.True(dispatches > 0);
    }

    [Fact]
    public async Task CreateAndRun_delivers_tool_output_once_per_transient_frame()
    {
        var fake = new FakeClient { Run = MakeRun() };
        var output = new AgentToolOutputModel(
            "total 3",
            "exec_abcdef0123456789abcdef0123456789",
            null,
            4);
        fake.ToolOutputStream.Add(output);
        fake.ToolOutputStream.Add(output);
        var received = new List<AgentToolOutputModel>();
        var controller = new AgentRunController(fake);
        controller.ToolOutputReceived += (_, item) => received.Add(item);

        await controller.CreateAndRunAsync(new CreateAgentRunRequest(
            "sess_1",
            "C:\\work",
            "do it",
            "standard",
            null,
            "idem-tool-output"));

        var item = Assert.Single(received);
        Assert.Equal("total 3", item.Text);
        Assert.Equal("exec_abcdef0123456789abcdef0123456789", item.ExecutionId);
    }

    [Fact]
    public async Task CreateAndRun_fetches_and_delivers_provider_usage_payload()
    {
        var fake = new FakeClient
        {
            Run = MakeRun(),
            PayloadContent = "{\"input_tokens\":0,\"output_tokens\":12,\"total_tokens\":12}",
        };
        fake.Stream.Add(new AgentEventModel(
            "usage-event",
            RunId,
            "sess_1",
            "model.usage_recorded",
            "2026",
            "running",
            "reported",
            "payload_usage",
            null,
            1,
            false,
            null,
            null));
        var received = new List<AgentUsageReceivedEventArgs>();
        var controller = new AgentRunController(fake);
        controller.UsageReceived += (_, item) => received.Add(item);

        await controller.CreateAndRunAsync(new CreateAgentRunRequest(
            "sess_1", "C:\\work", "do it", "standard", null, "idem-usage"));

        var usage = Assert.Single(received);
        Assert.Equal(RunId, usage.RunId);
        Assert.Equal(0, usage.Usage.InputTokens);
        Assert.Equal("0 input · 12 output · 12 total", usage.Usage.DisplayText);
    }

    [Fact]
    public async Task CreateAndRun_ignores_assistant_delta_after_cancellation()
    {
        var fake = new FakeClient
        {
            Run = MakeRun(),
            SubscriptionGate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously),
        };
        fake.AssistantDeltaStream.Add(new AgentAssistantDeltaModel(
            "late",
            "step_0123456789abcdef0123456789abcdef",
            null,
            1));
        var received = new List<AgentAssistantDeltaModel>();
        var controller = new AgentRunController(fake);
        controller.AssistantDeltaReceived += (_, item) => received.Add(item);
        var runTask = controller.CreateAndRunAsync(new CreateAgentRunRequest(
            "sess_1",
            "C:\\work",
            "do it",
            "standard",
            null,
            "idem-late-delta"));

        for (var attempt = 0; attempt < 100 && controller.CurrentRun is null; attempt++)
        {
            await Task.Delay(1);
        }

        Assert.NotNull(controller.CurrentRun);
        var cancelTask = controller.CancelAsync();
        fake.SubscriptionGate.SetResult(true);
        await cancelTask;
        await runTask;

        Assert.Empty(received);
    }

    [Fact]
    public async Task CreateAndRun_subscribes_and_collects_events()
    {
        var fake = new FakeClient { Run = MakeRun() };
        fake.Stream.Add(DurableEvent(1));
        fake.Stream.Add(DurableEvent(2));
        var controller = new AgentRunController(fake);

        await controller.CreateAndRunAsync(new CreateAgentRunRequest("sess_1", "C:\\work", "do it", "standard", null, "idem-1"));

        Assert.Equal(RunId, controller.CurrentRun?.RunId);
        Assert.Equal(2, controller.Events.Count);
        Assert.Equal(2, controller.Events[^1].Sequence);
        Assert.False(controller.IsBusy);
        Assert.True(controller.CanCancel);
    }

    [Fact]
    public async Task Cancel_stops_subscription_and_calls_client()
    {
        var fake = new FakeClient { Run = MakeRun() };
        var controller = new AgentRunController(fake);
        await controller.CreateAndRunAsync(new CreateAgentRunRequest("sess_1", "C:\\work", "do it", "standard", null, "idem-1"));

        await controller.CancelAsync();

        Assert.True(fake.CancelCalled);
        Assert.False(controller.CanCancel);
        Assert.Null(controller.CurrentRun);
        Assert.False(controller.IsBusy);
    }

    [Fact]
    public async Task Continue_calls_client()
    {
        var fake = new FakeClient { Run = MakeRun() };
        var controller = new AgentRunController(fake);
        await controller.CreateAndRunAsync(new CreateAgentRunRequest("sess_1", "C:\\work", "do it", "standard", null, "idem-1"));

        await controller.ContinueAsync();

        Assert.True(fake.ContinueCalled);
    }

    [Fact]
    public async Task TrustAsync_approve_sends_the_pending_request_once()
    {
        var fake = new FakeClient { Run = MakeWorkspaceTrustRun() };
        var controller = new AgentRunController(fake);

        await controller.CreateAndRunAsync(new CreateAgentRunRequest(
            "sess_1", "C:\\work", "do it", "standard", null, "idem-trust-approve"));
        await controller.TrustAsync(approve: true);

        var call = Assert.Single(fake.TrustRequests);
        Assert.Equal(RunId, call.RunId);
        Assert.Equal("trust_12345678901234567890123456789012", call.Request.TrustRequestId);
        Assert.Equal(new string('a', 64), call.Request.DisplayIdentityHash);
        Assert.Equal("approve", call.Request.Decision);
    }

    [Fact]
    public async Task TrustAsync_deny_sends_the_pending_request_once()
    {
        var fake = new FakeClient { Run = MakeWorkspaceTrustRun() };
        var controller = new AgentRunController(fake);

        await controller.CreateAndRunAsync(new CreateAgentRunRequest(
            "sess_1", "C:\\work", "do it", "standard", null, "idem-trust-deny"));
        await controller.TrustAsync(approve: false);

        var call = Assert.Single(fake.TrustRequests);
        Assert.Equal(RunId, call.RunId);
        Assert.Equal("deny", call.Request.Decision);
    }

    [Fact]
    public async Task TrustAsync_rejects_a_run_without_a_complete_request()
    {
        var fake = new FakeClient
        {
            Run = AgentRunModel.FromCreateResponse(new CreateAgentRunResponse(
                RunId,
                "sess_1",
                "ws_1",
                "waiting_workspace_trust",
                "standard",
                "2026",
                "2026")),
        };
        var controller = new AgentRunController(fake);

        await controller.CreateAndRunAsync(new CreateAgentRunRequest(
            "sess_1", "C:\\work", "do it", "standard", null, "idem-trust-invalid"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.TrustAsync(approve: true));
        Assert.Empty(fake.TrustRequests);
    }

    [Fact]
    public async Task CreateAndRun_refreshes_terminal_state_and_disables_cancel()
    {
        var fake = new FakeClient
        {
            Run = AgentRunModel.FromCreateResponse(new CreateAgentRunResponse(
                RunId,
                "sess_1",
                "ws_1",
                "completed",
                "standard",
                "2026",
                "2026")),
        };
        var controller = new AgentRunController(fake);

        await controller.CreateAndRunAsync(new CreateAgentRunRequest("sess_1", "C:\\work", "do it", "standard", null, "idem-1"));

        Assert.Equal("completed", controller.CurrentRun?.Status);
        Assert.False(controller.CanCancel);
    }

    [Fact]
    public async Task CreateAndRun_preserves_safe_failure_code_after_terminal_refresh()
    {
        var fake = new FakeClient
        {
            Run = AgentRunModel.FromCreateResponse(new CreateAgentRunResponse(
                RunId,
                "sess_1",
                "ws_1",
                "failed",
                "standard",
                "2026",
                "2026")),
        };
        fake.Stream.Add(new AgentEventModel(
            EventId: "e-failed",
            RunId: RunId,
            SessionId: "sess_1",
            EventType: "run.failed",
            OccurredAt: "2026",
            Status: "failed",
            SafeSummary: "provider_rate_limited",
            PayloadRef: null,
            ApprovalId: null,
            Sequence: 1,
            IsTransient: false,
            TransientSequence: null,
            TransientData: null));
        var controller = new AgentRunController(fake);

        await controller.CreateAndRunAsync(new CreateAgentRunRequest(
            "sess_1", "C:\\work", "do it", "standard", null, "idem-failed"));

        Assert.Equal("failed", controller.CurrentRun?.Status);
        Assert.Equal("provider_rate_limited", controller.StatusText);
        Assert.False(controller.IsBusy);
        Assert.False(controller.CanCancel);
    }

    [Fact]
    public async Task CreateAndRun_uses_terminal_sse_snapshot_when_refresh_fails()
    {
        var fake = new FakeClient
        {
            Run = MakeRun(),
            ThrowOnGetRun = true,
        };
        fake.Stream.Add(new AgentEventModel(
            EventId: string.Empty,
            RunId: string.Empty,
            SessionId: string.Empty,
            EventType: "final_snapshot",
            OccurredAt: string.Empty,
            Status: "completed",
            SafeSummary: "completed",
            PayloadRef: null,
            ApprovalId: null,
            Sequence: null,
            IsTransient: false,
            TransientSequence: null,
            TransientData: null));
        var controller = new AgentRunController(fake);

        await controller.CreateAndRunAsync(new CreateAgentRunRequest(
            "sess_1", "C:\\work", "do it", "standard", null, "idem-snapshot-fallback"));

        Assert.Equal("completed", controller.CurrentRun?.Status);
        Assert.False(controller.IsBusy);
        Assert.False(controller.CanCancel);
    }

    [Fact]
    public void Durable_approval_event_parses_the_approval_id_from_the_envelope()
    {
        const string approvalId = "apr_cccccccccccccccccccccccccccccccc";
        var envelope = new HarnessSafeEventEnvelope(
            ProtocolVersion: HarnessSafeEventEnvelope.CurrentProtocolVersion,
            EventId: "evt_approval",
            RunId: RunId,
            SessionId: "sess_1",
            Sequence: 4,
            EventType: "approval.requested",
            OccurredAt: "2026",
            Status: "waiting_approval",
            SafeSummary: "external_directory_required",
            PayloadRef: null,
            ApprovalId: approvalId);
        var frame = new SseFrame(4, "message", JsonSerializer.Serialize(envelope), IsTransient: false, TransientSequence: null);

        var model = AgentEventModel.FromSseFrame(frame);

        Assert.Equal("approval.requested", model.EventType);
        Assert.Equal(approvalId, model.ApprovalId);
        Assert.False(model.IsTransient);
    }

    [Fact]
    public async Task CreateAndRun_tracks_a_pending_approval_while_the_run_waits()
    {
        const string approvalId = "apr_cccccccccccccccccccccccccccccccc";
        var fake = new FakeClient { Run = WaitingApprovalRun() };
        fake.Stream.Add(new AgentEventModel(
            EventId: "e-approval",
            RunId: RunId,
            SessionId: "sess_1",
            EventType: "approval.requested",
            OccurredAt: "2026",
            Status: "waiting_approval",
            SafeSummary: "external_directory_required",
            PayloadRef: null,
            ApprovalId: approvalId,
            Sequence: 3,
            IsTransient: false,
            TransientSequence: null,
            TransientData: null));
        var controller = new AgentRunController(fake);

        await controller.CreateAndRunAsync(new CreateAgentRunRequest(
            "sess_1", "C:\\work", "do it", "standard", null, "idem-approval"));

        Assert.True(controller.HasPendingApproval);
        Assert.Equal(approvalId, controller.PendingApprovalId);
        Assert.Equal("external_directory_required", controller.PendingApprovalSummary);
    }

    [Fact]
    public async Task CreateAndRun_keeps_a_raw_approval_code_off_the_status_line()
    {
        // The approval panel already speaks for the request: pouring its machine
        // code into the status line only prints one more raw token row.
        const string approvalId = "apr_dddddddddddddddddddddddddddddddd";
        var fake = new FakeClient { Run = WaitingApprovalRun() };
        fake.Stream.Add(new AgentEventModel(
            EventId: "e-approval",
            RunId: RunId,
            SessionId: "sess_1",
            EventType: "approval.requested",
            OccurredAt: "2026",
            Status: "waiting_approval",
            SafeSummary: "terminal_unknown_executable",
            PayloadRef: null,
            ApprovalId: approvalId,
            Sequence: 3,
            IsTransient: false,
            TransientSequence: null,
            TransientData: null));
        var controller = new AgentRunController(fake);
        var seenStatuses = new List<string?>();
        controller.PropertyChanged += (_, e) =>
        {
            if (string.Equals(e.PropertyName, nameof(AgentRunController.StatusText), StringComparison.Ordinal))
            {
                seenStatuses.Add(controller.StatusText);
            }
        };

        await controller.CreateAndRunAsync(new CreateAgentRunRequest(
            "sess_1", "C:\\work", "do it", "standard", null, "idem-approval-code"));

        Assert.True(controller.HasPendingApproval);
        Assert.DoesNotContain(
            seenStatuses,
            status => status is not null
                && status.Contains("terminal_unknown_executable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateAndRun_clears_a_pending_approval_when_the_run_reports_it_resolved()
    {
        const string approvalId = "apr_cccccccccccccccccccccccccccccccc";
        var fake = new FakeClient { Run = WaitingApprovalRun() };
        fake.Stream.Add(new AgentEventModel(
            EventId: "e-approval",
            RunId: RunId,
            SessionId: "sess_1",
            EventType: "approval.requested",
            OccurredAt: "2026",
            Status: "waiting_approval",
            SafeSummary: "external_directory_required",
            PayloadRef: null,
            ApprovalId: approvalId,
            Sequence: 3,
            IsTransient: false,
            TransientSequence: null,
            TransientData: null));
        fake.Stream.Add(new AgentEventModel(
            EventId: "e-resolved",
            RunId: RunId,
            SessionId: "sess_1",
            EventType: "run.state_changed",
            OccurredAt: "2026",
            Status: "running",
            SafeSummary: "approval_resolved",
            PayloadRef: null,
            ApprovalId: null,
            Sequence: 4,
            IsTransient: false,
            TransientSequence: null,
            TransientData: null));
        var controller = new AgentRunController(fake);

        await controller.CreateAndRunAsync(new CreateAgentRunRequest(
            "sess_1", "C:\\work", "do it", "standard", null, "idem-approval-resolved"));

        Assert.False(controller.HasPendingApproval);
        Assert.Null(controller.PendingApprovalSummary);
    }

    [Fact]
    public async Task CreateAndRun_tracks_terminal_approval_command_from_activity_stream()
    {
        const string approvalId = "apr_cccccccccccccccccccccccccccccccc";
        var fake = new FakeClient { Run = WaitingApprovalRun() };
        fake.Stream.Add(new AgentEventModel(
            EventId: "e-approval",
            RunId: RunId,
            SessionId: "sess_1",
            EventType: "approval.requested",
            OccurredAt: "2026",
            Status: "waiting_approval",
            SafeSummary: "terminal_delete_requires_approval",
            PayloadRef: null,
            ApprovalId: approvalId,
            Sequence: 3,
            IsTransient: false,
            TransientSequence: null,
            TransientData: null));
        var activity = new AgentActivityModel(
            ActivityId: "act_1",
            Kind: AgentActivityKind.Terminal,
            State: AgentActivityState.Waiting,
            FileName: null,
            LineStart: null,
            LineEnd: null,
            Symbol: null,
            Count: null,
            Added: null,
            Removed: null,
            DurationMs: null,
            SafeCode: "terminal_delete_requires_approval",
            ToolDisplay: "Terminal",
            DurableSequence: null,
            TransientSequence: 1,
            Detail: "rm -rf build/");
        fake.ActivityStream.Add(activity);
        var controller = new AgentRunController(fake);

        await controller.CreateAndRunAsync(new CreateAgentRunRequest(
            "sess_1", "C:\\work", "do it", "standard", null, "idem-approval-command"));

        Assert.True(controller.HasPendingApproval);
        Assert.Equal("rm -rf build/", controller.PendingApprovalCommand);
    }

    [Fact]
    public async Task Continue_stays_available_when_the_run_parks_for_the_repetition_guard()
    {
        var fake = new FakeClient
        {
            Run = new AgentRunModel(
                RunId,
                "sess_1",
                "ws_1",
                "waiting_user",
                "waiting_user",
                "standard",
                2026,
                "2026",
                1,
                5,
                "2026",
                "2026",
                null,
                null,
                null,
                null),
        };
        var controller = new AgentRunController(fake);

        await controller.CreateAndRunAsync(new CreateAgentRunRequest(
            "sess_1", "C:\\work", "do it", "standard", null, "idem-waiting-user"));

        Assert.True(controller.CanContinue);
        Assert.Contains("Waiting to continue", controller.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Continue_stays_available_when_the_run_parks_for_reconciliation()
    {
        // A run parked on an ambiguous tool outcome stops being driven by the host.
        // The only way out is a reconciliation decision, and Continue is the only
        // affordance the desktop client has, so it must stay enabled here instead
        // of leaving the user with a dead Stop-only panel.
        var fake = new FakeClient
        {
            Run = new AgentRunModel(
                RunId,
                "sess_1",
                "ws_1",
                "reconciliation_required",
                null,
                "standard",
                2026,
                "2026",
                1,
                5,
                "2026",
                "2026",
                null,
                null,
                null,
                null),
        };
        fake.Stream.Add(new AgentEventModel(
            EventId: "e1",
            RunId: RunId,
            SessionId: "sess_1",
            EventType: "run.state_changed",
            OccurredAt: "2026",
            Status: "reconciliation_required",
            SafeSummary: "reconciliation_required",
            PayloadRef: null,
            ApprovalId: null,
            Sequence: 1,
            IsTransient: false,
            TransientSequence: null,
            TransientData: null));
        var controller = new AgentRunController(fake);

        await controller.CreateAndRunAsync(new CreateAgentRunRequest(
            "sess_1", "C:\\work", "do it", "standard", null, "idem-reconciliation"));

        Assert.True(controller.CanContinue);
    }

    private static AgentRunModel MakeWorkspaceTrustRun() =>
        AgentRunModel.FromCreateResponse(new CreateAgentRunResponse(
            RunId,
            "sess_1",
            "ws_1",
            "waiting_workspace_trust",
            "standard",
            "2026",
            "2026",
            TrustRequestId: "trust_12345678901234567890123456789012",
            DisplayIdentityHash: new string('a', 64)));

    private static AgentRunModel WaitingApprovalRun() =>
        AgentRunModel.FromCreateResponse(new CreateAgentRunResponse(
            RunId, "sess_1", "ws_1", "waiting_approval", "standard", "2026", "2026"));
}
