using System.Net.Http;
using System.Net.Sockets;

namespace RuAgent.Client;

/// <summary>Retry behavior for a live run event feed.</summary>
public sealed record AgentRunFeedOptions
{
    /// <summary>Refill the resume budget whenever the stream delivers a frame.</summary>
    public bool ResetResumeBudgetOnFrame { get; init; }

    /// <summary>Whether raw socket failures count as reconnectable transport failures.</summary>
    public bool IncludeSocketExceptions { get; init; }

    /// <summary>Number of reconnect attempts allowed after a stream break or active clean end.</summary>
    public int MaxResumeAttempts { get; init; } = 3;

    /// <summary>Delay before reconnecting.</summary>
    public TimeSpan ResumeDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Creates the final transport failure after the resume budget is exhausted.</summary>
    public Func<Exception, int, AgentApiException>? TransportFailureFactory { get; init; }
}

/// <summary>
/// Follows one run's SSE feed, advancing only the durable cursor and resuming
/// after reconnectable read failures or a clean end while the run is active.
/// </summary>
public static class AgentRunFeed
{
    public static async Task FollowAsync(
        long afterSequence,
        Func<long, CancellationToken, IAsyncEnumerable<SseFrame>> readEventsAsync,
        Func<CancellationToken, Task<bool?>> isRunTerminalAsync,
        Func<SseFrame, CancellationToken, Task> onFrameAsync,
        CancellationToken cancellationToken,
        AgentRunFeedOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(readEventsAsync);
        ArgumentNullException.ThrowIfNull(isRunTerminalAsync);
        ArgumentNullException.ThrowIfNull(onFrameAsync);
        if (afterSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(afterSequence));
        }

        options ??= new AgentRunFeedOptions();
        if (options.MaxResumeAttempts < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Max resume attempts cannot be negative.");
        }
        if (options.ResumeDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Resume delay cannot be negative.");
        }

        var cursor = afterSequence;
        var resumes = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Exception? transportFailure = null;
            await using (var enumerator = readEventsAsync(cursor, cancellationToken)
                             .GetAsyncEnumerator(cancellationToken))
            {
                while (true)
                {
                    bool hasFrame;
                    try
                    {
                        hasFrame = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (IsTransportFailure(ex, options.IncludeSocketExceptions)
                                               && !cancellationToken.IsCancellationRequested)
                    {
                        transportFailure = ex;
                        break;
                    }

                    if (!hasFrame)
                    {
                        break;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    var frame = enumerator.Current;
                    if (frame.DurableSequence is { } sequence && sequence > cursor)
                    {
                        cursor = sequence;
                    }

                    if (options.ResetResumeBudgetOnFrame)
                    {
                        resumes = 0;
                    }

                    // Keep consumer work outside the transport catch. An IOException
                    // from a renderer or dispatcher is a consumer failure, not a reason
                    // to replay the feed.
                    await onFrameAsync(frame, cancellationToken).ConfigureAwait(false);
                }
            }

            if (transportFailure is null)
            {
                bool? terminal;
                try
                {
                    terminal = await isRunTerminalAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // An inconclusive status probe preserves the established behavior:
                    // a cleanly closed feed ends instead of spinning against an
                    // unreachable host.
                    terminal = null;
                }

                if (terminal != false)
                {
                    return;
                }
            }

            resumes++;
            if (resumes > options.MaxResumeAttempts)
            {
                if (transportFailure is not null)
                {
                    throw options.TransportFailureFactory?.Invoke(transportFailure, resumes)
                        ?? new AgentApiException(
                            "stream_interrupted",
                            $"event stream broke: {transportFailure.Message}",
                            inner: transportFailure);
                }

                throw new AgentApiException(
                    "stream_interrupted",
                    "event stream ended before the run reached a terminal state");
            }

            await Task.Delay(options.ResumeDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsTransportFailure(Exception exception, bool includeSocketExceptions) =>
        exception is IOException or HttpRequestException
        || (includeSocketExceptions && exception is SocketException);
}
