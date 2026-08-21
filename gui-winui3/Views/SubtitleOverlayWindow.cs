using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views;

public sealed class SubtitleOverlayWindow : IDisposable
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_HIDE = 0;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const uint ULW_ALPHA = 0x00000002;

    private const uint WM_NCHITTEST = 0x0084;
    private const IntPtr HTCAPTION = (IntPtr)2;
    private const uint WM_MOVE = 0x0003;
    private const uint WM_EXITSIZEMOVE = 0x0232;

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private readonly IntPtr _hwnd;
    private readonly WndProc _wndProcDelegate;
    private readonly DispatcherTimer _fadeTimer;

    private string _text = string.Empty;
    private int _x;
    private int _y;
    private int _width = 600;
    private int _height = 80;
    private int _fontSize = 24;
    private double _sizeScale = 1.0;
    private Color _textColor = Color.White;

    private double _currentOpacity = 1.0;
    private Action? _fadeCompleted;
    private bool _shown;
    private bool _disposed;
    private bool _isDraggable;

    public event Action<int, int>? PositionMoved;

    public bool IsDraggable => _isDraggable;
    public int CurrentX => _x;
    public int CurrentY => _y;

    public SubtitleOverlayWindow()
    {
        _wndProcDelegate = CustomWndProc;
        string className = "RuSubtitleOverlay_" + Guid.NewGuid().ToString("N");

        var wndClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = GetModuleHandle(null),
            hIcon = IntPtr.Zero,
            hCursor = IntPtr.Zero,
            hbrBackground = IntPtr.Zero,
            lpszMenuName = null,
            lpszClassName = className,
            hIconSm = IntPtr.Zero
        };

        RegisterClassEx(ref wndClass);

        int exStyle = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        _hwnd = CreateWindowEx(
            exStyle,
            className,
            "Ru Subtitle Overlay",
            WS_POPUP,
            0, 0, _width, _height,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

        _fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        _fadeTimer.Tick += OnFadeTick;
    }

    public void SetDraggable(bool draggable)
    {
        if (_disposed) return;
        _isDraggable = draggable;

        int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        if (draggable)
        {
            exStyle &= ~WS_EX_TRANSPARENT;
            exStyle &= ~WS_EX_NOACTIVATE;
        }
        else
        {
            exStyle |= WS_EX_TRANSPARENT;
            exStyle |= WS_EX_NOACTIVATE;
        }
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle);

        if (draggable)
        {
            CancelFadeOut();
            _currentOpacity = 1.0;
            _shown = true;
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
            EnsureTopmost();
        }
        RenderAndPresent();
    }

    public void EnsureTopmost()
    {
        if (_hwnd != IntPtr.Zero && _shown && !_disposed)
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
    }

    public void ShowOverlay()
    {
        if (_disposed) return;
        CancelFadeOut();
        _currentOpacity = 1.0;
        _shown = true;
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        EnsureTopmost();
        RenderAndPresent();
    }

    public void HideOverlay()
    {
        if (_disposed || !_shown || _isDraggable) return;
        _shown = false;
        ShowWindow(_hwnd, SW_HIDE);
    }

    public void Close()
    {
        Dispose();
    }

    public void MoveAndResize(RectPx rect, int fontSize)
    {
        _x = rect.X;
        _y = rect.Y;
        _width = Math.Max(50, rect.Width);
        _height = Math.Max(30, rect.Height);
        _fontSize = fontSize;

        if (_shown)
        {
            RenderAndPresent();
        }
    }

    public void SetPositionOnly(int x, int y)
    {
        _x = x;
        _y = y;
        if (_shown)
        {
            RenderAndPresent();
        }
    }

    public void ApplySnapshot(SubtitleSnapshot snapshot)
    {
        _text = snapshot.Text ?? string.Empty;
        if (_shown)
        {
            RenderAndPresent();
        }
    }

    public void SetTextColor(string hexColor)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hexColor)) return;
            hexColor = hexColor.Trim();
            if (!hexColor.StartsWith('#')) hexColor = "#" + hexColor;

            byte a = 255, r = 255, g = 255, b = 255;
            if (hexColor.Length == 7)
            {
                r = Convert.ToByte(hexColor.Substring(1, 2), 16);
                g = Convert.ToByte(hexColor.Substring(3, 2), 16);
                b = Convert.ToByte(hexColor.Substring(5, 2), 16);
            }
            else if (hexColor.Length == 9)
            {
                a = Convert.ToByte(hexColor.Substring(1, 2), 16);
                r = Convert.ToByte(hexColor.Substring(3, 2), 16);
                g = Convert.ToByte(hexColor.Substring(5, 2), 16);
                b = Convert.ToByte(hexColor.Substring(7, 2), 16);
            }
            _textColor = Color.FromArgb(a, r, g, b);
        }
        catch
        {
            _textColor = Color.White;
        }

        if (_shown)
        {
            RenderAndPresent();
        }
    }

    public void SetSizeCategory(string sizeCategory)
    {
        _sizeScale = sizeCategory?.ToLowerInvariant() switch
        {
            "small" => 0.8,
            "large" => 1.25,
            "xlarge" => 1.5,
            _ => 1.0
        };

        if (_shown)
        {
            RenderAndPresent();
        }
    }

    public void BeginFadeOut(Action onCompleted)
    {
        if (_isDraggable) return;
        CancelFadeOut();
        _fadeCompleted = onCompleted;
        _fadeTimer.Start();
    }

    public void CancelFadeOut()
    {
        _fadeTimer.Stop();
        _fadeCompleted = null;
        _currentOpacity = 1.0;
        if (_shown)
        {
            RenderAndPresent();
        }
    }

    private void OnFadeTick(object? sender, object e)
    {
        if (_isDraggable)
        {
            _fadeTimer.Stop();
            _currentOpacity = 1.0;
            return;
        }

        _currentOpacity -= 0.05;
        if (_currentOpacity <= 0.0)
        {
            _currentOpacity = 0.0;
            _fadeTimer.Stop();
            HideOverlay();
            Action? completed = _fadeCompleted;
            _fadeCompleted = null;
            completed?.Invoke();
        }
        else
        {
            RenderAndPresent();
        }
    }

    private void RenderAndPresent()
    {
        if (_hwnd == IntPtr.Zero || _disposed || _width <= 0 || _height <= 0) return;

        string displayText = _text;
        if (_isDraggable && string.IsNullOrWhiteSpace(displayText))
        {
            displayText = "✨ [Nhấn giữ chuột trái để kéo thả vị trí phụ đề] ✨";
        }

        int effectiveFontSize = Math.Max(12, (int)Math.Round(_fontSize * _sizeScale));
        int targetWidth = _width;
        int targetHeight = _height;

        // Dynamically measure required text height to prevent clipping long sentences
        if (!string.IsNullOrWhiteSpace(displayText))
        {
            using var measureBmp = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
            using var mg = Graphics.FromImage(measureBmp);
            using var font = new Font("Segoe UI", effectiveFontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var sfMeasure = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = 0
            };

            float contentWidth = Math.Max(100, targetWidth - 24);
            var measured = mg.MeasureString(displayText, font, new SizeF(contentWidth, 4000), sfMeasure);
            int neededHeight = (int)Math.Ceiling(measured.Height) + 24; // Extra padding for 4.5px outline stroke
            if (neededHeight > targetHeight)
            {
                targetHeight = neededHeight;
            }
        }

        using var bitmap = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            if (_isDraggable)
            {
                // Draw a nice subtle dashed border indicating draggable area
                using var guidePen = new Pen(Color.FromArgb(180, 0, 160, 255), 1.5f)
                {
                    DashStyle = DashStyle.Dash
                };
                g.DrawRectangle(guidePen, 1, 1, targetWidth - 2, targetHeight - 2);
            }

            if (!string.IsNullOrWhiteSpace(displayText))
            {
                using var path = new GraphicsPath();
                using var font = new Font("Segoe UI", effectiveFontSize, FontStyle.Bold, GraphicsUnit.Pixel);
                using var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    FormatFlags = 0
                };

                var layoutRect = new RectangleF(12, 4, targetWidth - 24, targetHeight - 8);
                path.AddString(displayText, font.FontFamily, (int)font.Style, effectiveFontSize, layoutRect, sf);

                // Deep dark outline (pen width = 4.5px) for high contrast on any background
                using var outlinePen = new Pen(Color.FromArgb(230, 10, 10, 10), 4.5f)
                {
                    LineJoin = LineJoin.Round
                };
                g.DrawPath(outlinePen, path);

                // Inner fill color
                using var fillBrush = new SolidBrush(_isDraggable ? Color.FromArgb(255, 255, 220, 100) : _textColor);
                g.FillPath(fillBrush, path);
            }
        }

        IntPtr screenDC = GetDC(IntPtr.Zero);
        IntPtr memDC = CreateCompatibleDC(screenDC);
        IntPtr hBitmap = bitmap.GetHbitmap(Color.FromArgb(0, 0, 0, 0));
        IntPtr oldBitmap = SelectObject(memDC, hBitmap);

        var ptSrc = new POINT { X = 0, Y = 0 };
        var ptDst = new POINT { X = _x, Y = _y };
        var sz = new SIZE { cx = targetWidth, cy = targetHeight };
        var blend = new BLENDFUNCTION
        {
            BlendOp = AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = (byte)Math.Clamp((int)Math.Round(_currentOpacity * 255), 0, 255),
            AlphaFormat = AC_SRC_ALPHA
        };

        UpdateLayeredWindow(_hwnd, screenDC, ref ptDst, ref sz, memDC, ref ptSrc, 0, ref blend, ULW_ALPHA);

        SelectObject(memDC, oldBitmap);
        DeleteObject(hBitmap);
        DeleteDC(memDC);
        ReleaseDC(IntPtr.Zero, screenDC);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _fadeTimer.Stop();
        if (_hwnd != IntPtr.Zero)
        {
            ShowWindow(_hwnd, SW_HIDE);
            DestroyWindow(_hwnd);
        }
    }

    private IntPtr CustomWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_NCHITTEST && _isDraggable)
        {
            return HTCAPTION;
        }
        if ((msg == WM_MOVE || msg == WM_EXITSIZEMOVE) && _isDraggable)
        {
            if (GetWindowRect(_hwnd, out RECT r))
            {
                _x = r.Left;
                _y = r.Top;
                PositionMoved?.Invoke(_x, _y);
            }
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, uint crKey, [In] ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
}
