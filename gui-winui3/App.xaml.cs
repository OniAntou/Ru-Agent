using System;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using RuApp.Services;

namespace RuApp;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private Window? _window;
    public App()
    {
        InitializeComponent();
        RegisterTranscriptTypography();
        this.UnhandledException += (s, e) =>
        {
            // Only swallow exceptions that are recoverable; let truly fatal
            // failures crash so bugs are not hidden behind an empty UI.
            e.Handled = AppUnhandledExceptionPolicy.Handle(
                e.Exception,
                exception => Services.GuiDiagnosticWriter.WriteCrashEvent(exception));
        };

        // A fail-fast inside the XAML framework never reaches UnhandledException, so
        // the crash log stayed empty and a report could name only a fault module and
        // an offset. This domain handler runs as the process tears down and is the
        // only chance to record what actually threw.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                Services.GuiDiagnosticWriter.WriteCrashEvent(e.ExceptionObject as Exception);
            }
            catch
            {
                // Nothing left to report to: the process is already terminating.
            }
        };
    }

    /// <summary>
    /// Publishes the transcript typography singleton as an application resource so
    /// the transcript can bind its font sizes to it.
    /// </summary>
    /// <remarks>
    /// Registered from code rather than declared in App.xaml. Markup cannot construct
    /// the type -- its constructor takes an optional store, which is not a public
    /// default constructor as the XAML compiler defines one -- and declaring it there
    /// would also risk a second instance existing beside the singleton the transcript
    /// reads, so a size change could apply to one and not the other.
    /// </remarks>
    private void RegisterTranscriptTypography()
    {
        try
        {
            Resources["TranscriptTypography"] = Services.TranscriptTypography.Instance;
        }
        catch (Exception ex)
        {
            // A missing typography resource costs the font-size preference, not the app.
            System.Diagnostics.Debug.WriteLine($"[App] Transcript typography registration failed: {ex.Message}");
        }
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        RuAppComposition composition = RuAppComposition.FromArguments(
            ResolveLaunchArguments(args.Arguments));
        const string mutexName = "RuApp_WinUI3_SingleInstance";
        _singleInstanceMutex = new Mutex(true, mutexName, out bool createdNew);
        if (!createdNew)
        {
            if (!composition.IsHarness)
            {
                ActivateExistingInstance();
            }

            composition.Dispose();
            Environment.Exit(0);
            return;
        }

        _window = new MainWindow(composition);
        _window.Closed += (_, _) =>
        {
            try { _singleInstanceMutex?.ReleaseMutex(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] Mutex release failed: {ex.Message}");
            }
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
        };
        _window.Activate();
    }

    private static string ResolveLaunchArguments(string? activatedArguments)
    {
        if (!string.IsNullOrWhiteSpace(activatedArguments))
        {
            return activatedArguments;
        }

        string[] processArguments = Environment.GetCommandLineArgs();
        for (int index = 1; index < processArguments.Length - 1; index++)
        {
            if (string.Equals(processArguments[index], "--ru-harness-session", StringComparison.Ordinal))
            {
                return $"--ru-harness-session \"{processArguments[index + 1]}\"";
            }
        }

        return string.Empty;
    }

    private static void ActivateExistingInstance()
    {
        try
        {
            var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
            var processes = System.Diagnostics.Process.GetProcessesByName("RuApp");
            IntPtr targetHwnd = IntPtr.Zero;

            foreach (var proc in processes)
            {
                if (proc.Id != currentProcess.Id && proc.MainWindowHandle != IntPtr.Zero)
                {
                    targetHwnd = proc.MainWindowHandle;
                    break;
                }
            }

            if (targetHwnd == IntPtr.Zero)
            {
                targetHwnd = FindWindowW("WinUIDesktopWin32WindowClass", null);
                if (targetHwnd == IntPtr.Zero)
                {
                    targetHwnd = FindWindowW(null, "Ru — Conversational Runtime");
                }
            }

            if (targetHwnd != IntPtr.Zero)
            {
                ShowWindow(targetHwnd, 9); // SW_RESTORE
                SetForegroundWindow(targetHwnd);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] Activate existing instance failed: {ex.Message}");
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? className, string? windowName);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
