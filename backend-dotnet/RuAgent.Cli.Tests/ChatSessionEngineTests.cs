using RuAgent.Cli.Chat;
using RuAgent.Cli.TerminalGui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ChatSessionEngineTests
{
    [Fact]
    public async Task Pending_task_inspection_blocks_prompt_admission_until_cancelled()
    {
        using var errors = new StringWriter();
        var view = new PlainChatView(errors, "workspace"); var engine = new ChatSessionEngine(view);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false; var snapshots = 0;
        engine.SetTaskCommandHandler(async (_, ct) =>
        {
            entered.SetResult();
            try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { cancelled = true; }
        });
        engine.SetSubmissionSnapshotFactory(line =>
        {
            snapshots++;
            return ChatSubmissionSnapshot.Create(line, "s", "w", "standard", "b", "m", [], []);
        });
        view.InputText = "/tasks"; engine.Submit();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        view.InputText = "unsent prompt"; engine.Submit();
        Assert.Equal("unsent prompt", view.InputText); Assert.Equal(0, snapshots);
        Assert.Contains("finish the current inspection", errors.ToString());
        engine.Cancel();
        await engine.WaitForIdleAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(cancelled);
        engine.Submit();
        await engine.WaitForIdleAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, snapshots);
        engine.Stop(); await engine.Completion;
    }

    [Fact]
    public void NativeBindingUsesTheSameControllerAsLineInput() =>
        Assert.Equal(typeof(ChatSessionEngine), typeof(TgChatLoop).BaseType);

    [Fact]
    public async Task LineAdapterPreservesQuotesAndLeavesInputAfterExitUnread()
    {
        var view = new PlainChatView(TextWriter.Null, "workspace");
        var engine = new ChatSessionEngine(view);
        var received = new List<string>();
        engine.SetPromptHandler((line, _) =>
        {
            received.Add(line);
            if (line == "/exit") engine.Stop();
            return Task.CompletedTask;
        });
        using var input = new StringReader("\"quoted prompt\"\n/exit\nnot submitted\n");
        using var output = new StringWriter();
        await ChatLineAdapter.RunAsync(input, output, view, engine, () => Task.CompletedTask, default);
        Assert.Equal(["\"quoted prompt\"", "/exit"], received);
        Assert.Equal("not submitted", await input.ReadLineAsync());
        Assert.Equal(received, engine.History);
        Assert.True(engine.Completion.IsCompleted);
    }

    [Fact]
    public async Task LineAdapterWaitsForTheRunningTurnBeforeReadingMoreData()
    {
        var view = new PlainChatView(TextWriter.Null, "");
        var engine = new ChatSessionEngine(view);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var input = new CountingReader("first\nsecond\n/exit\n");
        var seen = new List<string>();
        engine.SetPromptHandler(async (line, ct) =>
        {
            seen.Add(line);
            if (line == "first") { entered.TrySetResult(); await release.Task.WaitAsync(ct); }
            if (line == "/exit") engine.Stop();
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var running = ChatLineAdapter.RunAsync(input, TextWriter.Null, view, engine, () => Task.CompletedTask, deadline.Token);
        await entered.Task.WaitAsync(deadline.Token);
        try { Assert.Equal(1, input.Reads); Assert.Equal(["first"], seen); }
        finally { release.TrySetResult(); }
        await running;
        Assert.Equal(["first", "second", "/exit"], seen);
    }

    [Fact]
    public async Task TheSameQueueAndStateChangeGuardsWorkWithoutAnyWidgets()
    {
        using var diagnostics = new StringWriter();
        var view = new PlainChatView(diagnostics, ""); var engine = new ChatSessionEngine(view);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<string>();
        engine.SetStateChangingCommandClassifier(line => line.StartsWith("/model", StringComparison.Ordinal));
        engine.SetPromptHandler(async (line, ct) => { seen.Add(line); if (line == "first") await release.Task.WaitAsync(ct); });
        view.InputText = "first"; engine.Submit();
        view.InputText = "second"; engine.Submit();
        view.InputText = "/model another"; engine.Submit();
        Assert.Equal("/model another", view.InputText);
        Assert.Contains("a run is still active", diagnostics.ToString());
        Assert.Equal(["first"], seen);
        release.TrySetResult();
        await engine.WaitForIdleAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["first", "second"], seen);
        Assert.False(engine.IsBusy);
        engine.Stop(); await engine.Completion;
    }

    [Fact]
    public async Task State_change_while_a_local_command_owns_the_surface_is_dropped()
    {
        using var diagnostics = new StringWriter();
        var view = new PlainChatView(diagnostics, ""); var engine = new ChatSessionEngine(view);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.SetStateChangingCommandClassifier(line => line.StartsWith("/model", StringComparison.Ordinal));
        engine.SetLocalCommandClassifier(line => line.StartsWith("/sessions", StringComparison.Ordinal));
        engine.SetPromptHandler(async (line, ct) =>
        {
            if (line == "/sessions")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
        });

        view.InputText = "/sessions";
        engine.Submit();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // A stray submit while the local surface owns the input: no misleading busy hint.
        view.InputText = "/model another";
        engine.Submit();
        Assert.Equal("/model another", view.InputText);
        Assert.DoesNotContain("a run is still active", diagnostics.ToString());

        release.TrySetResult();
        await engine.WaitForIdleAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        engine.Stop(); await engine.Completion;
    }

    [Fact]
    public async Task Refused_state_change_names_the_paused_queue_instead_of_a_run()
    {
        using var diagnostics = new StringWriter();
        var view = new PlainChatView(diagnostics, ""); var engine = new ChatSessionEngine(view);
        engine.SetStateChangingCommandClassifier(line => line.StartsWith("/new", StringComparison.Ordinal));
        engine.PauseQueuedSubmissions();

        view.InputText = "/new";
        engine.Submit();

        Assert.Contains("pending approval, question or retry", diagnostics.ToString());
        Assert.DoesNotContain("a run is still active", diagnostics.ToString());
        engine.Stop(); await engine.Completion;
    }

    [Fact]
    public async Task An_explicit_prompt_resumes_a_paused_queue_left_by_a_failed_turn()
    {
        using var diagnostics = new StringWriter();
        var view = new PlainChatView(diagnostics, "");
        var engine = new ChatSessionEngine(view);
        var seen = new List<string>();
        engine.SetPromptHandler((line, _) =>
        {
            lock (seen) seen.Add(line);
            return Task.CompletedTask;
        });
        // A failed turn left a retry hold and paused the queue: nothing will drain it
        // on its own. The retry probe stands in for ChatSubmissionState.Retryable.
        engine.SetPausedQueueRetryProbe(() => true);
        engine.PauseQueuedSubmissions();

        view.InputText = "retry after fixing the key";
        engine.Submit();

        await WaitUntilAsync(() => { lock (seen) return seen.Count == 1; });
        Assert.Equal(["retry after fixing the key"], seen);
        Assert.DoesNotContain("prompt queued", diagnostics.ToString());
        engine.Stop();
        await engine.Completion;
    }

    [Fact]
    public async Task A_resumed_paused_queue_runs_the_explicit_retry_before_older_queued_prompts()
    {
        using var diagnostics = new StringWriter();
        var view = new PlainChatView(diagnostics, "");
        var engine = new ChatSessionEngine(view);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<string>();
        engine.SetPromptHandler(async (line, ct) =>
        {
            lock (seen) seen.Add(line);
            if (line == "first") await release.Task.WaitAsync(ct);
        });

        view.InputText = "first";
        engine.Submit();
        await WaitUntilAsync(() => { lock (seen) return seen.Count == 1; });
        view.InputText = "queued";
        engine.Submit();
        engine.SetPausedQueueRetryProbe(() => true);
        engine.PauseQueuedSubmissions();
        release.TrySetResult();
        await WaitUntilAsync(() => !engine.IsBusy);

        view.InputText = "retry";
        engine.Submit();

        await WaitUntilAsync(() => { lock (seen) return seen.Count == 3; });
        Assert.Equal(["first", "retry", "queued"], seen);
        engine.Stop();
        await engine.Completion;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition was not reached");
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task EmptyEofDoesNotCreateASubmission()
    {
        var view = new PlainChatView(TextWriter.Null, ""); var engine = new ChatSessionEngine(view);
        engine.SetPromptHandler((_, _) => throw new InvalidOperationException("must not submit at EOF"));
        using var input = new StringReader("");
        await ChatLineAdapter.RunAsync(input, TextWriter.Null, view, engine, () => Task.CompletedTask, default);
        Assert.Empty(engine.History);
        Assert.True(engine.Completion.IsCompleted);
    }

    [Fact]
    public async Task DroppedFileIntakeSettlesBeforeTheAdapterReadsThePrompt()
    {
        var view = new PlainChatView(TextWriter.Null, ""); var engine = new ChatSessionEngine(view);
        var attachments = new ChatAttachmentTasks();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? intake = null;
        engine.SetDropHandler(line =>
        {
            if (line != "image.png") return false;
            intake = attachments.RunAsync(async () => { reading.SetResult(); await release.Task; });
            return true;
        });
        var seen = new List<string>();
        engine.SetPromptHandler((line, _) => { seen.Add(line); return Task.CompletedTask; });
        using var input = new CountingReader("image.png\ndescribe this\n");
        var running = ChatLineAdapter.RunAsync(input, TextWriter.Null, view, engine, attachments.WaitForIdleAsync, default);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { Assert.Equal(1, input.Reads); Assert.Empty(seen); }
        finally { release.TrySetResult(); }
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(intake!.IsCompleted); Assert.Equal(["describe this"], seen);
    }

    [Fact]
    public async Task CancellationWaitsForOwnedCleanupBeforeReturningFromTheAdapter()
    {
        using var lifetime = new CancellationTokenSource();
        var view = new PlainChatView(TextWriter.Null, ""); var engine = new ChatSessionEngine(view, lifetime.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.SetShutdownSettlement(() => cleaned.Task);
        engine.SetPromptHandler(async (_, ct) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct); }
            finally { await release.Task; cleaned.TrySetResult(); }
        });
        using var input = new StringReader("work\n");
        var running = ChatLineAdapter.RunAsync(input, TextWriter.Null, view, engine, () => Task.CompletedTask, lifetime.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lifetime.Cancel();
        try { Assert.False(cleaned.Task.IsCompleted); Assert.False(running.IsCompleted); }
        finally { release.TrySetResult(); }
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cleaned.Task.IsCompleted);
    }

    [Fact]
    public async Task HistoryHasOneBoundedOwnerForLineInput()
    {
        var view = new PlainChatView(TextWriter.Null, ""); var engine = new ChatSessionEngine(view);
        engine.SetPromptHandler((_, _) => Task.CompletedTask);
        using var input = new StringReader(string.Join('\n', Enumerable.Range(0, 120).Select(i => "prompt " + i)) + "\nprompt 119\n");
        await ChatLineAdapter.RunAsync(input, TextWriter.Null, view, engine, () => Task.CompletedTask, default);
        Assert.Equal(ChatSessionEngine.MaxHistory, engine.History.Count);
        Assert.Equal("prompt 20", engine.History[0]); Assert.Equal("prompt 119", engine.History[^1]);
    }

    [Fact]
    public void KeyCommandLinesStayOutOfHistory()
    {
        var engine = new ChatSessionEngine(new PlainChatView(TextWriter.Null, ""));
        engine.RememberForTests("/key sk-pasted-secret");
        engine.RememberForTests("  /KEY openai");
        engine.RememberForTests("/keys are not the key command");

        Assert.Equal(["/keys are not the key command"], engine.History);
    }

    [Fact]
    public async Task HttpTimeoutIsReportedAsAFailureNotAnEscapeInterrupt()
    {
        using var diagnostics = new StringWriter();
        var view = new PlainChatView(diagnostics, ""); var engine = new ChatSessionEngine(view);
        engine.SetPromptHandler((_, _) => Task.FromException(
            new TaskCanceledException("request timed out", new TimeoutException())));

        view.InputText = "hello"; engine.Submit();
        await engine.WaitForIdleAsync(default).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("request timed out", diagnostics.ToString());
        Assert.DoesNotContain("esc interrupted", diagnostics.ToString());
        engine.Stop(); await engine.Completion;
    }

    [Fact]
    public async Task UserCancellationStillReportsAnEscapeInterrupt()
    {
        using var diagnostics = new StringWriter();
        var view = new PlainChatView(diagnostics, ""); var engine = new ChatSessionEngine(view);
        engine.SetPromptHandler((_, _) => Task.FromException(new OperationCanceledException()));

        view.InputText = "hello"; engine.Submit();
        await engine.WaitForIdleAsync(default).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("esc interrupted", diagnostics.ToString());
        engine.Stop(); await engine.Completion;
    }

    private sealed class CountingReader(string text) : StringReader(text)
    {
        internal int Reads;
        public override ValueTask<string?> ReadLineAsync(CancellationToken ct)
        { Reads++; return base.ReadLineAsync(ct); }
    }
}
