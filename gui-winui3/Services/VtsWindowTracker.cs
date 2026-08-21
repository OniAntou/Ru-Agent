using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using RuApp.Models;

namespace RuApp.Services;

public readonly record struct VtsWindowInfo(RectPx Rect, RectPx WorkArea, int Dpi);
public readonly record struct VtsWindowSnapshot(bool Visible, RectPx Rect, RectPx WorkArea, int Dpi);

public interface IVtsWindowLocator
{
    VtsWindowInfo? GetCurrent();
}

public interface IVtsWindowNative
{
    IntPtr FindWindowByTitle(string title);
    IntPtr FindWindowByProcessNames(params string[] processNames);
    bool TryGetWindowInfo(IntPtr handle, out VtsWindowInfo info);
}

public sealed class VtsWindowLocator : IVtsWindowLocator
{
    private const string WindowTitle = "VTube Studio";
    private static readonly string[] ProcessNames = ["VTube Studio", "VTubeStudio", "RuDesktopOverlay"];
    private static readonly TimeSpan DefaultProcessRetryDelay = TimeSpan.FromSeconds(2);

    private readonly IVtsWindowNative _native;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _processRetryDelay;
    private IntPtr _cachedHandle;
    private DateTimeOffset _nextProcessLookupAt = DateTimeOffset.MinValue;

    public VtsWindowLocator()
        : this(new Win32VtsWindowNative(), () => DateTimeOffset.UtcNow, DefaultProcessRetryDelay)
    {
    }

    public VtsWindowLocator(IVtsWindowNative native)
        : this(native, () => DateTimeOffset.UtcNow, DefaultProcessRetryDelay)
    {
    }

    public VtsWindowLocator(
        IVtsWindowNative native,
        Func<DateTimeOffset> utcNow,
        TimeSpan processRetryDelay)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        if (processRetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(processRetryDelay));
        }
        _processRetryDelay = processRetryDelay;
    }

    public VtsWindowInfo? GetCurrent()
    {
        if (_cachedHandle != IntPtr.Zero)
        {
            if (_native.TryGetWindowInfo(_cachedHandle, out VtsWindowInfo cached))
            {
                return cached;
            }
            _cachedHandle = IntPtr.Zero;
            _nextProcessLookupAt = DateTimeOffset.MinValue;
        }

        IntPtr handle = _native.FindWindowByTitle(WindowTitle);
        if (handle != IntPtr.Zero && _native.TryGetWindowInfo(handle, out VtsWindowInfo titled))
        {
            _cachedHandle = handle;
            return titled;
        }

        DateTimeOffset now = _utcNow();
        if (now < _nextProcessLookupAt)
        {
            return null;
        }
        _nextProcessLookupAt = now + _processRetryDelay;

        handle = _native.FindWindowByProcessNames(ProcessNames);
        if (handle == IntPtr.Zero || !_native.TryGetWindowInfo(handle, out VtsWindowInfo byProcess))
        {
            return null;
        }

        _cachedHandle = handle;
        return byProcess;
    }
}

public sealed class Win32VtsWindowNative : IVtsWindowNative
{
    private const int DwmExtendedFrameBounds = 9;
    private const uint MonitorDefaultToNearest = 2;

    public IntPtr FindWindowByTitle(string title) => FindWindow(null, title);

    public IntPtr FindWindowByProcessNames(params string[] processNames)
    {
        var targetPids = new HashSet<uint>();
        foreach (string processName in processNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch
            {
                continue;
            }

            try
            {
                foreach (Process process in processes)
                {
                    try
                    {
                        targetPids.Add((uint)process.Id);
                    }
                    catch (InvalidOperationException)
                    {
                        // The process exited between enumeration and inspection.
                    }
                }
            }
            finally
            {
                foreach (Process process in processes)
                {
                    process.Dispose();
                }
            }
        }

        if (targetPids.Count == 0)
        {
            return IntPtr.Zero;
        }

        IntPtr found = IntPtr.Zero;
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || IsIconic(handle))
            {
                return true;
            }
            GetWindowThreadProcessId(handle, out uint pid);
            if (!targetPids.Contains(pid))
            {
                return true;
            }
            found = handle;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public bool TryGetWindowInfo(IntPtr handle, out VtsWindowInfo info)
    {
        info = default;
        if (handle == IntPtr.Zero || !IsWindow(handle) || !IsWindowVisible(handle) || IsIconic(handle))
        {
            return false;
        }

        RECT rect;
        int dwmResult = DwmGetWindowAttribute(
            handle,
            DwmExtendedFrameBounds,
            out rect,
            Marshal.SizeOf<RECT>());
        if (dwmResult != 0 && !GetWindowRect(handle, out rect))
        {
            return false;
        }
        if (rect.Right <= rect.Left || rect.Bottom <= rect.Top)
        {
            return false;
        }

        var windowRect = ToRectPx(rect);
        RectPx workArea = windowRect;
        IntPtr monitor = MonitorFromRect(ref rect, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero)
        {
            var monitorInfo = new MONITORINFO
            {
                Size = (uint)Marshal.SizeOf<MONITORINFO>(),
            };
            if (GetMonitorInfo(monitor, ref monitorInfo))
            {
                workArea = ToRectPx(monitorInfo.WorkArea);
            }
        }

        int dpi = (int)GetDpiForWindow(handle);
        info = new VtsWindowInfo(windowRect, workArea, dpi > 0 ? dpi : 96);
        return true;
    }

    private static RectPx ToRectPx(RECT rect) =>
        new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr handle,
        int attribute,
        out RECT value,
        int valueSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint Size;
        public RECT Monitor;
        public RECT WorkArea;
        public uint Flags;
    }
}

public sealed class VtsWindowTracker : IDisposable
{
    private readonly IVtsWindowLocator _locator;

    public VtsWindowTracker(IVtsWindowLocator locator)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
    }

    public VtsWindowSnapshot GetCurrent()
    {
        VtsWindowInfo? info = _locator.GetCurrent();
        return info is null
            ? new VtsWindowSnapshot(false, default, default, 96)
            : new VtsWindowSnapshot(true, info.Value.Rect, info.Value.WorkArea, info.Value.Dpi);
    }

    public void Dispose()
    {
    }
}
