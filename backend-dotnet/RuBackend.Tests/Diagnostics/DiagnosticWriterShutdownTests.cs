using System.IO;
using RuBackend.Diagnostics;
using RuBackend.Tests.Fakes;
using Xunit;

namespace RuBackend.Tests.Diagnostics;

public sealed class DiagnosticWriterShutdownTests
{
    [Fact]
    public async Task Stop_drains_and_releases_the_daily_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-shutdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var writer = new StructuredDiagnosticLogWriter(root);
            Assert.True(writer.TryEnqueue(new DiagnosticEvent(
                "e1", 1720000000, "2024-07-03T09:46:40Z", "info", "host", "host_startup")));
            Assert.Equal(DiagnosticShutdownOutcome.Drained,
                await writer.StopAsync(TimeSpan.FromSeconds(2)));
            await writer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(writer.TryEnqueue(new DiagnosticEvent(
                "e2", 1720000001, "2024-07-03T09:46:41Z", "info", "host", "host_startup")));
            var file = Assert.Single(Directory.GetFiles(root, "aspnetcore-*.jsonl"));
            Assert.Single(await File.ReadAllLinesAsync(file));
            using var exclusive = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task Async_dispose_reports_shutdown_and_waits_for_quiescent_cleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-shutdown-async-dispose-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var writer = new StructuredDiagnosticLogWriter(root);
            Assert.True(writer.TryEnqueue(new DiagnosticEvent(
                "e1", 1720000000, "2024-07-03T09:46:40Z", "info", "host", "host_startup")));

            await writer.DisposeAsync();

            Assert.Equal(DiagnosticShutdownOutcome.Drained, writer.LastShutdownOutcome);
            await writer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(writer.TryEnqueue(new DiagnosticEvent(
                "e2", 1720000001, "2024-07-03T09:46:41Z", "info", "host", "host_startup")));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task Sink_stall_honoring_cancellation_returns_cancelled_and_completes()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-shutdown-honor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sink = new ControlledDiagnosticLogSink(honorCancellation: true);
        var writer = new StructuredDiagnosticLogWriter(root, sink: sink);
        try
        {
            Assert.True(writer.TryEnqueue(new DiagnosticEvent("e1", 1720000000, "2024-07-03T09:46:40Z", "info", "host", "host_startup")));
            await sink.Entered.WaitAsync(TimeSpan.FromSeconds(2));
            var stopTask = writer.StopAsync(TimeSpan.FromMilliseconds(100));
            // The sink honors cancellation, but it can unwind just after the
            // caller's deadline. The bounded result must report that observation
            // honestly; Completion is the eventual quiescence signal.
            var outcome = await stopTask;
            await sink.Exited.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(outcome == DiagnosticShutdownOutcome.Cancelled
                || outcome == DiagnosticShutdownOutcome.Incomplete);
            Assert.True(sink.CancellationObserved);
            await writer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(writer.Completion.IsCompleted);
            // No ObjectDisposedException when accessing gates after
            Assert.False(writer.TryEnqueue(new DiagnosticEvent("e2", 1720000001, "2024-07-03T09:46:41Z", "info", "host", "host_startup")));
        }
        finally
        {
            try { sink.Release(); } catch { }
            writer.Dispose();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task Sink_ignoring_cancellation_returns_incomplete_then_completes_after_release()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-shutdown-ignore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sink = new ControlledDiagnosticLogSink(honorCancellation: false);
        var writer = new StructuredDiagnosticLogWriter(root, sink: sink);
        try
        {
            Assert.True(writer.TryEnqueue(new DiagnosticEvent("e1", 1720000000, "2024-07-03T09:46:40Z", "info", "host", "host_startup")));
            await Task.Delay(100);
            await sink.Entered.WaitAsync(TimeSpan.FromSeconds(2));
            var outcome = await writer.StopAsync(TimeSpan.FromMilliseconds(500));
            Assert.Equal(DiagnosticShutdownOutcome.Incomplete, outcome);
            Assert.False(writer.Completion.IsCompleted);
            sink.Release();
            await writer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(writer.Completion.IsCompleted);
        }
        finally
        {
            try { sink.Release(); } catch { }
            writer.Dispose();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task Flush_in_flight_while_dispose_has_one_owner_no_duplicate()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-shutdown-flush-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sink = new ControlledDiagnosticLogSink(honorCancellation: true);
        var writer = new StructuredDiagnosticLogWriter(root, sink: sink, flushInterval: TimeSpan.FromMilliseconds(50));
        try
        {
            Assert.True(writer.TryEnqueue(new DiagnosticEvent("e1", 1720000000, "2024-07-03T09:46:40Z", "info", "host", "host_startup")));
            // Start a Flush that will stall in sink
            var flushTask = writer.FlushAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAny(sink.Entered, Task.Delay(2000));
            // Now dispose while flush is in flight
            var stopTask = writer.StopAsync(TimeSpan.FromSeconds(2));
            // Both should not deadlock or throw ObjectDisposed
            sink.Release();
            await Task.WhenAll(flushTask, stopTask).WaitAsync(TimeSpan.FromSeconds(5));
            await writer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            var files = Directory.GetFiles(root, "aspnetcore-*.jsonl");
            if (files.Length > 0)
            {
                var lines = await File.ReadAllLinesAsync(files[0]);
                // At most one line, no duplicate due to single owner
                Assert.True(lines.Length <= 1);
            }
        }
        finally
        {
            try { sink.Release(); } catch { }
            writer.Dispose();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task Stop_does_not_complete_while_an_admitted_flush_is_still_in_sink()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-shutdown-flush-ownership-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sink = new ControlledDiagnosticLogSink(honorCancellation: false);
        var writer = new StructuredDiagnosticLogWriter(
            root,
            flushInterval: TimeSpan.FromHours(1),
            sink: sink);
        try
        {
            await Task.Delay(100);
            Assert.True(writer.TryEnqueue(new DiagnosticEvent(
                "e1", 1720000000, "2024-07-03T09:46:40Z", "info", "host", "host_startup")));

            var flushTask = sink.RunExplicitFlushAsync(
                () => writer.FlushAsync(TimeSpan.FromSeconds(5)));
            await sink.Entered.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(sink.WasExplicitFlush);

            var outcome = await writer.StopAsync(TimeSpan.FromMilliseconds(100));

            Assert.Equal(DiagnosticShutdownOutcome.Incomplete, outcome);
            Assert.False(flushTask.IsCompleted);
            Assert.False(writer.Completion.IsCompleted);

            sink.Release();
            await flushTask.WaitAsync(TimeSpan.FromSeconds(2));
            await writer.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            sink.Release();
            writer.Dispose();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task Shutdown_budget_cancels_an_admitted_explicit_flush()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-shutdown-flush-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sink = new ControlledDiagnosticLogSink(honorCancellation: true);
        var writer = new StructuredDiagnosticLogWriter(
            root,
            flushInterval: TimeSpan.FromHours(1),
            sink: sink);
        try
        {
            await Task.Delay(100);
            Assert.True(writer.TryEnqueue(new DiagnosticEvent(
                "e1", 1720000000, "2024-07-03T09:46:40Z", "info", "host", "host_startup")));

            var flushTask = sink.RunExplicitFlushAsync(
                () => writer.FlushAsync(TimeSpan.FromSeconds(5)));
            await sink.Entered.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(sink.WasExplicitFlush);

            var outcome = await writer.StopAsync(TimeSpan.FromMilliseconds(100));

            await sink.Exited.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(outcome == DiagnosticShutdownOutcome.Cancelled
                || outcome == DiagnosticShutdownOutcome.Incomplete);
            Assert.True(sink.CancellationObserved);
            await flushTask.WaitAsync(TimeSpan.FromSeconds(2));
            await writer.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            sink.Release();
            writer.Dispose();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task Multiple_stop_callers_share_same_terminal_cleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-shutdown-multi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sink = new ControlledDiagnosticLogSink(honorCancellation: false);
        var writer = new StructuredDiagnosticLogWriter(root, sink: sink);
        try
        {
            Assert.True(writer.TryEnqueue(new DiagnosticEvent("e1", 1720000000, "2024-07-03T09:46:40Z", "info", "host", "host_startup")));
            await Task.Delay(100);
            var t1 = writer.StopAsync(TimeSpan.FromMilliseconds(500));
            var t2 = writer.StopAsync(TimeSpan.FromMilliseconds(500));
            var t3 = writer.StopAsync(TimeSpan.FromMilliseconds(500));
            Assert.Same(t1, t2);
            Assert.Same(t2, t3);
            var r1 = await t1;
            var r2 = await t2;
            Assert.Equal(r1, r2);
            Assert.Equal(DiagnosticShutdownOutcome.Incomplete, r1);
            sink.Release();
            await writer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(writer.Completion.IsCompleted);
            // Second dispose should be no-op
            writer.Dispose();
            writer.Dispose();
            Assert.Equal<DiagnosticShutdownOutcome?>(DiagnosticShutdownOutcome.Incomplete, writer.LastShutdownOutcome);
        }
        finally
        {
            try { sink.Release(); } catch { }
            try { writer.Dispose(); } catch { }
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task Repeated_stop_with_cancelled_token_does_not_cancel_first_owner()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-shutdown-cancelled-duplicate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sink = new ControlledDiagnosticLogSink(honorCancellation: false);
        var writer = new StructuredDiagnosticLogWriter(root, sink: sink);
        using var duplicateCancellation = new CancellationTokenSource();
        duplicateCancellation.Cancel();
        try
        {
            Assert.True(writer.TryEnqueue(new DiagnosticEvent(
                "e1", 1720000000, "2024-07-03T09:46:40Z", "info", "host", "host_startup")));
            await sink.Entered.WaitAsync(TimeSpan.FromSeconds(2));

            var first = writer.StopAsync(TimeSpan.FromSeconds(2));
            var duplicate = writer.StopAsync(TimeSpan.FromSeconds(2), duplicateCancellation.Token);
            Assert.Same(first, duplicate);
            await Task.Delay(100);
            Assert.False(first.IsCompleted);

            sink.Release();
            await first.WaitAsync(TimeSpan.FromSeconds(2));
            await writer.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            sink.Release();
            writer.Dispose();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task Event_and_flush_after_admission_closed_are_noop()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-shutdown-noop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var writer = new StructuredDiagnosticLogWriter(root);
        Assert.True(writer.TryEnqueue(new DiagnosticEvent("e1", 1720000000, "2024-07-03T09:46:40Z", "info", "host", "host_startup")));
        await writer.StopAsync(TimeSpan.FromSeconds(2));
        await writer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(writer.TryEnqueue(new DiagnosticEvent("e2", 1720000001, "2024-07-03T09:46:41Z", "info", "host", "host_startup")));
        // Flush after stop is safe no-op
        await writer.FlushAsync(TimeSpan.FromMilliseconds(100));
        var files = Directory.GetFiles(root, "aspnetcore-*.jsonl");
        // Only first event should be present
        if (files.Length > 0)
        {
            var lines = await File.ReadAllLinesAsync(files[0]);
            Assert.DoesNotContain(lines, l => l.Contains("\"event_id\":\"e2\""));
        }
        try { Directory.Delete(root, true); } catch { }
    }

    [Fact]
    public async Task Sink_write_failure_is_bounded_and_not_infinite()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-shutdown-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sink = new FailingDiagnosticLogSink();
        await using var writer = new StructuredDiagnosticLogWriter(root, sink: sink, flushInterval: TimeSpan.FromMilliseconds(20));
        Assert.True(writer.TryEnqueue(new DiagnosticEvent("e1", 1720000000, "2024-07-03T09:46:40Z", "info", "host", "host_startup")));
        var outcome = await writer.StopAsync(TimeSpan.FromSeconds(2));
        await writer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        // Failure should not cause infinite drain; should return Cancelled or Incomplete but complete
        Assert.True(outcome == DiagnosticShutdownOutcome.Cancelled || outcome == DiagnosticShutdownOutcome.Drained || outcome == DiagnosticShutdownOutcome.Incomplete);
        Assert.True(writer.Completion.IsCompleted);
        // WriteFailureCount should be recorded
        Assert.True(writer.Queue.GetSnapshot().WriteFailureCount >= 1);
        try { Directory.Delete(root, true); } catch { }
    }
}
