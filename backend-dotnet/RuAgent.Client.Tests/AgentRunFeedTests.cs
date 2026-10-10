using System.Runtime.CompilerServices;
using System.Threading;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed class AgentRunFeedTests
{
    [Fact]
    public async Task FollowAsync_resumes_from_last_durable_sequence_and_can_refill_budget_on_progress()
    {
        var cursors = new List<long>();
        var delivered = new List<SseFrame>();

        async IAsyncEnumerable<SseFrame> ReadEvents(
            long cursor,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cursors.Add(cursor);
            var attempt = cursors.Count;
            if (attempt <= 2)
            {
                yield return DurableFrame(attempt == 1 ? 5 : 6);
                await Task.Yield();
                throw new IOException("stream broke");
            }
        }

        await AgentRunFeed.FollowAsync(
            0,
            ReadEvents,
            _ => Task.FromResult<bool?>(true),
            (frame, _) =>
            {
                delivered.Add(frame);
                return Task.CompletedTask;
            },
            CancellationToken.None,
            new AgentRunFeedOptions
            {
                ResetResumeBudgetOnFrame = true,
                MaxResumeAttempts = 1,
                ResumeDelay = TimeSpan.Zero,
            });

        Assert.Equal(new long[] { 0, 5, 6 }, cursors);
        Assert.Equal(new long?[] { 5, 6 }, delivered.Select(frame => frame.DurableSequence).ToArray());
    }

    [Fact]
    public async Task FollowAsync_transient_frame_refills_cli_budget_without_advancing_durable_cursor()
    {
        var cursors = new List<long>();
        var delivered = new List<SseFrame>();

        async IAsyncEnumerable<SseFrame> ReadEvents(
            long cursor,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cursors.Add(cursor);
            if (cursors.Count == 1)
            {
                yield return new SseFrame(null, "transient", "delta", true, 17);
                await Task.Yield();
                throw new IOException("stream broke");
            }
        }

        await AgentRunFeed.FollowAsync(
            4,
            ReadEvents,
            _ => Task.FromResult<bool?>(true),
            (frame, _) =>
            {
                delivered.Add(frame);
                return Task.CompletedTask;
            },
            CancellationToken.None,
            new AgentRunFeedOptions
            {
                ResetResumeBudgetOnFrame = true,
                MaxResumeAttempts = 1,
                ResumeDelay = TimeSpan.Zero,
            });

        Assert.Equal(new long[] { 4, 4 }, cursors);
        Assert.Single(delivered);
        Assert.True(delivered[0].IsTransient);
        Assert.Null(delivered[0].DurableSequence);
    }

    [Fact]
    public async Task FollowAsync_cumulative_budget_exhausts_after_progress_when_refill_is_disabled()
    {
        var reads = 0;
        var delivered = 0;

        async IAsyncEnumerable<SseFrame> ReadEvents(
            long _,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            reads++;
            yield return DurableFrame(reads);
            await Task.Yield();
            throw new IOException("stream broke");
        }

        var error = await Assert.ThrowsAsync<AgentApiException>(() => AgentRunFeed.FollowAsync(
            0,
            ReadEvents,
            _ => Task.FromResult<bool?>(false),
            (_, _) =>
            {
                delivered++;
                return Task.CompletedTask;
            },
            CancellationToken.None,
            new AgentRunFeedOptions
            {
                MaxResumeAttempts = 1,
                ResumeDelay = TimeSpan.Zero,
            }));

        Assert.Equal("stream_interrupted", error.ErrorCode);
        Assert.Equal(2, reads);
        Assert.Equal(2, delivered);
    }

    [Fact]
    public async Task FollowAsync_propagates_callback_io_failure_without_reconnecting()
    {
        var reads = 0;
        var failure = new IOException("consumer failed");

        async IAsyncEnumerable<SseFrame> ReadEvents(
            long _,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            reads++;
            yield return DurableFrame(1);
            await Task.Yield();
        }

        var error = await Assert.ThrowsAsync<IOException>(() => AgentRunFeed.FollowAsync(
            0,
            ReadEvents,
            _ => Task.FromResult<bool?>(true),
            (_, _) => Task.FromException(failure),
            CancellationToken.None,
            new AgentRunFeedOptions { ResumeDelay = TimeSpan.Zero }));

        Assert.Same(failure, error);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task FollowAsync_propagates_cancellation_from_terminal_status_probe()
    {
        async IAsyncEnumerable<SseFrame> ReadEvents(
            long _,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }

        using var cancellation = new CancellationTokenSource();
        var failure = new OperationCanceledException(cancellation.Token);
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => AgentRunFeed.FollowAsync(
            0,
            ReadEvents,
            _ => Task.FromException<bool?>(failure),
            (_, _) => Task.CompletedTask,
            cancellation.Token,
            new AgentRunFeedOptions { ResumeDelay = TimeSpan.Zero }));

        Assert.Same(failure, error);
    }

    private static SseFrame DurableFrame(long sequence) =>
        new(sequence, "message", "{}", false, null);
}
