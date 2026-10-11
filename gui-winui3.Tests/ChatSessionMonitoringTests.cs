using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ChatSessionMonitoringTests
{
    private static AgentRunModel Run(string status = "running") => AgentRunModel.FromCreateResponse(
        new CreateAgentRunResponse("run_12345678901234567890123456789012", "one", "ws_1", status,
            "standard", "2026", "2026"));

    [Fact]
    public async Task Monitor_refreshes_then_completes_the_terminal_run_once()
    {
        var client = new MonitorClient { Read = _ => Task.FromResult(Run("completed")) };
        using var controller = new AgentRunController(client);
        Assert.True(await controller.AdoptRunAsync(Run()));
        using var session = new ChatSessionPresentation(_ => Task.CompletedTask);
        session.Activate("one");
        int changed = 0, completed = 0;

        await session.StartMonitoring(controller,
            token => { Assert.False(token.IsCancellationRequested); completed++; return Task.CompletedTask; },
            () => changed++, _ => Assert.Fail("Unexpected refresh failure"));

        Assert.Equal(1, client.ReadCount);
        Assert.Equal(1, changed);
        Assert.Equal(1, completed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Late_refresh_cannot_update_complete_or_report_into_another_session_or_turn(bool fail, bool newTurn)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new MonitorClient
        {
            Read = async _ =>
            {
                entered.SetResult();
                await finish.Task; // Deliberately ignores cancellation, as a late response can.
                if (fail) throw new InvalidOperationException("old refresh failed");
                return Run("completed");
            },
        };
        using var controller = new AgentRunController(client);
        await controller.AdoptRunAsync(Run());
        using var session = new ChatSessionPresentation(_ => Task.CompletedTask);
        session.Activate("one");
        int changed = 0, completed = 0, errors = 0;
        var monitor = session.StartMonitoring(controller,
            _ => { completed++; return Task.CompletedTask; }, () => changed++, _ => errors++);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (newTurn) session.BeginTurn();
        else session.Activate("two");
        finish.SetResult();
        await monitor.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, changed);
        Assert.Equal(0, completed);
        Assert.Equal(0, errors);
    }

    [Fact]
    public async Task Replacing_a_monitor_cancels_the_old_wait_without_stopping_the_new_monitor()
    {
        int waits = 0;
        using var session = new ChatSessionPresentation(token => ++waits == 1
            ? Task.Delay(Timeout.InfiniteTimeSpan, token) : Task.CompletedTask);
        session.Activate("one");
        using var controller = new AgentRunController(new MonitorClient());
        await controller.AdoptRunAsync(Run("completed"));
        int completed = 0;
        Task Complete(CancellationToken _) { completed++; return Task.CompletedTask; }
        var old = session.StartMonitoring(controller, Complete, () => { }, _ => Assert.Fail("Old monitor failed"));
        await session.StartMonitoring(controller, Complete, () => { }, _ => Assert.Fail("New monitor failed"));
        await old.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, completed);
    }

    [Fact]
    public async Task A_current_refresh_failure_is_reported_once()
    {
        var expected = new InvalidOperationException("unavailable");
        using var controller = new AgentRunController(new MonitorClient
        {
            Read = _ => Task.FromException<AgentRunModel>(expected),
        });
        await controller.AdoptRunAsync(Run());
        using var session = new ChatSessionPresentation(_ => Task.CompletedTask);
        session.Activate("one");
        var failures = new List<Exception>();
        await session.StartMonitoring(controller, _ => { Assert.Fail("Run is not complete"); return Task.CompletedTask; },
            () => Assert.Fail("Refresh failed"), failures.Add);
        Assert.Same(expected, Assert.Single(failures));
    }

    // Only the existing HTTP/SSE interface is substituted. The real run controller
    // participates in monitor tests, including its run ownership guards.
    private sealed class MonitorClient : IAgentRunClient
    {
        internal Func<CancellationToken, Task<AgentRunModel>> Read { get; init; } = _ => Task.FromResult(Run());
        internal int ReadCount { get; private set; }
        public Task<AgentRunModel> GetRunAsync(string runId, CancellationToken cancellationToken = default)
        { ReadCount++; return Read(cancellationToken); }
        public Task SubscribeEventsAsync(string runId, long afterSequence, Func<AgentEventModel, Task> onEvent,
            CancellationToken cancellationToken = default, Func<AgentActivityModel, Task>? onActivity = null,
            Func<AgentRunSummaryModel, Task>? onSummary = null, Func<AgentAssistantDeltaModel, Task>? onAssistantDelta = null,
            Func<AgentToolOutputModel, Task>? onToolOutput = null) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        public Task<AgentRunModel> CreateRunAsync(CreateAgentRunRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentPayloadModel> GetPayloadAsync(string runId, string payloadId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task TrustAsync(string runId, TrustAgentRunRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> SetSessionPresetAsync(string sessionId, string preset, long expectedVersion, bool interactive, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CancelAsync(string runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ContinueAsync(string runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DispatchAgentCommandResponse> DispatchCommandAsync(string runId, string text, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ApproveAsync(string runId, string approvalId, string scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DenyAsync(string runId, string approvalId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SubmitInputAsync(string runId, string inputId, string answer, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
