using System;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

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
            try
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_crash.txt"),
                    e.Exception.ToString());
            }
            catch { }

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
        const string mutexName = "RuApp_WinUI3_SingleInstance";
        _singleInstanceMutex = new Mutex(true, mutexName, out bool createdNew);
        if (!createdNew)
        {
            ActivateExistingInstance();
            Environment.Exit(0);
            return;
        }

        _window = new MainWindow();
        _window.Activate();
    }

    private static void ActivateExistingInstance()
    {
        try
        {
            IntPtr hwnd = FindWindowW(null, "Ru — Local LFM2.5 Conversational Runtime");
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
