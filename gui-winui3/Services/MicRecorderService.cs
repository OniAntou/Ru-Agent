using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace RuApp.Services
{
    public class MicRecorderService : IDisposable
    {
        private MediaCapture? _capture;
        private InMemoryRandomAccessStream? _stream;
        private MediaEncodingProfile? _profile;

        public bool IsRecording => _capture != null;

        public async Task<bool> StartAsync()
        {
            await StopAsync();

            try
            {
                var capture = new MediaCapture();
                var settings = new MediaCaptureInitializationSettings
                {
                    StreamingCaptureMode = StreamingCaptureMode.Audio,
                    MediaCategory = MediaCategory.Speech
                };
                await capture.InitializeAsync(settings);

                var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
                profile.Audio = AudioEncodingProperties.CreatePcm(16000, 1, 16);

                var stream = new InMemoryRandomAccessStream();
                await capture.StartRecordToStreamAsync(profile, stream);

                _capture = capture;
                _stream = stream;
                _profile = profile;
                return true;
            }
            catch
            {
                await StopAsync();
                return false;
            }
        }

        public async Task<byte[]?> StopAsync()
        {
            var capture = _capture;
            var stream = _stream;
            if (capture == null || stream == null)
            {
                await StopAsync();
                return null;
            }

            byte[]? wavBytes = null;
            try
            {
                await capture.StopRecordAsync();
                if (stream.Size > 44)
                {
                    using var dataReader = new DataReader(stream.GetInputStreamAt(0));
                    uint size = (uint)stream.Size;
                    await dataReader.LoadAsync(size);
                    byte[] buffer = new byte[size];
                    dataReader.ReadBytes(buffer);
                    wavBytes = buffer;
                }
            }
            catch
            {
                wavBytes = null;
            }
            finally
            {
                await StopAsync();
            }
            return wavBytes;
        }

        public async Task StopWithoutCaptureAsync()
        {
            await StopAsync();
        }

        public void Dispose()
        {
            _ = StopAsync();
        }
    }
}