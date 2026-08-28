using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ChatTurnPresenterTests
{
    [Fact]
    public void Presenter_routes_only_the_active_correlation_and_completes_once()
    {
        var queue = new ChatTurnQueue();
        Assert.True(queue.TryEnqueue("session-1", "c_first123", out var turn));
        var presenter = new ChatTurnPresenter(queue);

        var delta = presenter.Apply(new ChatEventFrame(
            "chunk", "Xin", "session-1", null, "c_first123"));
        var stale = presenter.Apply(new ChatEventFrame(
            "chunk", " cũ", "session-1", null, "c_stale12"));
        var done = presenter.Apply(new ChatEventFrame(
            "done", "Xin chào", "session-1", null, "c_first123"));
        var duplicate = presenter.Apply(new ChatEventFrame(
            "done", "Xin chào", "session-1", null, "c_first123"));

        Assert.Equal(ChatTurnUpdateKind.Delta, delta.Kind);
        Assert.Equal(turn, delta.Turn);
        Assert.Equal("Xin", delta.Content);
        Assert.Equal(ChatTurnUpdateKind.Ignored, stale.Kind);
        Assert.Equal(ChatTurnUpdateKind.Done, done.Kind);
        Assert.Equal(ChatTurnUpdateKind.Ignored, duplicate.Kind);
        Assert.Null(queue.ActiveTurn);
    }
}
