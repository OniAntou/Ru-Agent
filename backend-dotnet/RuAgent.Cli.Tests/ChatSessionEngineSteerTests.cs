using RuAgent.Cli.Attachments;
using RuAgent.Cli.Chat;
using RuAgent.Cli.Images;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
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

    [Fact]
    public async Task Enter_with_a_text_file_steers_the_run_with_the_file_inlined()
    {
        await using var harness = await BusyHarness.StartAsync(new SteerResult(SteerAttempt.Sent));
        var file = new CliTextAttachment("notes.md", "# plan", 6);
        harness.AttachTexts(file);

        harness.View.InputText = "use these notes";
        harness.Engine.Submit();

        var sent = await harness.Steered.Task.WaitAsync(Deadline);
        Assert.Equal(AttachedPrompt.Compose("use these notes", [file]), sent);
        Assert.Equal(0, harness.Engine.QueuedCount);
        // The file left the pending list with the steer, so it is not sent twice.
        Assert.Single(harness.Accepted);
        Assert.Empty(harness.Returned);
    }

    [Fact]
    public async Task A_file_alone_can_steer_the_run()
    {
        await using var harness = await BusyHarness.StartAsync(new SteerResult(SteerAttempt.Sent));
        var file = new CliTextAttachment("big.cs", string.Empty, 20_000, "src/big.cs");
        harness.AttachTexts(file);

        harness.View.InputText = string.Empty;
        harness.Engine.Submit();

        Assert.Equal(AttachedPrompt.Compose(string.Empty, [file]), await harness.Steered.Task.WaitAsync(Deadline));
    }

    [Fact]
    public async Task Images_steer_the_run_with_the_text()
    {
        await using var harness = await BusyHarness.StartAsync(new SteerResult(SteerAttempt.Sent));
        var shot = new CliImageAttachment("shot.png", "image/png", "AAAA", 1, 1, 3);
        harness.AttachImages(shot);

        harness.View.InputText = "look at this";
        harness.Engine.Submit();

        Assert.Equal("look at this", await harness.Steered.Task.WaitAsync(Deadline));
        Assert.Same(shot, Assert.Single(harness.SteeredSubmission!.Images));
        Assert.Equal(0, harness.Engine.QueuedCount);
    }

    [Fact]
    public async Task An_image_alone_can_steer_the_run()
    {
        await using var harness = await BusyHarness.StartAsync(new SteerResult(SteerAttempt.Sent));
        harness.AttachImages(new CliImageAttachment("shot.png", "image/png", "AAAA", 1, 1, 3));

        harness.View.InputText = string.Empty;
        harness.Engine.Submit();

        Assert.Equal(string.Empty, await harness.Steered.Task.WaitAsync(Deadline));
    }

    [Fact]
    public async Task Files_too_long_for_a_steer_queue_as_the_next_turn()
    {
        await using var harness = await BusyHarness.StartAsync(new SteerResult(SteerAttempt.Sent));
        harness.AttachTexts(new CliTextAttachment("long.txt", new string('x', AgentSteerLimits.MaxCharacters), AgentSteerLimits.MaxCharacters));

        harness.View.InputText = "read it";
        harness.Engine.Submit();

        Assert.False(harness.Steered.Task.IsCompleted);
        Assert.Equal(1, harness.Engine.QueuedCount);
    }

    [Fact]
    public async Task A_refused_steer_gives_its_files_back()
    {
        await using var harness = await BusyHarness.StartAsync(new SteerResult(SteerAttempt.Refused, "refused by hook"));
        var file = new CliTextAttachment("notes.md", "# plan", 6);
        harness.AttachTexts(file);

        harness.View.InputText = "use these notes";
        harness.Engine.Submit();
        await harness.Steered.Task.WaitAsync(Deadline);

        await WaitUntilAsync(() => harness.Returned.Count == 1);
        Assert.Same(file, Assert.Single(harness.Returned[0].Texts));
        await WaitUntilAsync(() => harness.View.InputText == "use these notes");
    }

    [Fact]
    public async Task Returned_steers_with_files_become_one_turn_with_the_files_first()
    {
        await using var harness = await BusyHarness.StartAsync(new SteerResult(SteerAttempt.Sent));
        var withFile = AttachedPrompt.Compose("and this", [new CliTextAttachment("a.md", "# a", 3)]);

        harness.Engine.SendReturnedSteers(["first", withFile]);
        await WaitUntilAsync(() => harness.Engine.QueuedCount == 1);
        harness.Release();
        await WaitUntilAsync(() => { lock (harness.Prompts) return harness.Prompts.Count == 2; });

        string turn;
        lock (harness.Prompts) turn = harness.Prompts[1];
        var parts = Assert.IsType<AttachedPromptParts>(AttachedPromptText.TrySplit(turn));
        Assert.Equal("a.md", Assert.Single(parts.Documents).FileName);
        Assert.Equal("first\n\nand this", parts.Text);
    }

    [Fact]
    public async Task A_returned_steer_the_full_queue_refuses_goes_back_to_the_prompt()
    {
        await using var harness = await BusyHarness.StartAsync(new SteerResult(SteerAttempt.Sent));
        for (var index = 0; index < ChatSessionEngine.MaxQueueDepth; index++)
        {
            harness.View.InputText = "queued " + index;
            harness.Engine.QueueSubmit();
        }

        await WaitUntilAsync(() => harness.Engine.QueuedCount == ChatSessionEngine.MaxQueueDepth);

        harness.Engine.SendReturnedSteers(["meant for now"]);

        // The queue refuses it, but the text came back from a steer and had already left the
        // prompt: dropping it silently would lose what the user wrote.
        await WaitUntilAsync(() => harness.View.InputText == "meant for now");
        Assert.Equal(ChatSessionEngine.MaxQueueDepth, harness.Engine.QueuedCount);
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
        public ChatSubmissionSnapshot? SteeredSubmission { get; private set; }
        public List<ChatSubmissionSnapshot> Accepted { get; } = [];
        public List<ChatSubmissionSnapshot> Returned { get; } = [];
        private CliImageAttachment[] _images = [];
        private CliTextAttachment[] _texts = [];

        public void AttachTexts(params CliTextAttachment[] texts) => _texts = texts;

        public void AttachImages(params CliImageAttachment[] images) => _images = images;

        public static async Task<BusyHarness> StartAsync(SteerResult answer)
        {
            var harness = new BusyHarness(new StringWriter());
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.Engine.SetPromptHandler(async (line, ct) =>
            {
                lock (harness.Prompts) harness.Prompts.Add(line);
                if (line == "working") { entered.TrySetResult(); await harness._release.Task.WaitAsync(ct); }
            });
            harness.Engine.SetSteerHandler(async (text, submission) =>
            {
                harness.SteeredSubmission = submission;
                harness.Steered.TrySetResult(text);
                if (harness.SteerGate is { } gate) await gate.Task;
                return answer;
            });
            // Stands in for the session's pending attachments: a submit takes them, a
            // returned steer puts them back.
            harness.Engine.SetAttachmentCountProvider(() => harness._images.Length + harness._texts.Length);
            harness.Engine.SetSubmissionSnapshotFactory(line => ChatSubmissionSnapshot.Create(
                line, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, harness._images, harness._texts));
            harness.Engine.SetSubmissionOwnershipHandlers(
                snapshot =>
                {
                    harness.Accepted.Add(snapshot);
                    harness._images = [];
                    harness._texts = [];
                },
                _ => { });
            harness.Engine.SetSubmissionReturnHandler(snapshot =>
            {
                harness.Returned.Add(snapshot);
                harness._images = [.. snapshot.Images];
                harness._texts = [.. snapshot.Texts];
            });
            harness.View.InputText = "working";
            harness.Engine.Submit();
            await entered.Task.WaitAsync(Deadline);
            harness.Accepted.Clear();
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
