using RuAgent.Cli.Chat;
using RuAgent.Cli.TerminalGui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ChatSessionEngineTests
{
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
        Assert.Contains("finish queued prompts", diagnostics.ToString());
        Assert.Equal(["first"], seen);
        release.TrySetResult();
        await engine.WaitForIdleAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["first", "second"], seen);
        Assert.True(engine.CanCloseSession);
        engine.Stop(); await engine.Completion;
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

    private sealed class CountingReader(string text) : StringReader(text)
    {
        internal int Reads;
        public override ValueTask<string?> ReadLineAsync(CancellationToken ct)
        { Reads++; return base.ReadLineAsync(ct); }
    }
}
