using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class VoiceReplyLatencyTrackerTests
{
    [Fact]
    public async Task TerminalFailureClearsTheReplyStreamAndReportsFailedExactlyOnce()
    {
        using var capture = new VoiceLatencyCaptureServer();
        using var api = capture.CreateApiService();
        using var service = new VoicePlaybackService(api);
        service.BeginReplyStream();
        service.AppendReplyDelta("stale pending reply");

        service.FailReplyStream();
        await service.CompleteReplyStreamAsync();

        VoiceReplyLatencySnapshot snapshot = await capture.WaitForReportAsync();
        Assert.Equal(VoiceReplyOutcome.Failed, snapshot.Outcome);
        Assert.NotNull(snapshot.SubmitToFirstDeltaMs);
        Assert.Equal(1, capture.ReportCount);
    }

    [Fact]
    public async Task CompletingAnEmptyReplyReportsFailedInsteadOfCompleted()
    {
        using var capture = new VoiceLatencyCaptureServer();
        using var api = capture.CreateApiService();
        using var service = new VoicePlaybackService(api);
        service.BeginReplyStream();

        await service.CompleteReplyStreamAsync();

        VoiceReplyLatencySnapshot snapshot = await capture.WaitForReportAsync();
        Assert.Equal(VoiceReplyOutcome.Failed, snapshot.Outcome);
        Assert.Equal(1, capture.ReportCount);
    }

    [Fact]
    public async Task RetryResetDiscardsPendingTextAndPreservesOneLatencyTracker()
    {
        using var capture = new VoiceLatencyCaptureServer();
        using var api = capture.CreateApiService();
        using var service = new VoicePlaybackService(api);
        service.BeginReplyStream();
        service.AppendReplyDelta("stale pending reply");

        service.ResetReplyStreamForRetry();
        service.AppendReplyDelta("[[VTS:neutral]]");
        await service.CompleteReplyStreamAsync();

        VoiceReplyLatencySnapshot snapshot = await capture.WaitForReportAsync();
        Assert.Equal(VoiceReplyOutcome.Failed, snapshot.Outcome);
        Assert.NotNull(snapshot.SubmitToFirstDeltaMs);
        Assert.Null(snapshot.SubmitToFirstPhraseMs);
        Assert.Equal(1, capture.ReportCount);
    }

    [Fact]
    public async Task RetryResetRestartsPipelineAfterWorkerFailureDetachedTheTracker()
    {
        using var capture = new VoiceLatencyCaptureServer();
        using var api = capture.CreateApiService();
        using var service = new VoicePlaybackService(api);
        service.BeginReplyStream();

        // A reply-worker fault terminalizes and detaches this tracker before
        // the hosted provider can issue its retry reset event.
        FieldInfo trackerField = Assert.IsAssignableFrom<FieldInfo>(typeof(VoicePlaybackService)
            .GetField("_replyLatencyTracker", BindingFlags.Instance | BindingFlags.NonPublic));
        trackerField.SetValue(service, null);

        service.ResetReplyStreamForRetry();
        service.AppendReplyDelta("[[VTS:neutral]]");
        await service.CompleteReplyStreamAsync();

        VoiceReplyLatencySnapshot snapshot = await capture.WaitForReportAsync();
        Assert.Equal(VoiceReplyOutcome.Failed, snapshot.Outcome);
        Assert.Equal(1, capture.ReportCount);
    }

    [Fact]
    public void TrackerBuildsNumericOnlySnapshot()
    {
        long[] ticks = [0, 10, 20, 45, 60];
        int index = 0;
        var tracker = new VoiceReplyLatencyTracker(
            () => ticks[index++],
            ticksPerSecond: 1_000);

        tracker.MarkFirstChatDelta();
        tracker.MarkFirstPhraseQueued();
        tracker.MarkFirstAudioReceived();
        tracker.MarkPlaybackRequested();

        VoiceReplyLatencySnapshot snapshot = Assert.IsType<VoiceReplyLatencySnapshot>(
            tracker.TryComplete(VoiceReplyOutcome.Completed));

        Assert.Equal(10, snapshot.SubmitToFirstDeltaMs);
        Assert.Equal(20, snapshot.SubmitToFirstPhraseMs);
        Assert.Equal(25, snapshot.PhraseToFirstAudioMs);
        Assert.Equal(60, snapshot.SubmitToPlaybackRequestedMs);
        Assert.Equal(VoiceReplyOutcome.Completed, snapshot.Outcome);

        string payload = JsonSerializer.Serialize(snapshot.ToWirePayload());
        using JsonDocument document = JsonDocument.Parse(payload);
        Assert.Equal(
            new[]
            {
                "outcome",
                "phrase_to_first_audio_ms",
                "submit_to_first_delta_ms",
                "submit_to_first_phrase_ms",
                "submit_to_playback_requested_ms",
            },
            document.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal("completed", document.RootElement.GetProperty("outcome").GetString());
        Assert.DoesNotContain("text", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("request_id", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TrackerKeepsOnlyTheFirstMilestoneTimestamp()
    {
        long[] ticks = [100, 110, 115, 120, 125, 130, 135, 140, 145];
        int index = 0;
        var tracker = new VoiceReplyLatencyTracker(
            () => ticks[index++],
            ticksPerSecond: 1_000);

        tracker.MarkFirstChatDelta();
        tracker.MarkFirstChatDelta();
        tracker.MarkFirstPhraseQueued();
        tracker.MarkFirstPhraseQueued();
        tracker.MarkFirstAudioReceived();
        tracker.MarkFirstAudioReceived();
        tracker.MarkPlaybackRequested();
        tracker.MarkPlaybackRequested();

        VoiceReplyLatencySnapshot snapshot = Assert.IsType<VoiceReplyLatencySnapshot>(
            tracker.TryComplete(VoiceReplyOutcome.Completed));

        Assert.Equal(10, snapshot.SubmitToFirstDeltaMs);
        Assert.Equal(20, snapshot.SubmitToFirstPhraseMs);
        Assert.Equal(10, snapshot.PhraseToFirstAudioMs);
        Assert.Equal(40, snapshot.SubmitToPlaybackRequestedMs);
    }

    [Fact]
    public void TrackerReturnsOnlyOneTerminalSnapshot()
    {
        var tracker = new VoiceReplyLatencyTracker(() => 0, ticksPerSecond: 1_000);

        VoiceReplyLatencySnapshot first = Assert.IsType<VoiceReplyLatencySnapshot>(
            tracker.TryComplete(VoiceReplyOutcome.Cancelled));

        Assert.Equal(VoiceReplyOutcome.Cancelled, first.Outcome);
        Assert.Null(tracker.TryComplete(VoiceReplyOutcome.Completed));
        Assert.Null(tracker.TryComplete(VoiceReplyOutcome.Failed));
    }

    [Fact]
    public void TrackerReportsFailedWhenGenerationFailsBeforeFirstAudio()
    {
        var tracker = new VoiceReplyLatencyTracker(() => 0, ticksPerSecond: 1_000);

        tracker.MarkSynthesisFailure();

        VoiceReplyLatencySnapshot snapshot = Assert.IsType<VoiceReplyLatencySnapshot>(
            tracker.TryComplete(VoiceReplyOutcome.Completed));

        Assert.Equal(VoiceReplyOutcome.Failed, snapshot.Outcome);
    }

    [Fact]
    public void TrackerKeepsCompletedWhenLaterGenerationFailsAfterAudio()
    {
        long[] ticks = [0, 10];
        int index = 0;
        var tracker = new VoiceReplyLatencyTracker(
            () => ticks[index++],
            ticksPerSecond: 1_000);

        tracker.MarkFirstAudioReceived();
        tracker.MarkSynthesisFailure();

        VoiceReplyLatencySnapshot snapshot = Assert.IsType<VoiceReplyLatencySnapshot>(
            tracker.TryComplete(VoiceReplyOutcome.Completed));

        Assert.Equal(VoiceReplyOutcome.Completed, snapshot.Outcome);
    }

    [Fact]
    public void TrackerReportsExactlyOnceWhenTerminalOutcomesRace()
    {
        var tracker = new VoiceReplyLatencyTracker(() => 0, ticksPerSecond: 1_000);
        var snapshots = new ConcurrentBag<VoiceReplyLatencySnapshot>();

        Parallel.ForEach(
            new[]
            {
                VoiceReplyOutcome.Completed,
                VoiceReplyOutcome.Cancelled,
                VoiceReplyOutcome.Failed,
            },
            outcome =>
            {
                VoiceReplyLatencySnapshot? snapshot = tracker.TryComplete(outcome);
                if (snapshot is not null)
                {
                    snapshots.Add(snapshot);
                }
            });

        Assert.Single(snapshots);
    }

    private sealed class VoiceLatencyCaptureServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly TaskCompletionSource<VoiceReplyLatencySnapshot> _report = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reportCount;

        public VoiceLatencyCaptureServer()
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            Port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _ = CaptureAsync();
        }

        public int Port { get; }

        public int ReportCount => Volatile.Read(ref _reportCount);

        public ApiService CreateApiService() => new("test-secret", Port);

        public async Task<VoiceReplyLatencySnapshot> WaitForReportAsync() =>
            await _report.Task.WaitAsync(TimeSpan.FromSeconds(2));

        public void Dispose()
        {
            _listener.Close();
        }

        private async Task CaptureAsync()
        {
            try
            {
                HttpListenerContext context = await _listener.GetContextAsync();
                using var document = await JsonDocument.ParseAsync(context.Request.InputStream);
                Interlocked.Increment(ref _reportCount);
                JsonElement body = document.RootElement;
                string? outcome = body.GetProperty("outcome").GetString();
                _report.TrySetResult(new VoiceReplyLatencySnapshot(
                    ReadMilliseconds(body, "submit_to_first_delta_ms"),
                    ReadMilliseconds(body, "submit_to_first_phrase_ms"),
                    ReadMilliseconds(body, "phrase_to_first_audio_ms"),
                    ReadMilliseconds(body, "submit_to_playback_requested_ms"),
                    outcome switch
                    {
                        "completed" => VoiceReplyOutcome.Completed,
                        "cancelled" => VoiceReplyOutcome.Cancelled,
                        _ => VoiceReplyOutcome.Failed,
                    }));
                context.Response.StatusCode = 200;
                context.Response.Close();
            }
            catch (HttpListenerException) when (!_listener.IsListening)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static double? ReadMilliseconds(JsonElement body, string propertyName)
        {
            return body.TryGetProperty(propertyName, out JsonElement value) &&
                value.ValueKind == JsonValueKind.Number
                ? value.GetDouble()
                : null;
        }
    }
}
