using System.Collections.Concurrent;
using System.Text.Json;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class VoiceReplyLatencyTrackerTests
{
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
}
