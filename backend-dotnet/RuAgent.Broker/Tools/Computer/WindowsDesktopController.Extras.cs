using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RuAgent.Broker.Tools.Computer;

/// <summary>
/// Modifier keys around a pointer action, launching a granted application, and the Unicode
/// clipboard (ADR 0031 amendment 2026-11-25).
/// </summary>
public sealed partial class WindowsDesktopController
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private const int ClipboardAttempts = 10;

    public void SetModifiers(IReadOnlyList<ushort> keys, bool down)
    {
        if (keys.Count == 0) return;
        var ordered = down ? keys : keys.Reverse().ToArray();
        Send(ordered.Select(key => Key(key, down ? 0 : KeyUp)).ToArray());
    }

    public string? Launch(string appName)
    {
        if (!OperatingSystem.IsWindows()) return "Applications can only be started on Windows.";
        var path = ApplicationLocator.Find(appName);
        if (path is null) return "No installed application named " + appName + " was found.";
        // An App Paths entry may point at another program, so the resolved file is checked too.
        var resolvedName = Path.GetFileName(path);
        if (RuAgent.Protocol.ComputerProtectedApps.IsProtected(resolvedName) || RuAgent.Protocol.ComputerLaunchPolicy.IsLaunchRefused(resolvedName))
            return appName + " resolves to " + resolvedName + ", which computer use never starts.";
        try
        {
            // The full path is started with no arguments, so nothing is searched for in the
            // current directory and the model cannot pass a command line.
            using var process = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            });
            return null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return "Windows could not start " + appName + ".";
        }
    }

    public string? ReadClipboardText()
    {
        OpenClipboardWithRetry();
        try
        {
            if (!IsClipboardFormatAvailable(CfUnicodeText)) return null;
            var handle = GetClipboardData(CfUnicodeText);
            if (handle == 0) return null;
            var pointer = GlobalLock(handle);
            if (pointer == 0) return null;
            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    public void WriteClipboardText(string text)
    {
        var bytes = (text.Length + 1) * 2;
        var memory = GlobalAlloc(GmemMoveable, (nuint)bytes);
        if (memory == 0) throw new ComputerUseException("computer_clipboard_unavailable", "Windows could not allocate clipboard memory.");
        var owned = true;
        try
        {
            var pointer = GlobalLock(memory);
            if (pointer == 0) throw new ComputerUseException("computer_clipboard_unavailable", "Windows could not lock clipboard memory.");
            try
            {
                Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
                Marshal.WriteInt16(pointer, text.Length * 2, 0);
            }
            finally
            {
                GlobalUnlock(memory);
            }

            OpenClipboardWithRetry();
            try
            {
                if (!EmptyClipboard() || SetClipboardData(CfUnicodeText, memory) == 0)
                    throw new ComputerUseException("computer_clipboard_unavailable", "Windows did not accept the clipboard text.");
                // The clipboard owns the memory once SetClipboardData succeeds.
                owned = false;
            }
            finally
            {
                CloseClipboard();
            }
        }
        finally
        {
            if (owned) GlobalFree(memory);
        }
    }

    private static void OpenClipboardWithRetry()
    {
        // Another program may hold the clipboard for a moment; a short retry covers that.
        for (var attempt = 0; attempt < ClipboardAttempts; attempt++)
        {
            if (OpenClipboard(0)) return;
            Thread.Sleep(20);
        }

        throw new ComputerUseException("computer_clipboard_unavailable", "Another program is holding the clipboard; try again.");
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
}

/// <summary>
/// Finds an installed application's executable by file name without consulting the current
/// directory: registered App Paths first, then the Windows directories, absolute PATH entries and
/// the user's app execution aliases.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class ApplicationLocator
{
    private const string AppPathsKey = @"Software\Microsoft\Windows\CurrentVersion\App Paths\";

    public static string? Find(string appName)
    {
        if (appName.IndexOfAny(['\\', '/', ':']) >= 0) return null;
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            if (RegisteredPath(hive, appName) is { } registered) return registered;
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidates = new List<string>
        {
            Path.Combine(Environment.SystemDirectory, appName),
            Path.Combine(windows, appName),
        };
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = Environment.ExpandEnvironmentVariables(entry.Trim('"'));
            // A relative entry such as "." would resolve against Ru's own working directory.
            if (Path.IsPathFullyQualified(directory)) candidates.Add(Path.Combine(directory, appName));
        }

        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", appName));
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? RegisteredPath(RegistryKey hive, string appName)
    {
        try
        {
            using var key = hive.OpenSubKey(AppPathsKey + appName);
            if (key?.GetValue(null) is not string value) return null;
            var path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            return Path.IsPathFullyQualified(path) && File.Exists(path) ? path : null;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
