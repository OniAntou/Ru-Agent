using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using RuAgent.Broker.Tools.Browser;
using Xunit;

namespace RuAgent.Broker.Tests.Browser;

/// <summary>
/// A visible browser (headless: false) shows only the session's page and leaves no window once
/// that page closes: the launch's blank tab is closed after the first page, and an idle visible
/// browser shuts down. Windows are matched to browsers this test process started, so the user's
/// own Edge or Firefox never counts.
/// </summary>
[Collection(nameof(BrowserPersistentProfileTests))]
public sealed class BrowserVisibleWindowTests
{
    [BrowserFact]
    public Task Chromium_leaves_no_window_after_its_last_page_closes() => RunAsync(new ChromiumBrowserEngine());

    [FirefoxFact]
    public Task Firefox_leaves_no_window_after_its_last_page_closes() => RunAsync(new FirefoxBrowserEngine());

    private static async Task RunAsync(IBrowserEngine engine)
    {
        await using var server = new BrowserPageActionsTests.PageServer();
        await using (engine)
        {
            var page = await engine.OpenPageAsync(headless: false, CancellationToken.None);
            await engine.NavigateAsync(page, server.Url, CancellationToken.None);
            var shown = await WaitForAsync(windows => windows.Count == 1 && windows[0].Contains("Main", StringComparison.Ordinal));
            Assert.True(shown.Count == 1, "Expected one window showing the page, saw: " + string.Join(" | ", shown));
            Assert.DoesNotContain("more page", shown[0], StringComparison.Ordinal);

            await engine.ClosePageAsync(page, CancellationToken.None);
            var left = await WaitForAsync(windows => windows.Count == 0);
            Assert.True(left.Count == 0, "A window outlived the last page: " + string.Join(" | ", left));

            // The next visible open starts a fresh browser.
            var again = await engine.OpenPageAsync(headless: false, CancellationToken.None);
            await engine.NavigateAsync(again, server.Url, CancellationToken.None);
            Assert.Single(await WaitForAsync(windows => windows.Count == 1));
            await engine.ClosePageAsync(again, CancellationToken.None);
        }
    }

    private static async Task<List<string>> WaitForAsync(Func<List<string>, bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var windows = VisibleWindowsOfChildBrowsers();
        while (!condition(windows) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            windows = VisibleWindowsOfChildBrowsers();
        }

        return windows;
    }

    /// <summary>Titles of visible top-level windows owned by browser processes this test process started.</summary>
    private static List<string> VisibleWindowsOfChildBrowsers()
    {
        var children = ChildProcessIds(Environment.ProcessId);
        var titles = new List<string>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            GetWindowThreadProcessId(handle, out var processId);
            if (!children.Contains((int)processId)) return true;
            var title = new StringBuilder(512);
            GetWindowText(handle, title, title.Capacity);
            if (title.Length > 0) titles.Add(title.ToString());
            return true;
        }, 0);
        return titles;
    }

    /// <summary>Browser processes descended from <paramref name="root"/>; Firefox starts its real process through a launcher.</summary>
    private static HashSet<int> ChildProcessIds(int root)
    {
        var parents = new Dictionary<int, int>();
        foreach (var process in Process.GetProcessesByName("msedge").Concat(Process.GetProcessesByName("chrome")).Concat(Process.GetProcessesByName("firefox")))
        {
            using (process) parents[process.Id] = ParentOf(process.Id);
        }

        var result = new HashSet<int>();
        var added = true;
        while (added)
        {
            added = false;
            foreach (var (id, parent) in parents)
            {
                if (!result.Contains(id) && (parent == root || result.Contains(parent))) added |= result.Add(id);
            }
        }

        return result;
    }

    private static int ParentOf(int processId)
    {
        var handle = OpenProcess(0x1000, false, processId);
        if (handle == 0) return 0;
        try
        {
            var info = new PROCESS_BASIC_INFORMATION();
            return NtQueryInformationProcess(handle, 0, ref info, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) == 0
                ? (int)info.InheritedFromUniqueProcessId
                : 0;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    private delegate bool EnumWindowsProc(nint handle, nint data);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder text, int max);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(nint process, int infoClass, ref PROCESS_BASIC_INFORMATION info, int length, out int returned);
}
