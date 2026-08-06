using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace RuApp.Services
{
    public class VoicePlaybackService : IDisposable
    {
        private readonly ApiService _apiService;
        private MediaPlayer? _player;
        private InMemoryRandomAccessStream? _stream;
        private CancellationTokenSource? _cts;
        private bool _vtsSpeaking;

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
            if (string.IsNullOrWhiteSpace(text)) return;

            if (interrupt)
            {
                StopVoice();
            }
            else if (IsPlaying)
            {
                return;
            }

            var audio =             await _apiService.SynthesizeTtsAsync(text.Trim(), VoiceLabel, VoiceSpeed);
            if (audio == null || audio.Length == 0) return;

            var cts = new CancellationTokenSource();
            _cts = cts;
            var player = new MediaPlayer();
            player.AutoPlay = false;
            _player = player;

            player.MediaOpened += async (_, _) =>
            {
                try
                {
                    bool ok = await _apiService.SetVtsSpeakingAsync(true);
                    Debug.WriteLine($"[VoicePlayback] MediaOpened SetVtsSpeakingAsync={ok}");
                    if (!ok || cts.IsCancellationRequested || !ReferenceEquals(_player, player))
                    {
                        await _apiService.SetVtsSpeakingAsync(false);
                        return;
                    }
                    _vtsSpeaking = true;
                    player.Play();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[VoicePlayback] MediaOpened failed: {ex.Message}");
                }
            };
            player.MediaEnded += async (_, _) =>
            {
                try
                {
                    _vtsSpeaking = false;
                    bool ok = await _apiService.SetVtsSpeakingAsync(false);
                    Debug.WriteLine($"[VoicePlayback] MediaEnded SetVtsSpeakingAsync={ok}");
                    CleanupPlayer();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[VoicePlayback] MediaEnded failed: {ex.Message}");
                }
            };
            player.MediaFailed += async (_, _) =>
            {
                try
                {
                    _vtsSpeaking = false;
                    bool ok = await _apiService.SetVtsSpeakingAsync(false);
                    Debug.WriteLine($"[VoicePlayback] MediaFailed SetVtsSpeakingAsync={ok}");
                    CleanupPlayer();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[VoicePlayback] MediaFailed failed: {ex.Message}");
                }
            };

            var stream = new InMemoryRandomAccessStream();
            var writer = new DataWriter(stream);
            writer.WriteBytes(audio);
            await writer.StoreAsync();
            writer.DetachStream();
            stream.Seek(0);

            _stream?.Dispose();
            _stream = stream;
            player.Source = MediaSource.CreateFromStream(stream, "audio/wav");
        }

        public void StopVoice()
        {
            _cts?.Cancel();
            _cts = null;
            if (_vtsSpeaking)
            {
                _vtsSpeaking = false;
                _ = ResetVtsSpeakingAsync();
            }
            CleanupPlayer();
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
            if (player == null) return;
            _player = null;
            try
            {
                player.Pause();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VoicePlayback] Pause failed: {ex.Message}");
            }
            try
            {
                player.Source = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VoicePlayback] Clear source failed: {ex.Message}");
            }
            player.Dispose();
            _stream?.Dispose();
            _stream = null;
        }

        public void Dispose()
        {
            StopVoice();
        }

        public async ValueTask DisposeAsync()
        {
            _cts?.Cancel();
            _cts = null;
            CleanupPlayer();
            if (_vtsSpeaking)
            {
                _vtsSpeaking = false;
                await ResetVtsSpeakingAsync();
            }
        }
    }
}
