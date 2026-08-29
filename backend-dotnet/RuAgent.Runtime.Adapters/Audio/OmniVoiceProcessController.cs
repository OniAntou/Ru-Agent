using System.Diagnostics;

namespace RuAgent.Runtime.Adapters.Audio;

public sealed record OmniVoiceProcessOptions
{
    public OmniVoiceProcessOptions(
        string projectRoot,
        string pythonExecutable,
        string serverScript)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)
            || string.IsNullOrWhiteSpace(pythonExecutable)
            || string.IsNullOrWhiteSpace(serverScript))
        {
            throw new ArgumentException("OmniVoice process paths are required.");
        }

        ProjectRoot = Path.GetFullPath(projectRoot);
        PythonExecutable = Path.GetFullPath(pythonExecutable);
        ServerScript = Path.GetFullPath(serverScript);
    }

    public string ProjectRoot { get; }

    public string PythonExecutable { get; }

    public string ServerScript { get; }

    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 5_052;

    public void Validate()
    {
        if (!Directory.Exists(ProjectRoot)
            || !File.Exists(PythonExecutable)
            || !File.Exists(ServerScript)
            || Host != "127.0.0.1"
            || Port is < 1024 or > 65_535)
        {
            throw new ArgumentException("OmniVoice process configuration is invalid.");
        }

        if (IsReparsePoint(ProjectRoot)
            || IsReparsePoint(PythonExecutable)
            || IsReparsePoint(ServerScript))
        {
            throw new ArgumentException("OmniVoice process paths cannot be filesystem links.");
        }
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}

public interface IOmniVoiceProcessHandle : IDisposable
{
    bool HasExited { get; }

    void Kill();

    Task WaitForExitAsync(CancellationToken cancellationToken);
}

public interface IOmniVoiceProcessLauncher
{
    IOmniVoiceProcessHandle Start(OmniVoiceProcessOptions options);
}

public sealed class OmniVoiceProcessController : IAsyncDisposable
{
    private readonly OmniVoiceProcessOptions _options;
    private readonly IOmniVoiceProcessLauncher _launcher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IOmniVoiceProcessHandle? _process;
    private int _disposed;

    public OmniVoiceProcessController(
        OmniVoiceProcessOptions options,
        IOmniVoiceProcessLauncher? launcher = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _launcher = launcher ?? new WindowsOmniVoiceProcessLauncher();
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false })
            {
                return;
            }

            _process?.Dispose();
            _process = _launcher.Start(_options);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var process = _process;
            _process = null;
            if (process is null)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    wait.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                process.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Dispose();
        }
    }
}

public sealed class WindowsOmniVoiceProcessLauncher : IOmniVoiceProcessLauncher
{
    public IOmniVoiceProcessHandle Start(OmniVoiceProcessOptions options)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.PythonExecutable,
            WorkingDirectory = options.ProjectRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(options.ServerScript);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("OmniVoice provider process could not start.");
        }

        _ = DrainAsync(process.StandardOutput);
        _ = DrainAsync(process.StandardError);
        return new WindowsOmniVoiceProcessHandle(process);
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is not null)
            {
            }
        }
        catch
        {
            // Provider diagnostics stay private and never cross the runtime boundary.
        }
    }
}

public sealed class WindowsOmniVoiceProcessHandle : IOmniVoiceProcessHandle
{
    private readonly Process _process;

    public WindowsOmniVoiceProcessHandle(Process process)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
    }

    public bool HasExited => _process.HasExited;

    public void Kill()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        _process.WaitForExitAsync(cancellationToken);

    public void Dispose() => _process.Dispose();
}
