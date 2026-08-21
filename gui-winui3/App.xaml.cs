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
        this.UnhandledException += (s, e) =>
        {
            Services.GuiDiagnosticWriter.WriteCrashEvent(e.Exception);

            // Only swallow exceptions that are recoverable; let truly fatal
            // failures crash so bugs are not hidden behind an empty UI.
            var ex = e.Exception;
            e.Handled = ex is System.IO.IOException
                or System.Net.WebSockets.WebSocketException
                or System.Net.Http.HttpRequestException
                or System.Threading.Tasks.TaskCanceledException
                or System.ObjectDisposedException
                or System.ArgumentException
                or System.InvalidOperationException
                or System.Runtime.InteropServices.COMException
                or Microsoft.UI.Xaml.Markup.XamlParseException;
        };
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
            IntPtr hwnd = FindWindowW(null, "Ru — Conversational Runtime");
            if (hwnd != IntPtr.Zero)
            {
                ShowWindow(hwnd, 9); // SW_RESTORE
                SetForegroundWindow(hwnd);
            }
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? className, string? windowName);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
