using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace RuApp.Services
{
    public sealed class VoicePlaybackService : IDisposable
    {
        private readonly ApiService _apiService;
        private readonly object _replyGate = new();
        private MediaPlayer? _player;
        private InMemoryRandomAccessStream? _stream;
        private CancellationTokenSource? _cts;
        private VoiceStreamClient? _streamClient;
        private VoicePlaybackQueue? _playbackQueue;
        private string? _activeRequestId;
        private CancellationTokenSource? _replyStreamCts;
        private Channel<string>? _replyTextChannel;
        private VoiceReplySentenceBuffer? _replySentenceBuffer;
        private VoiceReplyAudioQueue? _replyAudioQueue;
        private VoiceStreamClient? _replyStreamClient;
        private Task? _replyStreamWorker;
        private VoiceReplyLatencyTracker? _replyLatencyTracker;
        private bool _replyHasInput;
        private bool _vtsSpeaking;
        private bool _disposed;

        private static readonly Regex VoiceMarkerPattern = new(
            @"\s*\[\[(?:VTS|VOICE_PACE):[^\]]*\]\]\s*",
            RegexOptions.Compiled);

        public string VoiceLabel { get; set; } = "Ru (OmniVoice)";
        public double VoiceSpeed { get; set; } = 1.0;
        public bool AutoReadReplies { get; set; } = true;

        public bool IsPlaying => _player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

        public VoicePlaybackService(ApiService apiService)
        {
            _apiService = apiService;
        }

        public async Task RefreshSettingsAsync()
        {
            var voice = await _apiService.GetVoiceStatusAsync();
            if (voice == null) return;
            VoiceLabel = voice.VoiceLabel;
            VoiceSpeed = voice.VoiceSpeed;
            AutoReadReplies = voice.AutoReadReplies;
        }

        public async Task SpeakTextAsync(string text, bool interrupt = true)
        {
            if (_disposed || string.IsNullOrWhiteSpace(text)) return;

            if (interrupt)
            {
                StopVoice();
            }
            else if (_activeRequestId is not null || IsPlaying)
            {
                return;
            }

            string requestId = Guid.NewGuid().ToString("N");
            var cts = new CancellationTokenSource();
            var streamClient = _apiService.CreateVoiceStreamClient();
            var playbackQueue = new VoicePlaybackQueue(PlayChunkAsync);
            _activeRequestId = requestId;
            _cts = cts;
            _streamClient = streamClient;
            _playbackQueue = playbackQueue;

            bool receivedAudio = false;
            VoiceStreamTerminalEvent? terminal = null;
            try
            {
                await playbackQueue.StartAsync(requestId);
                await streamClient.StartAsync(
                    requestId,
                    text.Trim(),
                    VoiceLabel,
                    VoiceSpeed,
                    async chunk =>
                    {
                        receivedAudio = true;
                        await playbackQueue.EnqueueAsync(requestId, chunk, cts.Token);
                    },
                    streamEvent =>
                    {
                        terminal = streamEvent;
                        return Task.CompletedTask;
                    },
                    cts.Token);

                if (terminal?.Type == "ended")
                {
                    await playbackQueue.CompleteAsync(requestId);
                    await ReportPlaybackAsync(requestId, "ended");
                }
                else
                {
                    playbackQueue.Cancel(requestId);
                    if (!receivedAudio && !cts.IsCancellationRequested && IsCurrentRequest(requestId))
                    {
                        await PlayFallbackAsync(text.Trim(), cts.Token);
                    }
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                playbackQueue.Cancel(requestId);
            }
            catch (Exception ex)
            {
                playbackQueue.Cancel(requestId);
                Debug.WriteLine($"[VoicePlayback] Stream failed: {ex.Message}");

                if (receivedAudio && !cts.IsCancellationRequested)
                {
                    await ReportPlaybackAsync(requestId, "failed");
                }

                if (!receivedAudio && !cts.IsCancellationRequested && IsCurrentRequest(requestId))
                {
                    await PlayFallbackAsync(text.Trim(), cts.Token);
                }
            }
            finally
            {
                if (IsCurrentRequest(requestId) && _vtsSpeaking)
                {
                    _vtsSpeaking = false;
                    await ResetVtsSpeakingAsync();
                }
                playbackQueue.Dispose();
                streamClient.Dispose();
                if (IsCurrentRequest(requestId))
                {
                    _playbackQueue = null;
                    _streamClient = null;
                    _activeRequestId = null;
                    _cts = null;
                    cts.Dispose();
                }
                else
                {
                    cts.Dispose();
                }
            }
        }

        public void BeginReplyStream()
        {
            if (_disposed) return;
            StopVoice();
            var latencyTracker = new VoiceReplyLatencyTracker();
            var cts = new CancellationTokenSource();
            var channel = Channel.CreateUnbounded<string>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            var audioQueue = new VoiceReplyAudioQueue(
                (chunk, cancellationToken) =>
                    PlayReplyChunkAsync(chunk, cancellationToken, latencyTracker));
            Task worker = ConsumeReplyStreamAsync(channel.Reader, audioQueue, cts, latencyTracker);
            lock (_replyGate)
            {
                _replyStreamCts = cts;
                _replyTextChannel = channel;
                _replySentenceBuffer = new VoiceReplySentenceBuffer();
                _replyAudioQueue = audioQueue;
                _replyLatencyTracker = latencyTracker;
                _replyHasInput = false;
                _replyStreamWorker = worker;
            }
        }

        public void AppendReplyDelta(string delta)
        {
            if (_disposed || string.IsNullOrEmpty(delta)) return;
            lock (_replyGate)
            {
                var channel = _replyTextChannel;
                var buffer = _replySentenceBuffer;
                if (channel is null || buffer is null) return;

                VoiceReplyLatencyTracker? latencyTracker = _replyLatencyTracker;
                latencyTracker?.MarkFirstChatDelta();
                _replyHasInput = true;
                foreach (string sentence in buffer.Append(delta))
                {
                    if (TryQueueReplySentence(channel.Writer, sentence))
                    {
                        latencyTracker?.MarkFirstPhraseQueued();
                    }
                }
            }
        }

        public async Task CompleteReplyStreamAsync(string fallbackText = "")
        {
            CancellationTokenSource? cts;
            Channel<string>? channel;
            VoiceReplySentenceBuffer? buffer;
            VoiceReplyAudioQueue? audioQueue;
            VoiceReplyLatencyTracker? latencyTracker;
            Task? worker;
            lock (_replyGate)
            {
                cts = _replyStreamCts;
                channel = _replyTextChannel;
                buffer = _replySentenceBuffer;
                audioQueue = _replyAudioQueue;
                latencyTracker = _replyLatencyTracker;
                worker = _replyStreamWorker;
            }
            if (cts is null || channel is null || buffer is null || audioQueue is null || worker is null)
            {
                return;
            }

            lock (_replyGate)
            {
                if (ReferenceEquals(_replyStreamCts, cts) && !_replyHasInput && !string.IsNullOrWhiteSpace(fallbackText))
                {
                    _replyHasInput = true;
                    foreach (string sentence in buffer.Append(fallbackText))
                    {
                        if (TryQueueReplySentence(channel.Writer, sentence))
                        {
                            latencyTracker?.MarkFirstPhraseQueued();
                        }
                    }
                }
                if (ReferenceEquals(_replyStreamCts, cts))
                {
                    foreach (string sentence in buffer.Flush())
                    {
                        if (TryQueueReplySentence(channel.Writer, sentence))
                        {
                            latencyTracker?.MarkFirstPhraseQueued();
                        }
                    }
                    channel.Writer.TryComplete();
                }
            }

            try
            {
                await worker;
                CompleteAndReportReplyLatency(latencyTracker, VoiceReplyOutcome.Completed);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
            catch
            {
                CompleteAndReportReplyLatency(latencyTracker, VoiceReplyOutcome.Failed);
                throw;
            }
            finally
            {
                lock (_replyGate)
                {
                    if (ReferenceEquals(_replyStreamCts, cts))
                    {
                        _replyStreamCts = null;
                        _replyTextChannel = null;
                        _replySentenceBuffer = null;
                        _replyAudioQueue = null;
                        _replyStreamWorker = null;
                        audioQueue.Dispose();
                        cts.Dispose();
                    }
                }
            }
        }

        public void StopVoice()
        {
            string? requestId = _activeRequestId;
            CancellationTokenSource? cts = _cts;
            VoiceStreamClient? streamClient = _streamClient;
            VoicePlaybackQueue? playbackQueue = _playbackQueue;
            CancellationTokenSource? replyCts;
            Channel<string>? replyChannel;
            VoiceReplyAudioQueue? replyAudioQueue;
            VoiceStreamClient? replyStreamClient;
            Task? replyWorker;
            VoiceReplyLatencySnapshot? replyLatencySnapshot;

            _activeRequestId = null;
            _cts = null;
            _streamClient = null;
            _playbackQueue = null;
            lock (_replyGate)
            {
                replyCts = _replyStreamCts;
                replyChannel = _replyTextChannel;
                replyAudioQueue = _replyAudioQueue;
                replyStreamClient = _replyStreamClient;
                replyWorker = _replyStreamWorker;
                replyLatencySnapshot = _replyLatencyTracker?.TryComplete(VoiceReplyOutcome.Cancelled);
                _replyStreamCts = null;
                _replyTextChannel = null;
                _replySentenceBuffer = null;
                _replyAudioQueue = null;
                _replyStreamClient = null;
                _replyStreamWorker = null;
                _replyLatencyTracker = null;
            }

            ReportReplyLatency(replyLatencySnapshot);
            replyCts?.Cancel();
            replyChannel?.Writer.TryComplete();
            replyAudioQueue?.Cancel();
            replyStreamClient?.Dispose();
            if (replyCts is not null && replyWorker is not null)
            {
                _ = ObserveReplyWorkerAsync(replyWorker, replyCts);
            }
            if (requestId is not null)
            {
                playbackQueue?.Cancel(requestId);
                if (streamClient is not null)
                {
                    _ = CancelStreamAsync(streamClient, requestId);
                }
            }

            cts?.Cancel();
            playbackQueue?.Dispose();
            if (_vtsSpeaking)
            {
                _vtsSpeaking = false;
                _ = ResetVtsSpeakingAsync();
            }
            CleanupPlayer();
        }

        private async Task ConsumeReplyStreamAsync(
            ChannelReader<string> reader,
            VoiceReplyAudioQueue audioQueue,
            CancellationTokenSource streamCancellation,
            VoiceReplyLatencyTracker latencyTracker)
        {
            CancellationToken cancellationToken = streamCancellation.Token;
            try
            {
                try
                {
                    await foreach (string sentence in reader.ReadAllAsync(cancellationToken))
                    {
                        await GenerateReplySentenceAsync(
                            sentence,
                            audioQueue,
                            latencyTracker,
                            cancellationToken);
                    }
                }
                finally
                {
                    try
                    {
                        await audioQueue.CompleteAsync();
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch
            {
                CompleteAndReportReplyLatency(latencyTracker, VoiceReplyOutcome.Failed);
                throw;
            }
            finally
            {
                bool isCurrentReply;
                lock (_replyGate)
                {
                    isCurrentReply = ReferenceEquals(_replyStreamCts, streamCancellation);
                }
                if (isCurrentReply && _vtsSpeaking)
                {
                    _vtsSpeaking = false;
                    await ResetVtsSpeakingAsync();
                }
            }
        }

        private async Task GenerateReplySentenceAsync(
            string sentence,
            VoiceReplyAudioQueue audioQueue,
            VoiceReplyLatencyTracker latencyTracker,
            CancellationToken cancellationToken)
        {
            string requestId = Guid.NewGuid().ToString("N");
            var streamClient = _apiService.CreateVoiceStreamClient();
            lock (_replyGate)
            {
                _replyStreamClient = streamClient;
            }
            bool receivedAudio = false;
            try
            {
                await streamClient.StartAsync(
                    requestId,
                    sentence,
                    VoiceLabel,
                    VoiceSpeed,
                    async chunk =>
                    {
                        await audioQueue.EnqueueAsync(chunk, cancellationToken);
                        latencyTracker.MarkFirstAudioReceived();
                        receivedAudio = true;
                    },
                    _ => Task.CompletedTask,
                    cancellationToken);

                if (!receivedAudio && !cancellationToken.IsCancellationRequested)
                {
                    receivedAudio = await EnqueueFallbackReplyAudioAsync(
                        requestId,
                        sentence,
                        audioQueue,
                        latencyTracker,
                        cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VoicePlayback] Reply sentence stream failed: {ex.Message}");
                if (!receivedAudio && !cancellationToken.IsCancellationRequested)
                {
                    receivedAudio = await EnqueueFallbackReplyAudioAsync(
                        requestId,
                        sentence,
                        audioQueue,
                        latencyTracker,
                        cancellationToken);
                }
            }
            finally
            {
                if (!receivedAudio && !cancellationToken.IsCancellationRequested)
                {
                    latencyTracker.MarkSynthesisFailure();
                }
                lock (_replyGate)
                {
                    if (ReferenceEquals(_replyStreamClient, streamClient))
                    {
                        _replyStreamClient = null;
                    }
                }
                streamClient.Dispose();
            }
        }

        private async Task<bool> EnqueueFallbackReplyAudioAsync(
            string requestId,
            string sentence,
            VoiceReplyAudioQueue audioQueue,
            VoiceReplyLatencyTracker latencyTracker,
            CancellationToken cancellationToken)
        {
            byte[]? audio = await _apiService.SynthesizeTtsAsync(sentence, VoiceLabel, VoiceSpeed);
            if (audio is null || audio.Length == 0 || cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            await audioQueue.EnqueueAsync(
                new VoiceAudioChunk(requestId, 0, audio, 24_000, "wav"),
                cancellationToken);
            latencyTracker.MarkFirstAudioReceived();
            return true;
        }

        private Task PlayReplyChunkAsync(
            VoiceAudioChunk chunk,
            CancellationToken cancellationToken,
            VoiceReplyLatencyTracker latencyTracker)
        {
            latencyTracker.MarkPlaybackRequested();
            return PlayWavAsync(chunk.Audio, cancellationToken);
        }

        private static bool TryQueueReplySentence(ChannelWriter<string> writer, string sentence)
        {
            string clean = VoiceMarkerPattern.Replace(sentence, " ").Trim();
            if (!string.IsNullOrWhiteSpace(clean))
            {
                return writer.TryWrite(clean);
            }
            return false;
        }

        private void CompleteAndReportReplyLatency(
            VoiceReplyLatencyTracker? tracker,
            VoiceReplyOutcome outcome)
        {
            if (tracker is null)
            {
                return;
            }

            VoiceReplyLatencySnapshot? snapshot;
            lock (_replyGate)
            {
                snapshot = tracker.TryComplete(outcome);
                if (ReferenceEquals(_replyLatencyTracker, tracker))
                {
                    _replyLatencyTracker = null;
                }
            }
            ReportReplyLatency(snapshot);
        }

        private void ReportReplyLatency(VoiceReplyLatencySnapshot? snapshot)
        {
            if (snapshot is not null)
            {
                _ = Task.Run(() => _apiService.ReportVoiceLatencyAsync(snapshot));
            }
        }

        private static async Task ObserveReplyWorkerAsync(
            Task worker,
            CancellationTokenSource cancellation)
        {
            try
            {
                await worker;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VoicePlayback] Reply stream worker stopped: {ex.Message}");
            }
            finally
            {
                cancellation.Dispose();
            }
        }

        private async Task PlayChunkAsync(VoiceAudioChunk chunk, CancellationToken cancellationToken)
        {
            await PlayWavAsync(chunk.Audio, cancellationToken, chunk.RequestId);
        }

        private async Task PlayFallbackAsync(string text, CancellationToken cancellationToken)
        {
            byte[]? audio = await _apiService.SynthesizeTtsAsync(text, VoiceLabel, VoiceSpeed);
            if (audio is null || audio.Length == 0 || cancellationToken.IsCancellationRequested)
            {
                return;
            }
            await PlayWavAsync(audio, cancellationToken);
        }

        private async Task PlayWavAsync(
            byte[] audio,
            CancellationToken cancellationToken,
            string? requestId = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var player = new MediaPlayer { AutoPlay = false };
            var stream = new InMemoryRandomAccessStream();
            var writer = new DataWriter(stream);
            writer.WriteBytes(audio);
            await writer.StoreAsync();
            writer.DetachStream();
            stream.Seek(0);

            var opened = NewCompletionSource();
            var ended = NewCompletionSource();
            player.MediaOpened += (_, _) => opened.TrySetResult(true);
            player.MediaEnded += (_, _) => ended.TrySetResult(true);
            player.MediaFailed += (_, _) =>
            {
                var error = new InvalidOperationException("The audio chunk could not be played.");
                opened.TrySetException(error);
                ended.TrySetException(error);
            };

            _player = player;
            _stream = stream;
            try
            {
                player.Source = MediaSource.CreateFromStream(stream, "audio/wav");
                await opened.Task.WaitAsync(cancellationToken);
                if (!_vtsSpeaking)
                {
                    bool ok = await _apiService.SetVtsSpeakingAsync(true);
                    if (!ok)
                    {
                        throw new InvalidOperationException("VTS speaking state was not accepted.");
                    }
                    _vtsSpeaking = true;
                    if (requestId is not null)
                    {
                        await ReportPlaybackAsync(requestId, "started");
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                player.Play();
                await ended.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                if (requestId is not null)
                {
                    await ReportPlaybackAsync(requestId, "failed");
                }
                throw;
            }
            finally
            {
                if (ReferenceEquals(_player, player))
                {
                    _player = null;
                }
                if (ReferenceEquals(_stream, stream))
                {
                    _stream = null;
                }
                try
                {
                    player.Pause();
                    player.Source = null;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[VoicePlayback] Player cleanup failed: {ex.Message}");
                }
                player.Dispose();
                stream.Dispose();
            }
        }

        private bool IsCurrentRequest(string requestId) => _activeRequestId == requestId;

        private static TaskCompletionSource<bool> NewCompletionSource()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static async Task CancelStreamAsync(VoiceStreamClient client, string requestId)
        {
            try
            {
                await client.AcknowledgePlaybackAsync(requestId, "cancelled");
                await client.CancelAsync(requestId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VoicePlayback] Stream cancellation failed: {ex.Message}");
            }
        }

        private async Task ReportPlaybackAsync(string requestId, string state)
        {
            VoiceStreamClient? client = _streamClient;
            if (client is null)
            {
                return;
            }
            try
            {
                await client.AcknowledgePlaybackAsync(requestId, state);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VoicePlayback] Playback acknowledgement failed: {ex.Message}");
            }
        }

        private async Task ResetVtsSpeakingAsync()
        {
            try
            {
                await _apiService.SetVtsSpeakingAsync(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VoicePlayback] Reset VTS speaking failed: {ex.Message}");
            }
        }

        private void CleanupPlayer()
        {
            var player = _player;
            _player = null;
            if (player == null) return;
            try
            {
                player.Pause();
                player.Source = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VoicePlayback] Player cleanup failed: {ex.Message}");
            }
            player.Dispose();
            _stream?.Dispose();
            _stream = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopVoice();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            StopVoice();
            if (_vtsSpeaking)
            {
                _vtsSpeaking = false;
                await ResetVtsSpeakingAsync();
            }
        }
    }
}
