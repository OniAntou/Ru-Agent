using System.Runtime.InteropServices;
using System.Text;

namespace RuAgent.Broker.Tools.Computer;

/// <summary>
/// Win32 implementation of <see cref="IDesktopController"/>. Every call runs under a
/// per-monitor DPI awareness context on the calling thread, so monitor, window and
/// capture coordinates are all physical pixels, and the caller keeps one action on
/// one thread.
/// </summary>
public sealed partial class WindowsDesktopController : IDesktopController
{
    private const int MaxTitleChars = 256;

    public bool IsInputDesktopAvailable()
    {
        using var dpi = DpiScope.Enter();
        var desktop = OpenInputDesktop(0, false, 0);
        if (desktop == 0) return false;
        try
        {
            var buffer = new char[64];
            return GetUserObjectInformation(desktop, 2, buffer, buffer.Length * 2, out _)
                && string.Equals(new string(buffer).TrimEnd('\0'), "Default", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    public IReadOnlyList<DesktopMonitor> Monitors()
    {
        using var dpi = DpiScope.Enter();
        var found = new List<(PixelRect Bounds, bool Primary)>();
        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
                found.Add((ToRect(info.rcMonitor), (info.dwFlags & 1) != 0));
            return true;
        }, 0);
        return found
            .OrderByDescending(item => item.Primary)
            .ThenBy(item => item.Bounds.Left)
            .ThenBy(item => item.Bounds.Top)
            .Select((item, index) => new DesktopMonitor(index, item.Bounds, item.Primary))
            .ToArray();
    }

    public IReadOnlyList<DesktopWindow> Windows()
    {
        using var dpi = DpiScope.Enter();
        var handles = new List<nint>();
        EnumWindows((handle, _) =>
        {
            handles.Add(handle);
            return true;
        }, 0);
        var processes = new Dictionary<int, (string App, string? Path)>();
        var windows = new List<DesktopWindow>();
        foreach (var handle in handles)
        {
            if (!IsWindowVisible(handle) || IsIconic(handle) || IsCloaked(handle)) continue;
            // Click-through overlays draw nothing the user can act on and must not hide what lies beneath.
            if ((GetWindowLongPtr(handle, -20) & 0x20) != 0) continue;
            if (Describe(handle, processes) is { Bounds.IsEmpty: false } window) windows.Add(window);
        }

        return windows;
    }

    public byte[] Capture(PixelRect bounds)
    {
        using var dpi = DpiScope.Enter();
        if (bounds.IsEmpty) throw new ComputerUseException("computer_capture_failed", "The monitor has no area.");
        var screen = GetDC(0);
        if (screen == 0) throw new ComputerUseException("computer_capture_failed", "The screen could not be read.");
        var memory = CreateCompatibleDC(screen);
        try
        {
            var info = new BITMAPINFO
            {
                biSize = Marshal.SizeOf<BITMAPINFO>(),
                biWidth = bounds.Width,
                biHeight = -bounds.Height,
                biPlanes = 1,
                biBitCount = 32,
            };
            var bitmap = CreateDIBSection(screen, ref info, 0, out var bits, 0, 0);
            if (bitmap == 0) throw new ComputerUseException("computer_capture_failed", "The capture buffer could not be created.");
            try
            {
                var previous = SelectObject(memory, bitmap);
                const uint SourceCopyWithLayered = 0x00CC0020 | 0x40000000;
                var copied = BitBlt(memory, 0, 0, bounds.Width, bounds.Height, screen, bounds.Left, bounds.Top, SourceCopyWithLayered);
                SelectObject(memory, previous);
                if (!copied) throw new ComputerUseException("computer_capture_failed", "The screen could not be copied.");
                var result = new byte[bounds.Width * bounds.Height * 4];
                Marshal.Copy(bits, result, 0, result.Length);
                return result;
            }
            finally
            {
                DeleteObject(bitmap);
            }
        }
        finally
        {
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    public DesktopWindow? RootWindowAt(int x, int y)
    {
        using var dpi = DpiScope.Enter();
        var handle = WindowFromPoint(new POINT { X = x, Y = y });
        return handle == 0 ? null : Describe(GetAncestor(handle, 2), new());
    }

    public DesktopWindow? ForegroundWindow()
    {
        using var dpi = DpiScope.Enter();
        var handle = GetForegroundWindow();
        return handle == 0 ? null : Describe(GetAncestor(handle, 2), new());
    }

    public bool IsShieldedFromInput(int processId)
    {
        var target = IntegrityLevel(processId);
        var self = IntegrityLevel(Environment.ProcessId);
        // A process Ru cannot inspect is treated as shielded: fail closed.
        return target is null || self is null || target > self;
    }

    public void MovePointer(int x, int y)
    {
        using var dpi = DpiScope.Enter();
        Send([Mouse(x, y, MouseMove | MouseAbsolute | MouseVirtualDesk)]);
    }

    public void Click(int x, int y, PointerButton button, int count, nint expectedRoot)
    {
        using var dpi = DpiScope.Enter();
        var (down, up) = button switch
        {
            PointerButton.Right => (0x0008u, 0x0010u),
            PointerButton.Middle => (0x0020u, 0x0040u),
            _ => (0x0002u, 0x0004u),
        };
        Send([Mouse(x, y, MouseMove | MouseAbsolute | MouseVirtualDesk)]);
        for (var index = 0; index < count; index++)
        {
            RequireRoot(GetAncestor(WindowFromPoint(new POINT { X = x, Y = y }), 2), expectedRoot);
            Send([Mouse(x, y, down | MouseAbsolute | MouseVirtualDesk), Mouse(x, y, up | MouseAbsolute | MouseVirtualDesk)]);
            if (index + 1 < count) Thread.Sleep(40);
        }
    }

    public void Drag(int fromX, int fromY, int toX, int toY, CancellationToken cancellationToken)
    {
        using var dpi = DpiScope.Enter();
        Send([Mouse(fromX, fromY, MouseMove | MouseAbsolute | MouseVirtualDesk)]);
        Send([Mouse(fromX, fromY, 0x0002 | MouseAbsolute | MouseVirtualDesk)]);
        try
        {
            const int Steps = 12;
            for (var step = 1; step <= Steps; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Thread.Sleep(15);
                Send([Mouse(fromX + (toX - fromX) * step / Steps, fromY + (toY - fromY) * step / Steps,
                    MouseMove | MouseAbsolute | MouseVirtualDesk)]);
            }
        }
        finally
        {
            // Never leave the button held, even when the drag is cancelled.
            Send([Mouse(toX, toY, 0x0004 | MouseAbsolute | MouseVirtualDesk)]);
        }
    }

    public void Scroll(int x, int y, int deltaX, int deltaY)
    {
        using var dpi = DpiScope.Enter();
        Send([Mouse(x, y, MouseMove | MouseAbsolute | MouseVirtualDesk)]);
        if (deltaY != 0) Send([Mouse(x, y, 0x0800 | MouseAbsolute | MouseVirtualDesk, unchecked((uint)deltaY))]);
        if (deltaX != 0) Send([Mouse(x, y, 0x1000 | MouseAbsolute | MouseVirtualDesk, unchecked((uint)deltaX))]);
    }

    public void TypeText(string text, nint expectedRoot, CancellationToken cancellationToken)
    {
        using var dpi = DpiScope.Enter();
        foreach (var character in text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Enter can open another window; every keystroke must still reach the granted one.
            RequireRoot(GetAncestor(GetForegroundWindow(), 2), expectedRoot);
            if (character == '\r') continue;
            if (character is '\n' or '\t')
            {
                var key = character == '\n' ? (ushort)0x0D : (ushort)0x09;
                Send([Key(key, 0), Key(key, KeyUp)]);
            }
            else
            {
                Send([Unicode(character, 0), Unicode(character, KeyUp)]);
            }

            Thread.Sleep(4);
        }
    }

    public void PressChord(KeyChord chord, nint expectedRoot)
    {
        using var dpi = DpiScope.Enter();
        RequireRoot(GetAncestor(GetForegroundWindow(), 2), expectedRoot);
        var down = chord.Modifiers.Select(modifier => Key(modifier, 0)).Append(Key(chord.Key, 0));
        var up = new[] { Key(chord.Key, KeyUp) }.Concat(chord.Modifiers.Reverse().Select(modifier => Key(modifier, KeyUp)));
        Send(down.Concat(up).ToArray());
    }

    public IReadOnlySet<int> RuHostProcessIds()
    {
        var parents = new Dictionary<int, (int Parent, string Name)>();
        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot == -1) return new HashSet<int>();
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = Marshal.SizeOf<PROCESSENTRY32>() };
            for (var more = Process32First(snapshot, ref entry); more; more = Process32Next(snapshot, ref entry))
                parents[(int)entry.th32ProcessID] = ((int)entry.th32ParentProcessID, entry.szExeFile.ToLowerInvariant());
        }
        finally
        {
            CloseHandle(snapshot);
        }

        var hosts = new HashSet<int>();
        foreach (var (processId, process) in parents)
        {
            if (process.Name is not ("ruagent.cli.exe" or "ru.exe")) continue;
            var current = process.Parent;
            // Bounded walk; a recycled parent id can form a loop.
            for (var depth = 0; depth < 16 && parents.TryGetValue(current, out var parent); depth++)
            {
                if (parent.Name is "explorer.exe" or "services.exe" or "svchost.exe" or "wininit.exe" or "system") break;
                hosts.Add(current);
                current = parent.Parent;
            }
        }

        return hosts;
    }

    private static void RequireRoot(nint actual, nint expected)
    {
        if (actual != expected)
            throw new ComputerUseException("computer_target_changed",
                "The target window changed during the action; take a new screenshot before continuing.");
    }

    private const uint MouseMove = 0x0001;
    private const uint MouseAbsolute = 0x8000;
    private const uint MouseVirtualDesk = 0x4000;
    private const uint KeyUp = 0x0002;

    private static INPUT Mouse(int x, int y, uint flags, uint data = 0)
    {
        var left = GetSystemMetrics(76);
        var top = GetSystemMetrics(77);
        var width = Math.Max(2, GetSystemMetrics(78));
        var height = Math.Max(2, GetSystemMetrics(79));
        return new INPUT
        {
            type = 0,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = (int)Math.Round((x - left) * 65535.0 / (width - 1)),
                    dy = (int)Math.Round((y - top) * 65535.0 / (height - 1)),
                    mouseData = data,
                    dwFlags = flags,
                },
            },
        };
    }

    private static INPUT Key(ushort virtualKey, uint flags) => new()
    {
        type = 1,
        U = new InputUnion
        {
            ki = new KEYBDINPUT { wVk = virtualKey, dwFlags = flags | (IsExtended(virtualKey) ? 0x0001u : 0u) },
        },
    };

    private static INPUT Unicode(char character, uint flags) => new()
    {
        type = 1,
        U = new InputUnion { ki = new KEYBDINPUT { wScan = character, dwFlags = flags | 0x0004 } },
    };

    private static bool IsExtended(ushort key) =>
        key is >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D;

    private static void Send(INPUT[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            throw new ComputerUseException("computer_input_blocked", "Windows did not accept the input.");
    }

    private static DesktopWindow? Describe(nint handle, Dictionary<int, (string App, string? Path)> processes)
    {
        if (handle == 0) return null;
        GetWindowThreadProcessId(handle, out var processId);
        // A UWP app's frame belongs to ApplicationFrameHost; its content window names the app.
        if (string.Equals(Path.GetFileName(ProcessPath((int)processId)), "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            var core = FindWindowEx(handle, 0, "Windows.UI.Core.CoreWindow", null);
            if (core != 0) GetWindowThreadProcessId(core, out processId);
        }
        if (!processes.TryGetValue((int)processId, out var process))
        {
            var path = ProcessPath((int)processId);
            var app = RuAgent.Protocol.ComputerProtectedApps.Normalize(path is null ? null : Path.GetFileName(path)) ?? "unknown.exe";
            processes[(int)processId] = process = (app, path);
        }

        RECT rect;
        if (DwmGetWindowAttribute(handle, 9, out rect, Marshal.SizeOf<RECT>()) != 0 && !GetWindowRect(handle, out rect))
            return null;
        var title = new StringBuilder(MaxTitleChars);
        GetWindowText(handle, title, MaxTitleChars);
        return new DesktopWindow(handle, ToRect(rect), (int)processId, process.App, process.Path, title.ToString());
    }

    private static bool IsCloaked(nint handle) =>
        DwmGetWindowAttribute(handle, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static string? ProcessPath(int processId)
    {
        var process = OpenProcess(0x1000, false, (uint)processId);
        if (process == 0) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var length = buffer.Capacity;
            return QueryFullProcessImageName(process, 0, buffer, ref length) ? buffer.ToString(0, length) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static uint? IntegrityLevel(int processId)
    {
        var process = OpenProcess(0x1000, false, (uint)processId);
        if (process == 0) return null;
        try
        {
            if (!OpenProcessToken(process, 0x0008, out var token)) return null;
            try
            {
                GetTokenInformation(token, 25, 0, 0, out var needed);
                if (needed <= 0) return null;
                var buffer = Marshal.AllocHGlobal(needed);
                try
                {
                    if (!GetTokenInformation(token, 25, buffer, needed, out _)) return null;
                    var sid = Marshal.ReadIntPtr(buffer);
                    var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                    return count == 0 ? null : (uint)Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static PixelRect ToRect(RECT rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private readonly struct DpiScope : IDisposable
    {
        private readonly nint _previous;

        private DpiScope(nint previous) => _previous = previous;

        public static DpiScope Enter() => new(SetThreadDpiAwarenessContext(-4));

        public void Dispose()
        {
            if (_previous != 0) SetThreadDpiAwarenessContext(_previous);
        }
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
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
        public int bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data);
    private delegate bool WindowEnumProc(nint handle, nint data);

    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformation(nint handle, int index, [Out] char[] info, int length, out int needed);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowEnumProc callback, nint data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint handle);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint handle, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern nint GetDC(nint handle);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint handle, nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint hdc, ref BITMAPINFO info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle, int attribute, out RECT value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle, int attribute, out int value, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(nint process, int flags, StringBuilder name, ref int size);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(nint token, int infoClass, nint info, int length, out int needed);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthorityCount(nint sid);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthority(nint sid, uint index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string? className, string? title);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32First(nint snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32Next(nint snapshot, ref PROCESSENTRY32 entry);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public int dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }
}
