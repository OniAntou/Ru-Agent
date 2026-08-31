using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentReplyAccumulatorTests
{
    [Fact]
    public void Reply_dedup_preserves_unicode_and_rejects_an_old_run()
    {
        var reply = new AgentReplyAccumulator();
        reply.Begin("s1", "r1");

        Assert.True(reply.TryAppend("r1", "m1", 1, "Chào "));
        Assert.False(reply.TryAppend("r1", "m1", 1, "duplicate"));
        Assert.True(reply.TryAppend("r1", "m1", 2, "anh yêu ❤"));

        var snap = reply.TakeSnapshot();
        Assert.NotNull(snap);
        Assert.Equal("s1", snap.SessionId);
        Assert.Equal("r1", snap.RunId);
        Assert.Equal("Chào anh yêu ❤", snap.Text);

        // Second snapshot returns null since no new text was appended
        Assert.Null(reply.TakeSnapshot());

        // Switching to new session/run
        reply.Begin("s2", "r2");
        Assert.False(reply.TryAppend("r1", "m1", 3, "stale"));
        Assert.Null(reply.TakeSnapshot());

        Assert.True(reply.TryAppend("r2", "m1", 1, "Mới"));
        var snap2 = reply.TakeSnapshot();
        Assert.NotNull(snap2);
        Assert.Equal("s2", snap2.SessionId);
        Assert.Equal("r2", snap2.RunId);
        Assert.Equal("Mới", snap2.Text);
    }

    [Fact]
    public void Invalidate_clears_pending_text_and_identities()
    {
        var reply = new AgentReplyAccumulator();
        reply.Begin("s1", "r1");
        reply.TryAppend("r1", "m1", 1, "some text");

        reply.Invalidate();

        Assert.Null(reply.TakeSnapshot());
        Assert.False(reply.TryAppend("r1", "m1", 2, "more"));
        Assert.Null(reply.TakeSnapshot());
    }

    [Fact]
    public void TryAppend_validates_inputs()
    {
        var reply = new AgentReplyAccumulator();
        reply.Begin("s1", "r1");

        Assert.False(reply.TryAppend("", "m1", 1, "text"));
        Assert.False(reply.TryAppend("r1", "", 1, "text"));
        Assert.False(reply.TryAppend("r1", "m1", 1, null!));
        Assert.True(reply.TryAppend("r1", "m1", 1, ""));
    }

    [Fact]
    public void Large_scale_synthetic_deltas_with_duplicates_and_unicode()
    {
        var reply = new AgentReplyAccumulator();
        reply.Begin("session-10k", "run-10k");

        var expected = new StringBuilder();
        const int deltaCount = 10_000;
        string[] samples = ["Xin chào ", "Rushia neko~ ", "ニャー ", "✨ ", "🔥 "];

        for (int i = 0; i < deltaCount; i++)
        {
            string piece = samples[i % samples.Length];
            long seq = i;

            // First append succeeds
            bool added = reply.TryAppend("run-10k", "step-0", seq, piece);
            Assert.True(added);
            expected.Append(piece);

            // Duplicate append with same seq must be rejected
            bool dup = reply.TryAppend("run-10k", "step-0", seq, "DUP");
            Assert.False(dup);
        }

        var snapshot = reply.TakeSnapshot();
        Assert.NotNull(snapshot);
        Assert.Equal(expected.ToString(), snapshot.Text);
        Assert.Null(reply.TakeSnapshot());
    }

    [Fact]
    public void Concurrent_append_is_thread_safe()
    {
        var reply = new AgentReplyAccumulator();
        reply.Begin("s-concurrent", "r-concurrent");

        const int threads = 8;
        const int itemsPerThread = 1_000;

        Parallel.For(0, threads, threadId =>
        {
            for (int i = 0; i < itemsPerThread; i++)
            {
                string stepId = $"step-{threadId}";
                long seq = i;
                reply.TryAppend("r-concurrent", stepId, seq, "a");
                // Try a duplicate concurrently
                reply.TryAppend("r-concurrent", stepId, seq, "b");
            }
        });

        var snap = reply.TakeSnapshot();
        Assert.NotNull(snap);
        Assert.Equal(threads * itemsPerThread, snap.Text.Length);
    }
}
