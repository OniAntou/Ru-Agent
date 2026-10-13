using System.Runtime.InteropServices;
using System.Text;

namespace RuAgent.Cli.Images;

/// <summary>Copies text only on an explicit TUI copy action; never runs during rendering.</summary>
internal static class CliClipboardTextWriter
{
    // 200k retained Unicode characters may occupy 400k UTF-16 units, plus 2k row separators.
    internal const int MaxCopyChars = 404_000;
    public static Task<bool> TryWriteAsync(string text) => Task.Run(() => Write(text));

    private static bool Write(string text)
    {
        if (!OperatingSystem.IsWindows() || text.Length > MaxCopyChars) return false;
        var owner = GetConsoleWindow();
        if (owner == IntPtr.Zero) return false;
        var bytes = Encoding.Unicode.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal) + '\0');
        var memory = GlobalAlloc(0x0002, (nuint)bytes.Length);
        if (memory == IntPtr.Zero) return false;
        var opened = false;
        try
        {
            var pointer = GlobalLock(memory);
            if (pointer == IntPtr.Zero) return false;
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
            finally { GlobalUnlock(memory); }
            for (var attempt = 0; attempt < 5; attempt++)
            {
                opened = OpenClipboard(owner);
                if (opened) break;
                Thread.Sleep(40);
            }
            if (!opened || !EmptyClipboard() || SetClipboardData(13, memory) == IntPtr.Zero) return false;
            memory = IntPtr.Zero; // The clipboard now owns the allocation.
            return true;
        }
        finally
        {
            if (opened) CloseClipboard();
            if (memory != IntPtr.Zero) GlobalFree(memory);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);
}
