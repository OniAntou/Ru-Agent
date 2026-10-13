using System.Net;
using System.Net.Http;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SessionTranscriptBuilderTests
{
    private static AgentActivityItem Activity(string id) => new(new AgentActivityModel(
        id, AgentActivityKind.Thinking, AgentActivityState.Completed,
        null, null, null, null, null, null, null, 4000, "succeeded", null, null, null));

    private static SessionRunReplay Run(string id, string? text = null) => new(id,
        [Activity(id + "-thinking")], text is null
            ? [new(1, "activity", null, id + "-thinking")]
            : [new(1, "activity", null, id + "-thinking"), new(2, "text", text, null)]);

    [Fact]
    public void Failed_gold_turn_is_replayed_after_its_question_and_keeps_greeting_untouched()
    {
        var rows = SessionTranscriptBuilder.Build(
            [new("user", "Xin chào", "hello"), new("assistant", "Chào anh", "hello"),
             new("user", "Giá vàng hôm nay?", "gold")],
            // Order is deliberately reversed: ids, not list position, determine ownership.
            [Run("gold", "Để em tra cứu giá vàng."), Run("hello", "Chào anh")]);
        Assert.Equal(new[] { "user", "assistant", "user", "assistant" }, rows.Select(row => row.Role));
        Assert.Equal("Chào anh", rows[1].Content);
        Assert.Equal("hello-thinking", Assert.Single(rows[1].AgentActivities!).ActivityId);
        Assert.Equal("Giá vàng hôm nay?", rows[2].Content);
        Assert.Equal("Để em tra cứu giá vàng.", rows[3].Content);
        Assert.Equal("gold-thinking", Assert.Single(rows[3].AgentActivities!).ActivityId);
    }

    [Fact]
    public void Failed_middle_turn_does_not_shift_successful_turns_or_duplicate_its_feed()
    {
        var rows = SessionTranscriptBuilder.Build(
            [new("user", "one", "one"), new("assistant", "first", "one"),
             new("user", "two", "two"), new("user", "three", "three"),
             new("assistant", "last", "three")],
            [Run("one", "first"), Run("two"), Run("three", "last")]);
        Assert.Equal(new[] { "one", "two", "three" }, rows.Where(row => row.Role == "assistant").Select(row => row.SourceRunId));
        Assert.Equal("two", rows[2].Content);
        Assert.Equal("two", rows[3].SourceRunId);
        Assert.Equal("three", rows[4].Content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Partial_or_activity_only_replay_cannot_erase_persisted_final_answer(bool narration)
    {
        var rows = SessionTranscriptBuilder.Build(
            [new("user", "question", "r"), new("assistant", "Bảng giá đầy đủ.", "r")],
            [Run("r", narration ? "Để em tra cứu." : null)]);
        Assert.EndsWith("Bảng giá đầy đủ.", rows[1].Content);
        Assert.Equal("Bảng giá đầy đủ.", rows[1].Segments!.OfType<TextSegment>().Last().Text);
    }

    [Fact]
    public void Complete_replay_does_not_duplicate_the_persisted_final_answer()
    {
        var rows = SessionTranscriptBuilder.Build(
            [new("user", "question", "r"), new("assistant", "Final.", "r")], [Run("r", "Final.")]);
        Assert.Equal("Final.", rows[1].Content);
        Assert.Single(rows[1].Segments!.OfType<TextSegment>());
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void Missing_replay_keeps_saved_answer_and_legacy_unbalanced_history_is_not_guessed()
    {
        var rows = SessionTranscriptBuilder.Build(
            [new("user", "hi", null), new("assistant", "hello", null), new("user", "gold?", null)],
            [Run("old", "hello"), Run("new", "gold")]);
        Assert.Equal(3, rows.Count);
        Assert.Equal("hello", rows[1].Content);
        Assert.Null(rows[1].AgentActivities);
        var explicitRows = SessionTranscriptBuilder.Build([new("assistant", "saved", "missing")], [Run("other")]);
        Assert.Equal("saved", explicitRows[0].Content);
        Assert.Null(explicitRows[0].AgentActivities);
    }

    [Fact]
    public async Task Session_api_preserves_the_run_identity_already_present_on_the_wire()
    {
        using var handler = new HistoryHandler();
        using var api = new ApiService(new BackendSession(new Uri("http://127.0.0.1:59999/"), "synthetic"), handler);
        var history = await api.GetSessionHistoryAsync("session");
        Assert.NotNull(history);
        Assert.Equal("r1", history![0].SourceRunId);
        Assert.Equal("assistant", history[1].Role);
        Assert.Equal("r1", history[1].SourceRunId);
        Assert.Null(history[2].SourceRunId);
    }

    private sealed class HistoryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                {"session":{"messages":[{"role":"user","content":"hi","source_run_id":"r1"},
                {"role":"ai","content":"hello","source_run_id":"r1"}, {"role":"user","content":"legacy"}]}}
                """) });
    }
}
