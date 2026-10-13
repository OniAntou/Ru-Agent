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
    public void Retrying_a_later_step_preserves_completed_earlier_step_text()
    {
        var reply = new AgentReplyAccumulator();
        reply.Begin("s", "r");
        reply.TryAppend("r", "search", 1, "Để em tra cứu.\n");
        reply.TryAppend("r", "answer", 1, "Bảng giá chưa xong");
        reply.TakeSnapshot();
        reply.TryReset("r", "answer", 2);
        reply.TryAppend("r", "answer", 3, "Bảng giá đầy đủ.");
        Assert.Equal("Để em tra cứu.\nBảng giá đầy đủ.", reply.TakeSnapshot()!.Text);
    }

    [Fact]
    public void Rebinding_each_frame_to_the_same_run_keeps_duplicate_history()
    {
        var reply = new AgentReplyAccumulator();
        reply.Begin("s1", "s1");
        reply.Rebind("s1", "r1");
        Assert.True(reply.TryAppend("r1", "m1", 1, "Chào anh"));
        var first = reply.TakeSnapshot()!;

        reply.Rebind("s1", "r1");
        Assert.False(reply.TryAppend("r1", "m1", 1, "Chào anh"));
        Assert.Null(reply.TakeSnapshot());
        Assert.True(reply.TryAppend("r1", "m1", 2, "!"));
        var next = reply.TakeSnapshot()!;
        Assert.Equal("Chào anh!", next.Text);
        Assert.Equal(first.Generation, next.Generation);
    }

    [Fact]
    public void Replayed_reset_after_a_rebind_cannot_erase_the_retried_answer()
    {
        var reply = new AgentReplyAccumulator();
        reply.Begin("s1", "r1");
        reply.TryAppend("r1", "m1", 1, "partial");
        reply.TryReset("r1", "m1", 2);
        reply.TryAppend("r1", "m1", 3, "replacement");
        Assert.Equal("replacement", reply.TakeSnapshot()!.Text);

        reply.Rebind("s1", "r1");
        Assert.False(reply.TryReset("r1", "m1", 2));
        Assert.Null(reply.TakeSnapshot());
    }

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
    public void Reset_drops_the_superseded_attempt_text_instead_of_appending_onto_it()
    {
        // A model step retried after a transient failure re-runs the answer. The
        // partial text of the attempt that failed must be discarded, not stacked
        // under the retry.
        var reply = new AgentReplyAccumulator();
        reply.Begin("s1", "r1");

        Assert.True(reply.TryAppend("r1", "m1", 1, "half an ans"));
        var partial = reply.TakeSnapshot();
        Assert.Equal("half an ans", partial!.Text);

        Assert.True(reply.TryReset("r1", "m1", 2));
        var cleared = reply.TakeSnapshot();
        Assert.NotNull(cleared);
        Assert.Equal(string.Empty, cleared.Text);

        // The retry continues after the reset sequence and replaces the text.
        Assert.True(reply.TryAppend("r1", "m1", 3, "the full answer"));
        Assert.Equal("the full answer", reply.TakeSnapshot()!.Text);

        // A replayed reset frame is ignored.
        Assert.False(reply.TryReset("r1", "m1", 2));
    }

    [Fact]
    public void Rebind_moves_a_session_bound_reply_to_the_live_run_without_dropping_text()
    {
        var reply = new AgentReplyAccumulator();
        reply.Begin("s1", "s1");
        Assert.True(reply.HasSession);
        Assert.Equal("s1", reply.SessionId);

        reply.Rebind("s1", "r1");
        Assert.True(reply.TryAppend("r1", "m1", 1, "hello"));
        var snap = reply.TakeSnapshot();
        Assert.NotNull(snap);
        Assert.Equal("s1", snap.SessionId);
        Assert.Equal("r1", snap.RunId);
        Assert.Equal("hello", snap.Text);
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
