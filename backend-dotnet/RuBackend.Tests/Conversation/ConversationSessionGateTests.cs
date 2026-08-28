using RuBackend.Conversation;
using Xunit;

namespace RuBackend.Tests.Conversation;

public sealed class ConversationSessionGateTests
{
    [Fact]
    public void Same_session_allows_only_one_active_turn()
    {
        using var gate = new ConversationSessionGate();
        using var first = gate.TryEnter("session-1");

        Assert.NotNull(first);
        Assert.Null(gate.TryEnter("session-1"));
    }

    [Fact]
    public void Different_sessions_can_enter_at_the_same_time()
    {
        using var gate = new ConversationSessionGate();
        using var first = gate.TryEnter("session-1");
        using var second = gate.TryEnter("session-2");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(2, gate.ActiveSessionCount);
    }

    [Fact]
    public void Releasing_a_lease_allows_a_following_turn_and_cleans_the_key()
    {
        using var gate = new ConversationSessionGate();
        var first = gate.TryEnter("session-1");

        Assert.NotNull(first);
        first!.Dispose();

        using var second = gate.TryEnter("session-1");
        Assert.NotNull(second);
        Assert.Equal(1, gate.ActiveSessionCount);
    }

    [Fact]
    public void Disposing_with_an_active_lease_defers_semaphore_disposal()
    {
        var gate = new ConversationSessionGate();
        var lease = gate.TryEnter("session-1");

        Assert.NotNull(lease);
        gate.Dispose();

        Assert.Null(Record.Exception(lease!.Dispose));
        Assert.Equal(0, gate.ActiveSessionCount);
        Assert.Throws<ObjectDisposedException>(() => gate.TryEnter("session-2"));
    }
}
