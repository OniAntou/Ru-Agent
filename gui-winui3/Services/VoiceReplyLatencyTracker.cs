using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace RuApp.Services
{
    public enum VoiceReplyOutcome
    {
        Completed,
        Cancelled,
        Failed,
    }

    public sealed record VoiceReplyLatencySnapshot(
        double? SubmitToFirstDeltaMs,
        double? SubmitToFirstPhraseMs,
        double? PhraseToFirstAudioMs,
        double? SubmitToPlaybackRequestedMs,
        VoiceReplyOutcome Outcome)
    {
        public IReadOnlyDictionary<string, object?> ToWirePayload()
        {
            return new Dictionary<string, object?>
            {
                ["submit_to_first_delta_ms"] = SubmitToFirstDeltaMs,
                ["submit_to_first_phrase_ms"] = SubmitToFirstPhraseMs,
                ["phrase_to_first_audio_ms"] = PhraseToFirstAudioMs,
                ["submit_to_playback_requested_ms"] = SubmitToPlaybackRequestedMs,
                ["outcome"] = Outcome switch
                {
                    VoiceReplyOutcome.Completed => "completed",
                    VoiceReplyOutcome.Cancelled => "cancelled",
                    VoiceReplyOutcome.Failed => "failed",
                    _ => throw new ArgumentOutOfRangeException(nameof(Outcome)),
                },
            };
        }
    }

    public sealed class VoiceReplyLatencyTracker
    {
        private readonly object _gate = new();
        private readonly Func<long> _timestampSource;
        private readonly long _ticksPerSecond;
        private readonly long _submitTicks;
        private long? _firstChatDeltaTicks;
        private long? _firstPhraseQueuedTicks;
        private long? _firstAudioReceivedTicks;
        private long? _playbackRequestedTicks;
        private bool _generationFailed;
        private bool _completed;

        public VoiceReplyLatencyTracker(
            Func<long>? timestampSource = null,
            long? ticksPerSecond = null)
        {
            _timestampSource = timestampSource ?? Stopwatch.GetTimestamp;
            _ticksPerSecond = ticksPerSecond ?? Stopwatch.Frequency;
            if (_ticksPerSecond <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(ticksPerSecond));
            }

            _submitTicks = _timestampSource();
        }

        public void MarkFirstChatDelta() => MarkFirst(ref _firstChatDeltaTicks);

        public void MarkFirstPhraseQueued() => MarkFirst(ref _firstPhraseQueuedTicks);

        public void MarkFirstAudioReceived() => MarkFirst(ref _firstAudioReceivedTicks);

        public bool HasAudioReceived
        {
            get
            {
                lock (_gate)
                {
                    return _firstAudioReceivedTicks is not null;
                }
            }
        }

        public void MarkPlaybackRequested() => MarkFirst(ref _playbackRequestedTicks);

        public void MarkSynthesisFailure()
        {
            lock (_gate)
            {
                if (!_completed)
                {
                    _generationFailed = true;
                }
            }
        }

        public VoiceReplyLatencySnapshot? TryComplete(VoiceReplyOutcome outcome)
        {
            lock (_gate)
            {
                if (_completed)
                {
                    return null;
                }

                _completed = true;
                VoiceReplyOutcome terminalOutcome = outcome == VoiceReplyOutcome.Completed &&
                    _generationFailed && _firstAudioReceivedTicks is null
                    ? VoiceReplyOutcome.Failed
                    : outcome;
                return new VoiceReplyLatencySnapshot(
                    ToMilliseconds(_firstChatDeltaTicks, _submitTicks),
                    ToMilliseconds(_firstPhraseQueuedTicks, _submitTicks),
                    ToMilliseconds(_firstAudioReceivedTicks, _firstPhraseQueuedTicks),
                    ToMilliseconds(_playbackRequestedTicks, _submitTicks),
                    terminalOutcome);
            }
        }

        private void MarkFirst(ref long? milestone)
        {
            long timestamp = _timestampSource();
            lock (_gate)
            {
                if (!_completed && milestone is null)
                {
                    milestone = timestamp;
                }
            }
        }

        private double? ToMilliseconds(long? endTicks, long? startTicks)
        {
            if (endTicks is null || startTicks is null)
            {
                return null;
            }

            double milliseconds = (endTicks.Value - startTicks.Value) * 1_000.0 / _ticksPerSecond;
            return Math.Round(milliseconds, 3, MidpointRounding.AwayFromZero);
        }
    }
}
