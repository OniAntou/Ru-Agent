using Markdig.Extensions.Tables;
using RuApp.Controls;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class GuiReviewRegressionTests
{
    private sealed class HistoryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"session":{"messages":[
                      {"role":"system","content":"hidden"},
                      {"role":"user","content":"ask"},
                      {"role":"tool","content":"hidden"},
                      {"role":"assistant","content":"reply"}
                    ]}}
                    """, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    [Fact]
    public async Task History_api_retains_indices_before_filtering_non_chat_roles()
    {
        using var handler = new HistoryHandler();
        using var api = new ApiService(new BackendSession(new Uri("http://127.0.0.1:59999/"), "synthetic-token"), handler);
        var history = await api.GetSessionHistoryAsync("session-a");
        Assert.NotNull(history);
        Assert.Equal(new int?[] { 1, 3 }, history.Select(row => row.MessageIndex));
    }

    [Fact]
    public async Task Rewind_uses_persisted_index_and_reloads_only_the_owning_session()
    {
        bool owns = true, reloaded = false;
        var response = new TaskCompletionSource<bool>();
        var rewind = SessionRewindWorkflow.RunAsync("session-a", 7, () => owns,
            (session, index) => { Assert.Equal("session-a", session); Assert.Equal(7, index); return response.Task; },
            () => { reloaded = true; return Task.FromResult(true); });
        owns = false;
        response.SetResult(true);
        Assert.False(await rewind);
        Assert.False(reloaded);
        Assert.False(await SessionRewindWorkflow.RunAsync("session-a", null, () => true,
            (_, _) => throw new InvalidOperationException(), () => throw new InvalidOperationException()));
    }

    [Fact]
    public void Transcript_rows_keep_raw_server_indices()
    {
        var rows = SessionTranscriptBuilder.Build(
            [new("user", "hello", null, MessageIndex: 2), new("assistant", "hi", null, MessageIndex: 5)], [], "session-a");
        Assert.Equal(new int?[] { 2, 5 }, rows.Select(row => row.SourceMessageIndex));
        Assert.All(rows, row => Assert.Equal("session-a", row.SessionId));
    }

    [Fact]
    public async Task Queue_keeps_unaccepted_turns_after_exceptions_and_removes_accepted_turns()
    {
        var queue = new AgentTurnQueue();
        var attachment = new RuApp.Models.ImageAttachmentItem { FileName = "keep.png" };
        queue.TryEnqueue("prompt", "prompt", [attachment]);
        var turn = Assert.Single(queue.Items);
        await Assert.ThrowsAsync<IOException>(() => queue.DrainAsync((_, _) => throw new IOException()));
        Assert.Same(turn, Assert.Single(queue.Items));
        Assert.Same(attachment, Assert.Single(turn.Attachments));
        await queue.DrainAsync((_, _) => Task.FromResult(false));
        Assert.Same(turn, Assert.Single(queue.Items));
        await Assert.ThrowsAsync<IOException>(() => queue.DrainAsync((_, accept) => { accept(); throw new IOException(); }));
        Assert.Empty(queue.Items);
    }

    [Fact]
    public void Live_turn_gets_saved_indices_without_replacing_its_visible_reply()
    {
        var user = new RuApp.Models.ChatMessageItem { Role = "user" };
        var assistant = new RuApp.Models.ChatMessageItem { Role = "assistant", Content = "streamed reply" };
        SessionTranscriptBuilder.BindPersistedLiveTurn(
            [new("user", "ask", "run-a", 3), new("assistant", "saved", "run-a", 6)],
            "run-a", user, assistant);
        Assert.Equal(3, user.SourceMessageIndex);
        Assert.Equal(6, assistant.SourceMessageIndex);
        Assert.Equal("streamed reply", assistant.Content);
    }

    [Fact]
    public void A_hidden_machine_envelope_cannot_become_the_live_reply_rewind_target()
    {
        var assistant = new RuApp.Models.ChatMessageItem { Role = "assistant" };
        SessionTranscriptBuilder.BindPersistedLiveTurn(
            [new("assistant", "<tool_call>{\"tool\":\"read\",\"arguments\":{\"path\":\"README.md\"}}</tool_call>", "run-a", 4)],
            "run-a", null, assistant);
        Assert.Null(assistant.SourceMessageIndex);
    }

    [Fact]
    public async Task Queue_acceptance_allows_next_turn_without_old_completion_unlocking_it()
    {
        var queue = new AgentTurnQueue();
        queue.TryEnqueue("one", "one", null);
        queue.TryEnqueue("two", "two", null);
        var firstTail = new TaskCompletionSource<bool>();
        var secondTail = new TaskCompletionSource<bool>();
        Action? acceptSecond = null;
        var first = queue.DrainAsync((_, accept) => { accept(); return firstTail.Task; });
        var second = queue.DrainAsync((_, accept) => { acceptSecond = accept; return secondTail.Task; });
        firstTail.SetResult(true);
        await first;
        await queue.DrainAsync((_, _) => throw new InvalidOperationException("Double send"));
        acceptSecond!();
        secondTail.SetResult(true);
        await second;
        Assert.Empty(queue.Items);
    }

    [Fact]
    public async Task Reasoning_latest_choice_survives_an_older_failed_save()
    {
        var queue = new ReasoningSelectionSaveQueue();
        queue.Synchronize("model", "default");
        var pending = new TaskCompletionSource<bool>();
        var old = queue.SaveAsync("model", "low", "default", () => pending.Task);
        var newest = queue.SaveAsync("model", "high", "low", () => Task.FromResult(true));
        queue.Synchronize("model", "default");
        Assert.True(queue.IsPending);
        pending.SetResult(false);
        Assert.Null(await old);
        Assert.Equal("high", await newest);
        Assert.False(queue.IsPending);
    }

    [Fact]
    public async Task Reasoning_failed_latest_choice_uses_last_successfully_persisted_value()
    {
        var queue = new ReasoningSelectionSaveQueue();
        var pending = new TaskCompletionSource<bool>();
        var old = queue.SaveAsync("model", "low", "default", () => pending.Task);
        var newest = queue.SaveAsync("model", "high", "low", () => Task.FromResult(false));
        pending.SetResult(true);
        Assert.Null(await old);
        Assert.Equal("low", await newest);
    }

    [Fact]
    public async Task Reasoning_old_context_cannot_change_the_new_context_fallback()
    {
        var queue = new ReasoningSelectionSaveQueue();
        var pending = new TaskCompletionSource<bool>();
        var old = queue.SaveAsync("a", "high", "default", () => pending.Task);
        queue.Synchronize("b", "low");
        queue.Synchronize("a", "medium");
        pending.SetResult(true);
        Assert.Null(await old);
        Assert.Equal("medium", await queue.SaveAsync("a", "low", "medium", () => Task.FromResult(false)));
    }

    [Fact]
    public async Task Reasoning_default_choice_survives_a_stale_catalog_refresh()
    {
        var queue = new ReasoningSelectionSaveQueue();
        queue.Synchronize("model", "high");
        Assert.Equal("default", await queue.SaveAsync("model", "default", "high", () => Task.FromResult(true)));
        queue.Synchronize("model", "high");
        Assert.Equal("default", queue.ConfirmedEffort);
    }

    [Fact]
    public void Large_table_has_bounded_preview_and_complete_copy_content()
    {
        string Row(int row) => "|" + string.Join("|", Enumerable.Range(0, 9).Select(column => $"r{row}c{column}")) + "|";
        string markdown = Row(0) + "\n|" + string.Join("|", Enumerable.Repeat("---", 9)) + "|\n"
            + string.Join("\n", Enumerable.Range(1, 60).Select(Row));
        var table = Assert.Single(MarkdownEngine.Parse(markdown).OfType<Table>());
        var presentation = MarkdownTablePresentation.Create(table);
        Assert.True(presentation.IsTruncated);
        Assert.Equal(50, presentation.PreviewRows.Count);
        Assert.Equal(8, presentation.PreviewColumns);
        Assert.Contains("49 of 60 rows", presentation.Notice);
        Assert.Equal(61, presentation.FullText.Split('\n').Length);
        Assert.Contains("r60c8", presentation.FullText);
    }
}
