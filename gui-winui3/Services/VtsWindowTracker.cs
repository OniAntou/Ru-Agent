using System;
using System.Runtime.InteropServices;
using RuApp.Models;

namespace RuApp.Services;

public readonly record struct VtsWindowInfo(RectPx Rect, RectPx WorkArea, int Dpi);
public readonly record struct VtsWindowSnapshot(bool Visible, RectPx Rect, RectPx WorkArea, int Dpi);

public interface IVtsWindowLocator
{
    VtsWindowInfo? GetCurrent();
}

public sealed class VtsWindowLocator : IVtsWindowLocator
{
    private const string WindowTitle = "VTube Studio";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpProc, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const uint MONITOR_DEFAULTTONULL = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public VtsWindowInfo? GetCurrent()
    {
        IntPtr hwnd = FindWindow(null, WindowTitle);
        if (hwnd == IntPtr.Zero)
        {
            hwnd = FindByProcessName("VTubeStudio");
        }
        if (hwnd == IntPtr.Zero) return null;

        GetWindowRect(hwnd, out RECT rect);
        int dpi = (int)GetDpiForWindow(hwnd);
        var vtsRect = new RectPx(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

        RECT monitorRect = new RECT { Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom };
        IntPtr hMonitor = MonitorFromRect(ref monitorRect, MONITOR_DEFAULTTONULL);
        RectPx workArea = vtsRect;
        if (hMonitor != IntPtr.Zero)
        {
            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMonitor, ref mi))
            {
                workArea = new RectPx(
                    mi.rcWork.Left,
                    mi.rcWork.Top,
                    mi.rcWork.Right - mi.rcWork.Left,
                    mi.rcWork.Bottom - mi.rcWork.Top);
            }
        }

        return new VtsWindowInfo(vtsRect, workArea, dpi);
    }

    private static IntPtr FindByProcessName(string processName) { return IntPtr.Zero; }
}

public sealed class VtsWindowTracker : IDisposable
{
    private readonly IVtsWindowLocator _locator;
    public VtsWindowTracker(IVtsWindowLocator locator) => _locator = locator;
    public VtsWindowSnapshot GetCurrent()
    {
        VtsWindowInfo? info = _locator.GetCurrent();
        return info is null
            ? new VtsWindowSnapshot(false, default, default, 96)
            : new VtsWindowSnapshot(true, info.Value.Rect, info.Value.WorkArea, info.Value.Dpi);
    }
    public void Dispose() { }
}
