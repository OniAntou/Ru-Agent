using System.Collections.Concurrent;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class VoiceConversationServiceTests
{
    [Fact]
    public async Task LoopsFromListeningToReplyAndBackToListening()
    {
        var capture = new FakeVoiceTurnCapture();
        var sent = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var states = new ConcurrentQueue<VoiceConversationState>();
        var service = new VoiceConversationService(
            capture,
            (_, _) => Task.FromResult<string?>("  chào Ru  "),
            (text, _) =>
            {
                sent.TrySetResult(text);
                return Task.CompletedTask;
            });
        service.StatusChanged += (_, status) => states.Enqueue(status.State);

        await service.StartAsync();
        await capture.WaitForCaptureAsync(1);
        capture.CompleteNext(new VoiceTurnCaptureResult(new byte[] { 1, 2 }, VoiceTurnStopReason.SpeechEnded));

        Assert.Equal("chào Ru", await sent.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        service.NotifyReplyCompleted();
        await capture.WaitForCaptureAsync(2);

        await service.StopAsync();

        Assert.Contains(VoiceConversationState.Transcribing, states);
        Assert.Contains(VoiceConversationState.Sending, states);
        Assert.Contains(VoiceConversationState.WaitingForReply, states);
        Assert.Contains(VoiceConversationState.Listening, states);
    }

    [Fact]
    public async Task NoSpeechContinuesListeningWithoutTranscription()
    {
        var capture = new FakeVoiceTurnCapture();
        int transcribeCalls = 0;
        var service = new VoiceConversationService(
            capture,
            (_, _) =>
            {
                transcribeCalls++;
                return Task.FromResult<string?>("unexpected");
            },
            (_, _) => Task.CompletedTask);

        await service.StartAsync();
        await capture.WaitForCaptureAsync(1);
        capture.CompleteNext(new VoiceTurnCaptureResult(null, VoiceTurnStopReason.NoSpeech));
        await capture.WaitForCaptureAsync(2);

        Assert.Equal(0, transcribeCalls);
        await service.StopAsync();
    }

    [Fact]
    public async Task StopCancelsCaptureAndLeavesServiceStopped()
    {
        var capture = new FakeVoiceTurnCapture();
        var service = new VoiceConversationService(
            capture,
            (_, _) => Task.FromResult<string?>("unused"),
            (_, _) => Task.CompletedTask);

        await service.StartAsync();
        await capture.WaitForCaptureAsync(1);
        await service.StopAsync();

        Assert.False(service.IsRunning);
        Assert.Equal(VoiceConversationState.Stopped, service.State);
        Assert.Equal(1, capture.StopCalls);
    }

    [Fact]
    public async Task StopPreventsALateTranscriptionFromSending()
    {
        var capture = new FakeVoiceTurnCapture();
        var transcriptionStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lateTranscription = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int sendCalls = 0;
        var service = new VoiceConversationService(
            capture,
            (_, _) =>
            {
                transcriptionStarted.TrySetResult(true);
                return lateTranscription.Task;
            },
            (_, _) =>
            {
                sendCalls++;
                return Task.CompletedTask;
            });

        await service.StartAsync();
        await capture.WaitForCaptureAsync(1);
        capture.CompleteNext(new VoiceTurnCaptureResult(new byte[] { 1 }, VoiceTurnStopReason.SpeechEnded));
        await transcriptionStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await service.StopAsync();
        lateTranscription.TrySetResult("late prompt");
        await Task.Delay(20);

        Assert.Equal(0, sendCalls);
    }

    [Fact]
    public async Task SessionReplacementCancelsLateTranscriptionAndRecordsSafeEvent()
    {
        var capture = new FakeVoiceTurnCapture();
        var transcriptionStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lateTranscription = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var events = new InMemoryHarnessEventSink();
        int sendCalls = 0;
        int playbackCalls = 0;
        var service = new VoiceConversationService(
            capture,
            (_, _) =>
            {
                transcriptionStarted.TrySetResult(true);
                return lateTranscription.Task;
            },
            (_, _) =>
            {
                sendCalls++;
                playbackCalls++;
                return Task.CompletedTask;
            },
            events);

        await service.StartAsync();
        await capture.WaitForCaptureAsync(1);
        capture.CompleteNext(new VoiceTurnCaptureResult(new byte[] { 1 }, VoiceTurnStopReason.SpeechEnded));
        await transcriptionStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await service.ReplaceSessionAsync();
        lateTranscription.TrySetResult("stale prompt");
        await Task.Delay(20);

        Assert.Equal(0, sendCalls);
        Assert.Equal(0, playbackCalls);
        Assert.Equal(1, events.Counts["voice.session.replaced"]);
    }

    private sealed class FakeVoiceTurnCapture : IVoiceTurnCapture
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource<VoiceTurnCaptureResult>> _turns = new();
        private CancellationTokenSource? _activeCancellation;

        public int StopCalls { get; private set; }
        public bool IsCapturing
        {
            get
            {
                lock (_gate)
                {
                    return _turns.Any(turn => !turn.Task.IsCompleted);
                }
            }
        }

        public Task<VoiceTurnCaptureResult> CaptureTurnAsync(CancellationToken cancellationToken)
        {
            var turn = new TaskCompletionSource<VoiceTurnCaptureResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _turns.Add(turn);
                _activeCancellation?.Dispose();
                _activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            }

            return WaitForTurnAsync(turn, _activeCancellation.Token);
        }

        public async Task StopAsync()
        {
            CancellationTokenSource? cancellation;
            lock (_gate)
            {
                StopCalls++;
                cancellation = _activeCancellation;
            }

            cancellation?.Cancel();
            await Task.CompletedTask;
        }

        public Task WaitForCaptureAsync(int count)
        {
            return WaitUntilAsync(() =>
            {
                lock (_gate)
                {
                    return _turns.Count >= count;
                }
            });
        }

        public void CompleteNext(VoiceTurnCaptureResult result)
        {
            lock (_gate)
            {
                _turns.FirstOrDefault(turn => !turn.Task.IsCompleted)?.TrySetResult(result);
            }
        }

        public void Dispose()
        {
            _activeCancellation?.Dispose();
        }

        private static async Task<VoiceTurnCaptureResult> WaitForTurnAsync(
            TaskCompletionSource<VoiceTurnCaptureResult> turn,
            CancellationToken cancellationToken)
        {
            try
            {
                return await turn.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new VoiceTurnCaptureResult(null, VoiceTurnStopReason.Cancelled);
            }
        }

        private static async Task WaitUntilAsync(Func<bool> predicate)
        {
            for (int i = 0; i < 100; i++)
            {
                if (predicate())
                {
                    return;
                }

                await Task.Delay(10);
            }

            throw new TimeoutException("The fake capture source did not receive the expected turn.");
        }
    }
}
