using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using RuApp.Models;

namespace RuApp.Services
{
    public class ScreenObserverService
    {
        private readonly ApiService _apiService;
        private CancellationTokenSource? _observerCts;

        public event Action<ObservationMetricsDto>? OnMetricsUpdated;
        public bool IsActive { get; private set; } = false;

        private long _frameCount = 0;
        private long _visionCount = 0;
        private long _staleDrops = 0;
        private byte[]? _prevFingerprint;

        public ScreenObserverService(ApiService apiService)
        {
            _apiService = apiService;
        }

        public async Task<bool> StartAsync()
        {
            bool ok = await _apiService.StartObservationAsync();
            if (ok)
            {
                IsActive = true;
                StartObservationLoop();
            }
            return ok;
        }

        public async Task<bool> StopAsync()
        {
            bool ok = await _apiService.StopObservationAsync();
            IsActive = false;
            StopObservationLoop();
            return ok;
        }

        private void StartObservationLoop()
        {
            StopObservationLoop();
            _observerCts = new CancellationTokenSource();
            var ct = _observerCts.Token;

            _ = Task.Run(async () =>
            {
                var stopwatch = new Stopwatch();

                while (!ct.IsCancellationRequested && IsActive)
                {
                    stopwatch.Restart();
                    _frameCount++;

                    // 1. Capture 320x180 downsampled screen thumbnail using Win32 GDI StretchBlt
                    byte[]? bgraPixels = CaptureScreenThumbnail(320, 180);
                    double delta = 0.0;
                    long elapsedMs = 0;

                    if (bgraPixels != null)
                    {
                        // 2. Compute 32x18 grayscale fingerprint
                        byte[] currFingerprint = ComputeFingerprint(bgraPixels, 320, 180, 32, 18);

                        if (_prevFingerprint != null)
                        {
                            delta = ComputeMAD(currFingerprint, _prevFingerprint);
                        }
                        _prevFingerprint = currFingerprint;

                        // 3. Motion Gating Check
                        if (delta < 0.04 && _frameCount > 1)
                        {
                            // Static screen -> Skip vision AI processing
                            _staleDrops++;
                        }
                        else
                        {
                            // Noticeable motion -> Increment vision count
                            _visionCount++;
                        }
                    }

                    stopwatch.Stop();
                    elapsedMs = stopwatch.ElapsedMilliseconds;

                    var metrics = new ObservationMetricsDto
                    {
                        Active = IsActive,
                        FrameCount = _frameCount,
                        VisionCount = _visionCount,
                        StaleDrops = _staleDrops,
                        EmittedCount = 0,
                        MsBefore = elapsedMs,
                        MsCloud = 0,
                        MsAfter = 0,
                        LastMotionDelta = Math.Round(delta * 100.0, 2),
                        Engine = "UltraFast-GDI-Native"
                    };

                    OnMetricsUpdated?.Invoke(metrics);

                    // Adaptive Delay: 1500 ms sampling interval
                    try
                    {
                        await Task.Delay(1500, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }, ct);
        }

        private void StopObservationLoop()
        {
            _observerCts?.Cancel();
            _observerCts?.Dispose();
            _observerCts = null;
        }

        #region Ultra-Fast Win32 GDI Downsampled Screen Capture

        private static byte[]? CaptureScreenThumbnail(int width, int height)
        {
            IntPtr hDeskWin = GetDesktopWindow();
            IntPtr hSrcDC = GetDC(hDeskWin);
            if (hSrcDC == IntPtr.Zero) return null;

            int screenWidth = GetSystemMetrics(0);  // SM_CXSCREEN
            int screenHeight = GetSystemMetrics(1); // SM_CYSCREEN
            if (screenWidth <= 0 || screenHeight <= 0)
            {
                ReleaseDC(hDeskWin, hSrcDC);
                return null;
            }

            IntPtr hDestDC = CreateCompatibleDC(hSrcDC);
            IntPtr hBitmap = CreateCompatibleBitmap(hSrcDC, width, height);
            IntPtr hOldBmp = SelectObject(hDestDC, hBitmap);

            // StretchBlt directly downsamples on GPU/GDI driver to 320x180
            bool success = StretchBlt(hDestDC, 0, 0, width, height, hSrcDC, 0, 0, screenWidth, screenHeight, 0x00CC0020 /* SRCCOPY */);

            byte[]? bgra = null;
            if (success)
            {
                BITMAPINFO bmi = new BITMAPINFO();
                bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                bmi.bmiHeader.biWidth = width;
                bmi.bmiHeader.biHeight = -height; // Top-down
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = 0; // BI_RGB

                bgra = new byte[width * height * 4];
                GetDIBits(hDestDC, hBitmap, 0, (uint)height, bgra, ref bmi, 0);
            }

            SelectObject(hDestDC, hOldBmp);
            DeleteObject(hBitmap);
            DeleteDC(hDestDC);
            ReleaseDC(hDeskWin, hSrcDC);

            return bgra;
        }

        private static byte[] ComputeFingerprint(byte[] bgra, int srcW, int srcH, int fpW, int fpH)
        {
            byte[] fp = new byte[fpW * fpH];
            int scaleX = srcW / fpW;
            int scaleY = srcH / fpH;

            for (int y = 0; y < fpH; y++)
            {
                for (int x = 0; x < fpW; x++)
                {
                    int srcX = x * scaleX;
                    int srcY = y * scaleY;
                    int idx = (srcY * srcW + srcX) * 4;
                    if (idx + 2 < bgra.Length)
                    {
                        byte b = bgra[idx];
                        byte g = bgra[idx + 1];
                        byte r = bgra[idx + 2];
                        fp[y * fpW + x] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
                    }
                }
            }
            return fp;
        }

        private static double ComputeMAD(byte[] fp1, byte[] fp2)
        {
            if (fp1.Length != fp2.Length || fp1.Length == 0) return 0.0;
            long diffSum = 0;
            for (int i = 0; i < fp1.Length; i++)
            {
                diffSum += Math.Abs(fp1[i] - fp2[i]);
            }
            return (double)diffSum / (fp1.Length * 255.0);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public uint[] bmiColors;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        private static extern bool StretchBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, int nWidthSrc, int nHeightSrc, int dwRop);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, [Out] byte[] vbits, ref BITMAPINFO pbmi, uint usage);

        #endregion
    }
}
