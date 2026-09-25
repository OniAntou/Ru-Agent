using RuAgent.Runtime.Worker;
using Xunit;

namespace RuAgent.Runtime.Worker.Tests;

public sealed class WorkerRequestHistoryTests
{
    [Fact]
    public void TryBegin_accepts_a_request_id_once()
    {
        var history = new WorkerRequestHistory();

        Assert.True(history.TryBegin("request-1"));
        Assert.False(history.TryBegin("request-1"));
    }

    [Fact]
    public void NextSequence_is_monotonic_per_request_id()
    {
        var history = new WorkerRequestHistory();

        Assert.Equal(1, history.NextSequence("request-1"));
        Assert.Equal(2, history.NextSequence("request-1"));
        Assert.Equal(3, history.NextSequence("request-1"));
        Assert.Equal(1, history.NextSequence("request-2"));
    }

    [Fact]
    public void NextSequence_for_an_unseen_id_does_not_mark_it_begun()
    {
        var history = new WorkerRequestHistory();

        Assert.Equal(1, history.NextSequence("cancel-only"));

        Assert.True(history.TryBegin("cancel-only"));
    }

    [Fact]
    public void Eviction_bounds_the_window_oldest_first()
    {
        var history = new WorkerRequestHistory(limit: 3);

        Assert.True(history.TryBegin("a"));
        Assert.True(history.TryBegin("b"));
        Assert.True(history.TryBegin("c"));
        Assert.True(history.TryBegin("d"));

        Assert.Equal(3, history.Count);
    }

    [Fact]
    public void Eviction_drops_duplicate_detection_for_the_oldest_id()
    {
        var history = new WorkerRequestHistory(limit: 3);

        Assert.True(history.TryBegin("a"));
        Assert.True(history.TryBegin("b"));
        Assert.True(history.TryBegin("c"));
        Assert.True(history.TryBegin("d"));

        Assert.False(history.TryBegin("b"));
        Assert.True(history.TryBegin("a"));
    }

    [Fact]
    public void Eviction_resets_the_sequence_of_the_evicted_id()
    {
        var history = new WorkerRequestHistory(limit: 2);

        Assert.Equal(1, history.NextSequence("a"));
        Assert.Equal(2, history.NextSequence("a"));
        Assert.True(history.TryBegin("b"));
        Assert.True(history.TryBegin("c"));

        Assert.Equal(1, history.NextSequence("a"));
    }

    [Fact]
    public void In_flight_ids_are_never_evicted_by_their_own_traffic()
    {
        var history = new WorkerRequestHistory(limit: 2);

        Assert.True(history.TryBegin("hot"));
        for (var i = 0; i < 50; i++)
        {
            Assert.False(history.TryBegin("hot"));
            var seq = history.NextSequence("hot");
            Assert.Equal(i + 1, seq);
        }
    }

    [Fact]
    public void Rejects_a_non_positive_limit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkerRequestHistory(limit: 0));
    }
}
