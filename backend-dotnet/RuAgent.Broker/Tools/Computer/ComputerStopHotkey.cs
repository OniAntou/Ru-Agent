using System.Runtime.InteropServices;

namespace RuAgent.Broker.Tools.Computer;

/// <summary>The emergency stop for computer use (ADR 0031).</summary>
public interface IComputerStopHotkey
{
    /// <summary>Registers the hotkey once; false when Windows refused it.</summary>
    bool EnsureRegistered();

    event Action? Pressed;
}

/// <summary>
/// Ctrl+Alt+Backspace, plus Ctrl+Alt+Pause for keyboards that have that key, registered
/// on a dedicated message-loop thread: the Host has no window or message pump of its
/// own. Either chord stops computer use; it is available only when Ctrl+Alt+Backspace registers.
/// </summary>
public sealed class ComputerStopHotkey : IComputerStopHotkey, IDisposable
{
    public const string Description = "Ctrl+Alt+Backspace (or Ctrl+Alt+Pause)";
    private const int BackspaceHotkeyId = 0x5275; // "Ru"
    private const int PauseHotkeyId = 0x5276;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VirtualKeyBackspace = 0x08;
    private const uint VirtualKeyPause = 0x13;
    private const uint WmHotkey = 0x0312;
    private const uint WmQuit = 0x0012;

    private readonly object _gate = new();
    private Thread? _thread;
    private uint _threadId;
    private bool? _registered;

    public event Action? Pressed;

    public bool EnsureRegistered()
    {
        lock (_gate)
        {
            if (_registered is { } known) return known;
            using var ready = new ManualResetEventSlim();
            var registered = false;
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                var backspace = RegisterHotKey(0, BackspaceHotkeyId, ModControl | ModAlt | ModNoRepeat, VirtualKeyBackspace);
                var pause = RegisterHotKey(0, PauseHotkeyId, ModControl | ModAlt | ModNoRepeat, VirtualKeyPause);
                // Every keyboard has Backspace, so that chord must work; Pause is a convenience.
                registered = backspace;
                ready.Set();
                if (!registered)
                {
                    if (pause) UnregisterHotKey(0, PauseHotkeyId);
                    return;
                }
                try
                {
                    while (GetMessage(out var message, 0, 0, 0) > 0)
                    {
                        if (message.message == WmHotkey && message.wParam is BackspaceHotkeyId or PauseHotkeyId) Pressed?.Invoke();
                    }
                }
                finally
                {
                    if (backspace) UnregisterHotKey(0, BackspaceHotkeyId);
                    if (pause) UnregisterHotKey(0, PauseHotkeyId);
                }
            })
            {
                IsBackground = true,
                Name = "Ru computer-use stop hotkey",
            };
            _thread.Start();
            ready.Wait();
            _registered = registered;
            return registered;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_registered == true && _threadId != 0) PostThreadMessage(_threadId, WmQuit, 0, 0);
            _registered = false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint handle, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint handle, int id);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG message, nint handle, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint message, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
