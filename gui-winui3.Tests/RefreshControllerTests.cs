using System;
using System.Threading;
using System.Threading.Tasks;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class RefreshControllerTests
{
    [Fact]
    public async Task Inactive_refresh_does_not_fetch()
    {
        int calls = 0;
        await using var refresh = new RefreshController<int>(
            _ => { calls++; return Task.FromResult(7); },
            _ => throw new InvalidOperationException("inactive apply"),
            action => action());

        Assert.False(await refresh.RefreshAsync());
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Active_refresh_fetches_and_applies()
    {
        int appliedValue = 0;
        await using var refresh = new RefreshController<int>(
            _ => Task.FromResult(42),
            val => appliedValue = val,
            action => action());

        refresh.SetActive(true);
        Assert.True(await refresh.RefreshAsync());
        Assert.Equal(42, appliedValue);
    }

    [Fact]
    public async Task Single_flight_prevents_overlapping_fetches()
    {
        var fetchTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        int fetchCount = 0;
        int appliedValue = 0;

        await using var refresh = new RefreshController<int>(
            _ =>
            {
                fetchCount++;
                return fetchTcs.Task;
            },
            val => appliedValue = val,
            action => action());

        refresh.SetActive(true);

        var firstTask = refresh.RefreshAsync();
        Assert.Equal(1, fetchCount);

        // Second refresh while first is in flight should immediately return false
        bool secondResult = await refresh.RefreshAsync();
        Assert.False(secondResult);
        Assert.Equal(1, fetchCount);

        fetchTcs.SetResult(99);
        bool firstResult = await firstTask;
        Assert.True(firstResult);
        Assert.Equal(99, appliedValue);
    }

    [Fact]
    public async Task Deactivate_cancels_in_flight_fetch_and_discards_result()
    {
        var cancelledTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int appliedValue = 0;

        await using var refresh = new RefreshController<int>(
            async ct =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    cancelledTcs.TrySetResult(true);
                    throw;
                }
                return 123;
            },
            val => appliedValue = val,
            action => action());

        refresh.SetActive(true);
        var refreshTask = refresh.RefreshAsync();

        refresh.SetActive(false);

        Assert.True(await cancelledTcs.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        bool result = await refreshTask;
        Assert.False(result);
        Assert.Equal(0, appliedValue);
    }

    [Fact]
    public async Task Deactivate_after_dispatch_suppresses_apply()
    {
        Action? queuedDispatch = null;
        int appliedValue = 0;

        await using var refresh = new RefreshController<int>(
            _ => Task.FromResult(55),
            val => appliedValue = val,
            action => queuedDispatch = action);

        refresh.SetActive(true);
        var refreshTask = refresh.RefreshAsync();

        // Fetch is done, dispatch delegate was captured but not executed yet
        Assert.NotNull(queuedDispatch);

        // Deactivate before dispatch runs
        refresh.SetActive(false);

        // Now run the queued dispatch
        queuedDispatch();

        Assert.Equal(0, appliedValue);
        Assert.False(await refreshTask);
    }

    [Fact]
    public async Task Deferred_dispatch_completes_true_only_after_current_apply()
    {
        Action? queuedDispatch = null;
        int appliedValue = 0;

        await using var refresh = new RefreshController<int>(
            _ => Task.FromResult(55),
            value => appliedValue = value,
            action => queuedDispatch = action);

        refresh.SetActive(true);
        var refreshTask = refresh.RefreshAsync();

        Assert.NotNull(queuedDispatch);
        Assert.False(refreshTask.IsCompleted);
        queuedDispatch!();

        Assert.True(await refreshTask);
        Assert.Equal(55, appliedValue);
    }

    [Fact]
    public async Task Dispose_waits_for_a_started_apply_to_exit_before_completing()
    {
        Action? queuedDispatch = null;
        var applyStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseApply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applyExited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var refresh = new RefreshController<int>(
            _ => Task.FromResult(55),
            _ =>
            {
                applyStarted.TrySetResult(true);
                try { releaseApply.Task.GetAwaiter().GetResult(); }
                finally { applyExited.TrySetResult(true); }
            },
            action => queuedDispatch = action);

        refresh.SetActive(true);
        var refreshTask = refresh.RefreshAsync();
        Assert.NotNull(queuedDispatch);

        var applyTask = Task.Run(() => queuedDispatch!());
        await applyStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var disposeTask = refresh.DisposeAsync().AsTask();

        Assert.False(disposeTask.IsCompleted);
        Assert.False(refreshTask.IsCompleted);
        Assert.False(applyExited.Task.IsCompleted);

        releaseApply.TrySetResult(true);
        await Task.WhenAll(applyTask, refreshTask, disposeTask).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Cancellation_ignoring_fetch_keeps_dispose_pending_and_prevents_late_apply()
    {
        var fetchRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetchStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int applied = 0;

        var refresh = new RefreshController<int>(
            async _ =>
            {
                fetchStarted.TrySetResult(true);
                await fetchRelease.Task;
                return 7;
            },
            _ => Interlocked.Increment(ref applied),
            action => action());

        refresh.SetActive(true);
        var refreshTask = refresh.RefreshAsync();
        await fetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        refresh.SetActive(false);
        refresh.SetActive(true);

        var disposeTask = refresh.DisposeAsync().AsTask();
        Assert.False(disposeTask.IsCompleted);
        fetchRelease.TrySetResult(true);

        await Task.WhenAll(refreshTask, disposeTask).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, Volatile.Read(ref applied));
    }

    [Fact]
    public async Task Rejected_dispatcher_is_propagated_and_allows_retry()
    {
        int dispatchAttempts = 0;
        await using var refresh = new RefreshController<int>(
            _ => Task.FromResult(1),
            _ => { },
            action =>
            {
                var accepted = ++dispatchAttempts > 1;
                if (!accepted)
                {
                    throw new OperationCanceledException("dispatcher rejected enqueue");
                }

                action();
            });

        refresh.SetActive(true);
        Assert.False(await refresh.RefreshAsync());
        Assert.True(await refresh.RefreshAsync());
    }

    [Fact]
    public async Task Apply_exception_is_propagated_and_admission_allows_retry()
    {
        int attempt = 0;
        await using var refresh = new RefreshController<int>(
            _ => Task.FromResult(1),
            _ =>
            {
                if (++attempt == 1)
                {
                    throw new InvalidOperationException("apply failed");
                }
            },
            action => action());

        refresh.SetActive(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => refresh.RefreshAsync());
        Assert.True(await refresh.RefreshAsync());
    }

    [Fact]
    public async Task Dispatch_exception_is_propagated_and_admission_allows_retry()
    {
        int attempt = 0;
        await using var refresh = new RefreshController<int>(
            _ => Task.FromResult(1),
            _ => { },
            action =>
            {
                if (++attempt == 1)
                {
                    throw new InvalidOperationException("dispatch failed");
                }

                action();
            });

        refresh.SetActive(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => refresh.RefreshAsync());
        Assert.True(await refresh.RefreshAsync());
    }

    [Fact]
    public async Task Dispose_waits_for_the_post_fetch_dispatch_operation()
    {
        var dispatchEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDispatch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int appliedValue = 0;

        var refresh = new RefreshController<int>(
            _ => Task.FromResult(55),
            value => appliedValue = value,
            action =>
            {
                dispatchEntered.TrySetResult(true);
                releaseDispatch.Task.GetAwaiter().GetResult();
                action();
            });

        refresh.SetActive(true);
        var refreshTask = Task.Run(() => refresh.RefreshAsync());
        await dispatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var disposeTask = refresh.DisposeAsync().AsTask();
        Assert.False(disposeTask.IsCompleted);

        releaseDispatch.TrySetResult(true);
        try
        {
            await Task.WhenAll(refreshTask, disposeTask).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            releaseDispatch.TrySetResult(true);
        }

        Assert.Equal(0, appliedValue);
    }

    [Fact]
    public async Task Reactivate_discards_stale_result_from_prior_generation()
    {
        var slowFetchTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        int appliedValue = 0;

        await using var refresh = new RefreshController<int>(
            _ => slowFetchTcs.Task,
            val => appliedValue = val,
            action => action());

        refresh.SetActive(true);
        var firstTask = refresh.RefreshAsync();

        // Deactivate then reactivate (generation increases)
        refresh.SetActive(false);
        refresh.SetActive(true);

        // Resolve stale fetch
        slowFetchTcs.SetResult(777);
        bool firstResult = await firstTask;

        Assert.False(firstResult);
        Assert.Equal(0, appliedValue);
    }

    [Fact]
    public async Task Fetch_exception_resets_busy_state_for_subsequent_retry()
    {
        int attempt = 0;
        int appliedValue = 0;

        await using var refresh = new RefreshController<int>(
            _ =>
            {
                attempt++;
                if (attempt == 1)
                {
                    throw new InvalidOperationException("network error");
                }
                return Task.FromResult(100);
            },
            val => appliedValue = val,
            action => action());

        refresh.SetActive(true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => refresh.RefreshAsync());

        // Subsequent retry should work
        bool retryResult = await refresh.RefreshAsync();
        Assert.True(retryResult);
        Assert.Equal(100, appliedValue);
    }

    [Fact]
    public async Task Dispose_cancels_and_closes_admission()
    {
        var fetchStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetchCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var refresh = new RefreshController<int>(
            async ct =>
            {
                fetchStarted.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    fetchCancelled.TrySetResult(true);
                    throw;
                }
                return 1;
            },
            _ => { },
            action => action());

        refresh.SetActive(true);
        var task = refresh.RefreshAsync();

        await fetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await refresh.DisposeAsync();

        Assert.True(await fetchCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(await task);
        Assert.False(await refresh.RefreshAsync());
    }
}
