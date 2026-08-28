using System;
using System.Collections.Generic;
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
        public bool CancelCalled;
        public bool ContinueCalled;
        public bool TrustCalled;
        public bool PresetCalled;
        public bool ApproveCalled;
        public bool DenyCalled;
        public Task<AgentRunModel> CreateRunAsync(CreateAgentRunRequest r, CancellationToken ct = default)
            => Task.FromResult(Run!);

        public Task<AgentRunModel> GetRunAsync(string runId, CancellationToken ct = default)
            => Task.FromResult(Run!);

        public Task<AgentPayloadModel> GetPayloadAsync(string runId, string payloadId, CancellationToken ct = default)
            => Task.FromResult(new AgentPayloadModel(runId, payloadId, "{}"));

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
            Func<AgentRunSummaryModel, Task>? onSummary = null)
        {
            foreach (var e in Stream)
            {
                await onEvent(e).ConfigureAwait(false);
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
}
