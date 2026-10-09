using System.Diagnostics;
using RuAgent.Protocol;

namespace RuAgent.Hosting;

public interface IBrokerChildProcessFactory
{
    IBrokerChildProcessHandle Start(BrokerChildProcessSpec spec);
}

public interface IBrokerChildProcessHandle : IProcessHandle, IDisposable
{
    new bool HasExited { get; }
    void Kill();
    bool WaitForExit(TimeSpan timeout);

    /// <summary>
    /// Lets a process that was created suspended run. Called by the manager once the
    /// process has been assigned to the job, so nothing it creates can escape it.
    /// </summary>
    void Resume();
}

public interface IBrokerChildJobFactory
{
    OwnedProcessTree Create();
}

public interface IBrokerChildReadinessProbe
{
    Task<bool> WaitUntilReadyAsync(int brokerPort, CancellationToken cancellationToken);
}

public sealed class BrokerChildProcessManager : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly IBrokerChildProcessFactory _processFactory;
    private readonly IBrokerChildJobFactory _jobFactory;
    private readonly IBrokerChildReadinessProbe _readinessProbe;
    private List<IBrokerChildProcessHandle> _children = new();
    private OwnedProcessTree? _job;
    private string? _brokerDescriptorPath;
    private string? _brokerSessionRoot;
    private bool _disposed;

    public event EventHandler? Exited;

    public BrokerChildProcessManager(
        IBrokerChildProcessFactory? processFactory = null,
        IBrokerChildJobFactory? jobFactory = null,
        IBrokerChildReadinessProbe? readinessProbe = null)
    {
        _processFactory = processFactory ?? new WindowsBrokerChildProcessFactory();
        _jobFactory = jobFactory ?? new WindowsBrokerChildJobFactory();
        _readinessProbe = readinessProbe ?? new TcpReadinessProbe();
    }

    public int BrokerPort { get; private set; }
    public string? BrokerDescriptorPath { get; private set; }
    public string? BrokerSessionRoot { get; private set; }
    public string? LastError { get; private set; }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _children.Count > 0 && _children.All(IsAlive);
            }
        }
    }

    public async Task<bool> StartAsync(
        string projectRoot,
        BrokerChildStartupOptions startup,
        CancellationToken cancellationToken = default)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await StartCoreAsync(projectRoot, startup, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task<bool> StartCoreAsync(
        string projectRoot,
        BrokerChildStartupOptions startup,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startup);
        List<IBrokerChildProcessHandle>? staleChildren = null;
        OwnedProcessTree? staleJob = null;
        string? staleDescriptorPath = null;
        string? staleSessionRoot = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_children.Count > 0)
            {
                if (_children.All(IsAlive))
                {
                    return true;
                }

                // A child can exit between the watcher wake-up and a concurrent
                // StartAsync call. Reclaim that stale generation while holding the
                // start gate, otherwise the stale handles would block restarts.
                staleChildren = _children;
                _children = new List<IBrokerChildProcessHandle>();
                staleJob = _job;
                _job = null;
                staleDescriptorPath = _brokerDescriptorPath;
                staleSessionRoot = _brokerSessionRoot;
                _brokerDescriptorPath = null;
                _brokerSessionRoot = null;
                BrokerDescriptorPath = null;
                BrokerSessionRoot = null;
            }
            LastError = null;
        }

        if (staleChildren is not null)
        {
            Cleanup(staleChildren, staleJob, staleDescriptorPath, staleSessionRoot);
        }

        var startedChildren = new List<IBrokerChildProcessHandle>();
        OwnedProcessTree? job = null;
        BrokerChildLaunchPlan? plan = null;
        try
        {
            // A freshly probed dynamic Broker port can be taken by another
            // process in the window between the probe releasing its listener
            // and the child binding the port. That collision kills the child
            // quickly, so a readiness failure *with a dead child* retries with
            // a newly probed port -- bounded, and only while the Broker port
            // was not explicitly configured (a fixed port cannot be re-probed).
            var maxAttempts = startup.BrokerPort is null ? 3 : 1;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                plan = BrokerChildLaunchPlan.Create(projectRoot, startup);
                job = _jobFactory.Create();
                foreach (var spec in plan.Children)
                {
                    // Created suspended: the child runs no code until Resume, so every
                    // descendant it goes on to create is already inside this job. Handing a
                    // running child to Assign instead left a window in which it could spawn
                    // one that belonged to no job this application closes, and that
                    // descendant then outlived the run.
                    var child = _processFactory.Start(spec);
                    startedChildren.Add(child);
                    job.Assign(child);
                    child.Resume();
                }

                if (await _readinessProbe.WaitUntilReadyAsync(plan.BrokerPort, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }

                var deadChild = startedChildren.Any(child => !IsAlive(child));
                if (attempt == maxAttempts || !deadChild)
                {
                    // The child still alive but never listening is not a port
                    // collision, and a configured port has nothing to re-probe:
                    // surface the failure exactly as before.
                    throw new InvalidOperationException("Broker did not become ready.");
                }

                LastError = "Broker lost a port race during startup; retrying with a freshly probed port.";
                Cleanup(startedChildren, job, plan);
                startedChildren.Clear();
                job = null;
                plan = null;
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }

            // Every loop exit that reaches here broke out of a successful attempt.
            if (plan is null || job is null)
            {
                throw new InvalidOperationException("Broker did not become ready.");
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    Cleanup(startedChildren, job, plan);
                    return false;
                }
                _children = startedChildren;
                _job = job;
                _brokerDescriptorPath = plan.BrokerDescriptorPath;
                _brokerSessionRoot = plan.BrokerSessionRoot;
                BrokerPort = plan.BrokerPort;
                BrokerDescriptorPath = plan.BrokerDescriptorPath;
                BrokerSessionRoot = plan.BrokerSessionRoot;
                job = null;
            }
            _ = WatchForExitAsync(startedChildren);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LastError = "Startup canceled.";
            Cleanup(startedChildren, job, plan);
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex is FileNotFoundException ? ex.Message : $"Could not start Broker: {ex.Message}";
            Cleanup(startedChildren, job, plan);
            return false;
        }
    }

    private async Task WatchForExitAsync(IReadOnlyList<IBrokerChildProcessHandle> children)
    {
        try
        {
            var tasks = children.Where(IsAlive).Select(c => Task.Run(() => c.WaitForExit(Timeout.InfiniteTimeSpan))).ToList();
            if (tasks.Count > 0) await Task.WhenAny(tasks).ConfigureAwait(false);
            await _startGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                List<IBrokerChildProcessHandle> exitedChildren;
                OwnedProcessTree? job;
                string? descriptorPath;
                string? sessionRoot;
                lock (_gate)
                {
                    if (_disposed || !ReferenceEquals(_children, children)) return;
                    exitedChildren = _children;
                    _children = new List<IBrokerChildProcessHandle>();
                    job = _job;
                    _job = null;
                    descriptorPath = _brokerDescriptorPath;
                    sessionRoot = _brokerSessionRoot;
                    _brokerDescriptorPath = null;
                    _brokerSessionRoot = null;
                    BrokerDescriptorPath = null;
                    BrokerSessionRoot = null;
                    LastError = "Broker exited unexpectedly.";
                }

                Cleanup(exitedChildren, job, descriptorPath, sessionRoot);
                Exited?.Invoke(this, EventArgs.Empty);
            }
            finally
            {
                _startGate.Release();
            }
        }
        catch { }
    }

    public void Dispose()
    {
        List<IBrokerChildProcessHandle> children;
        OwnedProcessTree? job;
        string? descriptorPath;
        string? sessionRoot;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            children = _children;
            _children = new List<IBrokerChildProcessHandle>();
            job = _job;
            _job = null;
            descriptorPath = _brokerDescriptorPath;
            sessionRoot = _brokerSessionRoot;
            _brokerDescriptorPath = null;
            _brokerSessionRoot = null;
        }
        Cleanup(children, job, descriptorPath, sessionRoot);
        BrokerDescriptorPath = null;
        BrokerSessionRoot = null;
    }

    private static void Cleanup(IEnumerable<IBrokerChildProcessHandle> children, OwnedProcessTree? job, BrokerChildLaunchPlan? plan)
        => Cleanup(children, job, plan?.BrokerDescriptorPath, plan?.BrokerSessionRoot);

    private static void Cleanup(IEnumerable<IBrokerChildProcessHandle> children, OwnedProcessTree? job, string? descriptorPath, string? sessionRoot)
    {
        var list = children.Reverse().ToList();
        foreach (var child in list)
        {
            try { child.Kill(); } catch { }
            try { child.WaitForExit(TimeSpan.FromMilliseconds(500)); } catch { }
            try { child.Dispose(); } catch { }
        }
        try { job?.Dispose(); } catch { }
        DeleteOwnedArtifacts(descriptorPath, sessionRoot);
    }

    private static bool IsAlive(IBrokerChildProcessHandle child)
    {
        try
        {
            return !child.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteOwnedArtifacts(string? descriptorPath, string? sessionRoot)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(descriptorPath) && File.Exists(descriptorPath))
                File.Delete(descriptorPath);
            if (IsOwnedSessionRoot(sessionRoot) && Directory.Exists(sessionRoot))
                Directory.Delete(sessionRoot, recursive: true);
        }
        catch { }
    }

    private static bool IsOwnedSessionRoot(string? sessionRoot)
    {
        if (string.IsNullOrWhiteSpace(sessionRoot))
        {
            return false;
        }

        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(sessionRoot);
        }
        catch
        {
            return false;
        }

        try
        {
            if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        var leaf = Path.GetFileName(fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.Equals(leaf, "session", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parent = Path.GetDirectoryName(fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return parent is not null
            && string.Equals(Path.GetFileName(parent), "agent", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class WindowsBrokerChildProcessFactory : IBrokerChildProcessFactory
{
    public IBrokerChildProcessHandle Start(BrokerChildProcessSpec spec)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(spec.StandardOutputLogPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(spec.StandardErrorLogPath)!);
        var startInfo = ChildProcessPolicy.CreateStartInfo(spec);
        var started = SuspendedProcess.StartSuspended(startInfo);
        try
        {
            _ = DrainChildOutputAsync(started, spec);
            return new WindowsBrokerChildProcessHandle(started);
        }
        catch
        {
            started.Process.Dispose();
            started.ThreadHandle.Dispose();
            started.StandardOutput.Dispose();
            started.StandardError.Dispose();
            throw;
        }
    }

    private static async Task DrainChildOutputAsync(SuspendedProcess.StartedProcess started, BrokerChildProcessSpec spec)
    {
        try
        {
            await Task.WhenAll(
                ChildProcessPolicy.DrainToSummaryAsync(started.StandardOutput, spec.StandardOutputLogPath, spec.Role, "stdout"),
                ChildProcessPolicy.DrainToSummaryAsync(started.StandardError, spec.StandardErrorLogPath, spec.Role, "stderr"));
        }
        catch { }
    }
}

public sealed class WindowsBrokerChildProcessHandle : IBrokerChildProcessHandle
{
    private readonly SuspendedProcess.StartedProcess _started;
    private bool _resumed;

    internal WindowsBrokerChildProcessHandle(SuspendedProcess.StartedProcess started) => _started = started;

    public IntPtr Handle => _started.Process.Handle;
    public bool HasExited => _started.Process.HasExited;
    public void Kill() { if (!_started.Process.HasExited) _started.Process.Kill(entireProcessTree: true); }

    public void Resume()
    {
        if (_resumed)
        {
            return;
        }

        _resumed = true;
        SuspendedProcess.Resume(_started.ThreadHandle);
    }

    public bool WaitForExit(TimeSpan timeout)
    {
        var ms = timeout == Timeout.InfiniteTimeSpan ? Timeout.Infinite : (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);
        return _started.Process.WaitForExit(ms);
    }

    public void Dispose()
    {
        _started.ThreadHandle.Dispose();
        _started.StandardOutput.Dispose();
        _started.StandardError.Dispose();
        _started.Process.Dispose();
    }
}

public sealed class WindowsBrokerChildJobFactory : IBrokerChildJobFactory
{
    public OwnedProcessTree Create() => new OwnedProcessTree();
}

/// <summary>
/// Probes the Broker endpoint with a real TCP connect; the host must not publish a
/// runtime descriptor while the Broker has died or never started listening.
/// </summary>
public sealed class TcpReadinessProbe : IBrokerChildReadinessProbe
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollGap = TimeSpan.FromMilliseconds(250);

    public async Task<bool> WaitUntilReadyAsync(int brokerPort, CancellationToken cancellationToken)
    {
        var budget = Stopwatch.StartNew();
        while (!cancellationToken.IsCancellationRequested && budget.Elapsed < TotalBudget)
        {
            if (await CanConnectAsync(brokerPort, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(PollGap, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task<bool> CanConnectAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            await client.ConnectAsync(System.Net.IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (Exception error) when (error is System.Net.Sockets.SocketException or OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }
    }
}
