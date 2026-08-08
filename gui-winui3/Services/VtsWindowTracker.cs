using System;
using System.Runtime.InteropServices;
using RuApp.Models;

namespace RuApp.Services;

public readonly record struct VtsWindowInfo(RectPx Rect, int Dpi);
public readonly record struct VtsWindowSnapshot(bool Visible, RectPx Rect, int Dpi);

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

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

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
        return new VtsWindowInfo(new RectPx(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top), dpi);
    }

    private static IntPtr FindByProcessName(string processName) { /* enum, lấy pid từ process; null nếu không */ return IntPtr.Zero; }
}

public sealed class VtsWindowTracker : IDisposable
{
    private readonly IVtsWindowLocator _locator;
    public VtsWindowTracker(IVtsWindowLocator locator) => _locator = locator;
    public VtsWindowSnapshot GetCurrent()
    {
        VtsWindowInfo? info = _locator.GetCurrent();
        return info is null
            ? new VtsWindowSnapshot(false, default, 96)
            : new VtsWindowSnapshot(true, info.Value.Rect, info.Value.Dpi);
    }
    public void Dispose() { }
}