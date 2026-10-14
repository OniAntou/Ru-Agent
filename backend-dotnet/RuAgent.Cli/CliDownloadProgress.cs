using System.Diagnostics;
using System.Runtime.InteropServices;
using RuAgent.Cli.Tui;

namespace RuAgent.Cli;

internal sealed class CliDownloadProgress : IProgress<double>, IDisposable
{
    private readonly TextWriter _output;
    private readonly long _totalBytes;
    private readonly bool _interactive;
    private readonly bool _ansi;
    private readonly IntPtr _consoleHandle;
    private readonly uint? _originalMode;
    private readonly Stopwatch _timer = Stopwatch.StartNew();
    private int _lastPercent = -1;
    private bool _disposed;

    public CliDownloadProgress(TextWriter output, long totalBytes, bool interactive)
    {
        _output = output;
        _totalBytes = totalBytes;
        _interactive = interactive;
        if (interactive && ReferenceEquals(output, Console.Out) && OperatingSystem.IsWindows())
        {
            _consoleHandle = GetStdHandle(-11);
            if (GetConsoleMode(_consoleHandle, out var mode) && SetConsoleMode(_consoleHandle, mode | 4))
            {
                _originalMode = mode;
                _ansi = true;
            }
        }
    }

    public void Report(double value)
    {
        if (_disposed || !_interactive || !double.IsFinite(value)) return;
        var percent = (int)(Math.Clamp(value, 0, 1) * 100);
        if (percent <= _lastPercent) return;
        _lastPercent = percent;
        var filled = percent * 24 / 100;
        var fill = new string('█', filled);
        var rest = new string('░', 24 - filled);
        var bar = _ansi ? TuiPalette.Ok(fill) + TuiPalette.Quiet(rest) : fill + rest;
        var downloadedMb = _totalBytes * Math.Clamp(value, 0, 1) / (1024 * 1024);
        var totalMb = _totalBytes / (1024d * 1024);
        var speed = _timer.Elapsed.TotalSeconds > 0 ? downloadedMb / _timer.Elapsed.TotalSeconds : 0;
        _output.Write($"\r  Downloading  {bar}  {percent,3}%  {downloadedMb,6:0.0}/{totalMb:0.0} MB  {speed,5:0.0} MB/s   ");
        _output.Flush();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_lastPercent >= 0) _output.WriteLine(); }
        finally { if (_originalMode is { } mode) SetConsoleMode(_consoleHandle, mode); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);
}
