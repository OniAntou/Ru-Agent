using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ChatRunSessionTests
{
    [Fact]
    public void Failed_continue_keeps_the_parked_run_resumable()
    {
        using var session = new ChatRunSession();
        session.RememberParked("parked");
        session.BeginContinue("parked", CancellationToken.None);
        session.EndTurn(); // Continue POST failed; no confirmation was applied.
        Assert.Equal("parked", session.ParkedRunId);
        Assert.False(session.IsTurnActive);

        session.BeginContinue("parked", CancellationToken.None);
        session.ForgetParked("parked"); // Confirmed Continue.
        Assert.Null(session.ParkedRunId);
        Assert.Equal("parked", session.ActiveRunId);
    }

    [Fact]
    public void A_suppressed_reply_is_remembered_for_the_turn_and_cleared_by_the_next_one()
    {
        using var session = new ChatRunSession();
        session.BeginNewTurn(CancellationToken.None);
        Assert.False(session.HasSuppressedReply);

        session.NoteSuppressedReply();
        session.RecordAssistantPayload("payload_kept");
        Assert.True(session.HasSuppressedReply);

        session.EndTurn();
        session.BeginNewTurn(CancellationToken.None);

        Assert.False(session.HasSuppressedReply);
        Assert.Empty(session.AssistantPayloads);
    }

    [Fact]
    public void Continuing_another_run_does_not_forget_the_original_park()
    {
        using var session = new ChatRunSession();
        using var interrupted = new CancellationTokenSource();
        session.RememberParked("original");
        session.BeginContinue("other", interrupted.Token);
        session.ForgetParked("other");
        interrupted.Cancel();
        Assert.Equal("original", session.ParkedRunId);
        Assert.Equal("other", session.InterruptedRunId);
    }

    [Fact]
    public void Parking_stops_the_follow_without_requesting_server_cancellation()
    {
        using var session = new ChatRunSession();
        session.BeginNewTurn(CancellationToken.None);
        session.AcceptCreatedRun("active");
        var token = session.Token;
        session.ParkActiveRun("active");
        Assert.True(token.IsCancellationRequested);
        Assert.Null(session.InterruptedRunId);
        Assert.Null(session.FollowableRunId);
        session.EndTurn();
        Assert.Equal("active", session.ParkedRunId);
    }

    [Fact]
    public void New_turn_and_follow_reset_their_recovery_state()
    {
        using var session = new ChatRunSession();
        session.RememberParked("old");
        session.BeginFollow("followed", CancellationToken.None);
        session.RecordAssistantPayload("payload-1");
        session.RecordAssistantPayload("payload-1");
        session.RecordAssistantPayload("payload-2");
        Assert.Equal(new[] { "payload-1", "payload-2" }, session.AssistantPayloads);
        Assert.Null(session.ParkedRunId);
        session.EndTurn();
        session.BeginNewTurn(CancellationToken.None);
        Assert.Empty(session.AssistantPayloads);
        Assert.Null(session.ActiveRunId);
        Assert.Null(session.FollowableRunId);
        session.AcceptCreatedRun("created");
        Assert.Equal("created", session.ActiveRunId);
        Assert.Equal("created", session.FollowableRunId);
    }

    [Fact]
    public void Admission_rejects_a_second_turn_without_losing_the_active_turn()
    {
        using var session = new ChatRunSession();
        session.BeginNewTurn(CancellationToken.None);
        session.AcceptCreatedRun("first");
        var token = session.Token;
        Assert.Throws<InvalidOperationException>(() => session.BeginFollow("second", CancellationToken.None));
        Assert.Equal("first", session.ActiveRunId);
        Assert.Equal(token, session.Token);
        Assert.False(token.IsCancellationRequested);
    }

    [Fact]
    public void Session_reset_and_disposal_do_not_retain_run_or_payload_identity()
    {
        var session = new ChatRunSession();
        session.BeginFollow("first", CancellationToken.None);
        session.RecordAssistantPayload("private-reply");
        var token = session.Token;
        session.Dispose();
        Assert.True(token.IsCancellationRequested);
        Assert.False(session.IsTurnActive);
        Assert.Null(session.ActiveRunId);
        Assert.Null(session.ParkedRunId);
        Assert.Null(session.FollowableRunId);
        Assert.Empty(session.AssistantPayloads);
        session.Dispose();
    }
}
