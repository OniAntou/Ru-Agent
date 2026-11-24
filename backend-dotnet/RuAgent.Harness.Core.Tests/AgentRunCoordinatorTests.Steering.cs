using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Steering;
using RuAgent.Harness.Core.Tests.Fakes;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed partial class AgentRunCoordinatorTests
{
    private static readonly string SteerId = "steer_" + new string('5', 32);

    [Fact]
    public async Task A_final_reply_with_a_pending_steer_keeps_the_run_running_as_narration()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new(HarnessRunState.Running)));
        var steers = new FakeSteerInbox(store);
        steers.ArrivesDuringNextStep.Add(new DeliveredSteer(SteerId, "Also update the changelog"));
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("Parser fixed."));
        var sink = new RecordingActivitySink();

        var result = await Coordinator(model, new FakeToolGateway(), store, activitySink: sink, steerInbox: steers)
            .RunOneStepAsync(RunId(), default);

        Assert.Equal(HarnessRunState.Running, result.Run.State);
        Assert.Empty(store.Commits.SelectMany(commit => commit.ConversationOutbox));
        Assert.Contains(sink.Activities, item => item.Kind == AgentActivityKind.Narration && item.Detail == "Parser fixed.");
        var step = Assert.Single(store.Commits.SelectMany(commit => commit.Payloads),
            payload => payload.Kind == HarnessPayloadKind.AssistantStep);
        Assert.Equal(HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext, step.Audience);
    }

    [Fact]
    public async Task The_next_step_delivers_the_steer_before_the_model_runs_and_then_the_run_can_finish()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new(HarnessRunState.Running)));
        var steers = new FakeSteerInbox(store);
        steers.ArrivesDuringNextStep.Add(new DeliveredSteer(SteerId, "Also update the changelog"));
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("Parser fixed."));
        model.Enqueue(FinalStep("Changelog updated too."));
        var sink = new RecordingActivitySink();
        var coordinator = Coordinator(model, new FakeToolGateway(), store, activitySink: sink, steerInbox: steers);

        var first = await coordinator.RunOneStepAsync(RunId(), default);
        Assert.Equal(HarnessRunState.Running, first.Run.State);
        Assert.Equal(0, steers.Deliveries);
        var second = await coordinator.RunOneStepAsync(RunId(), default);

        Assert.Equal(1, steers.Deliveries);
        Assert.Equal(2, model.Requests.Count);
        var steer = Assert.Single(sink.Activities, item => item.Kind == AgentActivityKind.Steer);
        Assert.Equal("Also update the changelog", steer.Detail);
        var note = Assert.Single(store.Commits.SelectMany(commit => commit.Events),
            item => item.Kind == HarnessEventKind.RunNoteAdded);
        Assert.Equal(RunSteerContract.DeliveredSafeCode, note.SafeCode);
        // The steer is committed before the second model step starts, so its prompt includes it.
        var noteCommit = store.Commits.FindIndex(commit => commit.Events.Contains(note));
        var secondStepCommit = store.Commits.FindLastIndex(commit =>
            commit.Events.Any(item => item.Kind == HarnessEventKind.ModelStepStarted));
        Assert.True(noteCommit < secondStepCommit);
        Assert.Equal(HarnessRunState.WaitingHistory, second.Run.State);
    }

    [Fact]
    public async Task A_child_run_never_reads_steers()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new(HarnessRunState.Running)));
        var steers = new FakeSteerInbox(store);
        steers.ArrivesDuringNextStep.Add(new DeliveredSteer(SteerId, "ignored"));
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("done"));

        var result = await Coordinator(model, new FakeToolGateway(), store, internalRun: true, steerInbox: steers)
            .RunOneStepAsync(RunId(), default);

        Assert.Equal(HarnessRunState.Completed, result.Run.State);
        Assert.Equal(0, steers.Deliveries);
        Assert.Equal(0, steers.PendingChecks);
    }

    [Fact]
    public async Task Without_a_pending_steer_a_final_reply_ends_the_run_as_before()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new(HarnessRunState.Running)));
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("done"));

        var result = await Coordinator(model, new FakeToolGateway(), store, steerInbox: new FakeSteerInbox(store))
            .RunOneStepAsync(RunId(), default);

        Assert.Equal(HarnessRunState.WaitingHistory, result.Run.State);
        Assert.Single(store.Commits.SelectMany(commit => commit.ConversationOutbox));
    }

    /// <summary>
    /// Delivers like the SQLite store: commits the steers into the run, then forgets them.
    /// <see cref="ArrivesDuringNextStep"/> become pending once a delivery pass has run, as a
    /// steer the user sends while the model is writing its reply.
    /// </summary>
    private sealed class FakeSteerInbox(InMemoryHarnessEventStore store) : IRunSteerInbox
    {
        public List<DeliveredSteer> ArrivesDuringNextStep { get; } = [];
        private List<DeliveredSteer> Pending { get; } = [];
        public int Deliveries { get; private set; }
        public int PendingChecks { get; private set; }

        public async Task<IReadOnlyList<DeliveredSteer>> DeliverAsync(RunId runId, DateTimeOffset now, CancellationToken ct)
        {
            var run = (await store.GetRunAsync(runId, ct))!;
            if (Pending.Count == 0 || run.State != HarnessRunState.Running)
            {
                Pending.AddRange(ArrivesDuringNextStep);
                ArrivesDuringNextStep.Clear();
                return [];
            }

            var delivered = Pending.ToArray();
            await store.CommitAsync(RunSteerDelivery.CreateCommit(run, delivered, now), ct);
            Pending.Clear();
            Deliveries++;
            return delivered;
        }

        public Task<bool> HasPendingAsync(RunId runId, CancellationToken ct)
        {
            PendingChecks++;
            return Task.FromResult(Pending.Count > 0);
        }
    }
}
