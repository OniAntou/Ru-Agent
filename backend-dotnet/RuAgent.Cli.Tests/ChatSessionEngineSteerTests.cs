using RuAgent.Cli.Chat;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>Steering in the shared chat controller (ADR 0056).</summary>
public sealed class ChatSessionEngineSteerTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Enter_while_a_turn_runs_steers_it_instead_of_queueing()
    {
        await using var harness = await BusyHarness.StartAsync(new SteerResult(SteerAttempt.Sent));

        harness.View.InputText = "also update the changelog";
        harness.Engine.Submit();

        Assert.Equal("also update the changelog", await harness.Steered.Task.WaitAsync(Deadline));
        Assert.Equal(string.Empty, harness.View.InputText);
        Assert.Equal(0, harness.Engine.QueuedCount);
        Assert.Contains("also update the changelog", harness.Engine.History);
    }

    [Fact]
    public async Task Tab_while_a_turn_runs_queues_the_prompt_for_the_next_turn()
    {
        await using var harness = await BusyHarness.StartAsync(new SteerResult(SteerAttempt.Sent));

        harness.View.InputText = "next task";
        harness.Engine.QueueSubmit();

        Assert.False(harness.Steered.Task.IsCompleted);
        Assert.Equal(1, harness.Engine.QueuedCount);
        harness.Release();
        await harness.Engine.WaitForIdleAsync(default).WaitAsync(Deadline);
        Assert.Contains("next task", harness.Prompts);
    }

    [Theory]
    [InlineData("/status")]
    [InlineData("   ")]
    public async Task Commands_and_blank_lines_never_steer(string line)
    {
        await using var harness = await BusyHarness.StartAsync(new SteerResult(SteerAttempt.Sent));

        harness.View.InputText = line;
        harness.Engine.Submit();

        Assert.False(harness.Steered.Task.IsCompleted);
    }

    [Fact]
    public async Task A_steer_the_run_can_no_longer_take_becomes_the_next_turn()
    {
        await using var harness = await BusyHarness.StartAsync(new SteerResult(SteerAttempt.NotSteerable));

        harness.View.InputText = "one more thing";
        harness.Engine.Submit();
        await harness.Steered.Task.WaitAsync(Deadline);
        await WaitUntilAsync(() => harness.Engine.QueuedCount == 1);

        harness.Release();
        await harness.Engine.WaitForIdleAsync(default).WaitAsync(Deadline);
        Assert.Contains("one more thing", harness.Prompts);
    }

    [Fact]
    public async Task A_refused_steer_goes_back_to_the_prompt_in_front_of_new_typing()
    {
        await using var harness = await BusyHarness.StartAsync(new SteerResult(SteerAttempt.Refused, "refused by hook"));
        harness.SteerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        harness.View.InputText = "risky change";
        harness.Engine.Submit();
        await harness.Steered.Task.WaitAsync(Deadline);
        harness.View.InputText = "draft";
        harness.SteerGate.SetResult();

        await WaitUntilAsync(() => harness.View.InputText == "risky change\ndraft");
        Assert.Contains("refused by hook", harness.Errors.ToString());
        Assert.Equal(0, harness.Engine.QueuedCount);
    }

    [Fact]
    public async Task A_sent_steer_says_it_is_waiting_and_that_esc_sends_it_now()
    {
        await using var harness = await BusyHarness.StartAsync(new SteerResult(SteerAttempt.Sent));

        harness.View.InputText = "also the docs";
        harness.Engine.Submit();
        await harness.Steered.Task.WaitAsync(Deadline);

        await WaitUntilAsync(() => harness.Errors.ToString().Contains(ChatSessionEngine.SteerWaitingHint, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Messages_the_run_never_read_are_sent_as_the_next_turn_ahead_of_the_queue()
    {
        await using var harness = await BusyHarness.StartAsync(new SteerResult(SteerAttempt.Sent));
        harness.View.InputText = "queued later";
        harness.Engine.QueueSubmit();

        harness.Engine.SendReturnedSteers(["first", "second"]);
        await WaitUntilAsync(() => harness.Engine.QueuedCount == 2);
        harness.Release();
        await WaitUntilAsync(() => { lock (harness.Prompts) return harness.Prompts.Count == 3; });

        lock (harness.Prompts)
            Assert.Equal(["working", "first\n\nsecond", "queued later"], harness.Prompts);
        Assert.Equal(string.Empty, harness.View.InputText);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }

    /// <summary>An engine whose first prompt keeps the turn running until released.</summary>
    private sealed class BusyHarness : IAsyncDisposable
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private BusyHarness(StringWriter errors)
        {
            Errors = errors;
            View = new PlainChatView(errors, "workspace");
            Engine = new ChatSessionEngine(View);
        }

        public StringWriter Errors { get; }
        public PlainChatView View { get; }
        public ChatSessionEngine Engine { get; }
        public List<string> Prompts { get; } = [];
        public TaskCompletionSource<string> Steered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? SteerGate { get; set; }

        public static async Task<BusyHarness> StartAsync(SteerResult answer)
        {
            var harness = new BusyHarness(new StringWriter());
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.Engine.SetPromptHandler(async (line, ct) =>
            {
                lock (harness.Prompts) harness.Prompts.Add(line);
                if (line == "working") { entered.TrySetResult(); await harness._release.Task.WaitAsync(ct); }
            });
            harness.Engine.SetSteerHandler(async text =>
            {
                harness.Steered.TrySetResult(text);
                if (harness.SteerGate is { } gate) await gate.Task;
                return answer;
            });
            harness.View.InputText = "working";
            harness.Engine.Submit();
            await entered.Task.WaitAsync(Deadline);
            return harness;
        }

        public void Release() => _release.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            Release();
            Engine.Stop();
            await Engine.Completion.WaitAsync(Deadline);
            Errors.Dispose();
        }
    }
}
