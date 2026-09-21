using System.Diagnostics;

namespace RuAgent.Hosting;

public interface IWorkerBrokerProcessFactory
{
    IWorkerBrokerProcessHandle Start(WorkerBrokerProcessSpec spec);
}

public interface IWorkerBrokerProcessHandle : IProcessHandle, IDisposable
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

public interface IWorkerBrokerJobFactory
{
    OwnedProcessTree Create();
}

public interface IWorkerBrokerReadinessProbe
{
    Task<bool> WaitUntilReadyAsync(int agentWorkerPort, int brokerPort, CancellationToken cancellationToken);
}

public sealed class WorkerBrokerProcessManager : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly IWorkerBrokerProcessFactory _processFactory;
    private readonly IWorkerBrokerJobFactory _jobFactory;
    private readonly IWorkerBrokerReadinessProbe _readinessProbe;
    private List<IWorkerBrokerProcessHandle> _children = new();
    private OwnedProcessTree? _job;
    private string? _brokerDescriptorPath;
    private string? _brokerSessionRoot;
    private bool _disposed;

    public event EventHandler? Exited;

    public WorkerBrokerProcessManager(
        IWorkerBrokerProcessFactory? processFactory = null,
        IWorkerBrokerJobFactory? jobFactory = null,
        IWorkerBrokerReadinessProbe? readinessProbe = null)
    {
        _processFactory = processFactory ?? new WindowsWorkerBrokerProcessFactory();
        _jobFactory = jobFactory ?? new WindowsWorkerBrokerJobFactory();
        _readinessProbe = readinessProbe ?? new TcpReadinessProbe();
    }

    public int AgentWorkerPort { get; private set; }
    public int WorkerPort => AgentWorkerPort;
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
        WorkerBrokerStartupOptions startup,
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
        WorkerBrokerStartupOptions startup,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startup);
        List<IWorkerBrokerProcessHandle>? staleChildren = null;
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
                _children = new List<IWorkerBrokerProcessHandle>();
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

        var startedChildren = new List<IWorkerBrokerProcessHandle>();
        OwnedProcessTree? job = null;
        WorkerBrokerLaunchPlan? plan = null;
        try
        {
            plan = WorkerBrokerLaunchPlan.Create(
                projectRoot,
                startup);

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

            var ready = await _readinessProbe.WaitUntilReadyAsync(plan.AgentWorkerPort, plan.BrokerPort, cancellationToken).ConfigureAwait(false);
            if (!ready)
                throw new InvalidOperationException("Worker/Broker did not become ready.");

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
                AgentWorkerPort = plan.AgentWorkerPort;
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
            LastError = ex is FileNotFoundException ? ex.Message : $"Could not start Worker/Broker: {ex.Message}";
            Cleanup(startedChildren, job, plan);
            return false;
        }
    }

    private async Task WatchForExitAsync(IReadOnlyList<IWorkerBrokerProcessHandle> children)
    {
        try
        {
            var tasks = children.Where(IsAlive).Select(c => Task.Run(() => c.WaitForExit(Timeout.InfiniteTimeSpan))).ToList();
            if (tasks.Count > 0) await Task.WhenAny(tasks).ConfigureAwait(false);
            await _startGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                List<IWorkerBrokerProcessHandle> exitedChildren;
                OwnedProcessTree? job;
                string? descriptorPath;
                string? sessionRoot;
                lock (_gate)
                {
                    if (_disposed || !ReferenceEquals(_children, children)) return;
                    exitedChildren = _children;
                    _children = new List<IWorkerBrokerProcessHandle>();
                    job = _job;
                    _job = null;
                    descriptorPath = _brokerDescriptorPath;
                    sessionRoot = _brokerSessionRoot;
                    _brokerDescriptorPath = null;
                    _brokerSessionRoot = null;
                    BrokerDescriptorPath = null;
                    BrokerSessionRoot = null;
                    LastError = "Worker/Broker exited unexpectedly.";
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
        List<IWorkerBrokerProcessHandle> children;
        OwnedProcessTree? job;
        string? descriptorPath;
        string? sessionRoot;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            children = _children;
            _children = new List<IWorkerBrokerProcessHandle>();
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

    private static void Cleanup(IEnumerable<IWorkerBrokerProcessHandle> children, OwnedProcessTree? job, WorkerBrokerLaunchPlan? plan)
        => Cleanup(children, job, plan?.BrokerDescriptorPath, plan?.BrokerSessionRoot);

    private static void Cleanup(IEnumerable<IWorkerBrokerProcessHandle> children, OwnedProcessTree? job, string? descriptorPath, string? sessionRoot)
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

    private static bool IsAlive(IWorkerBrokerProcessHandle child)
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

public sealed class WindowsWorkerBrokerProcessFactory : IWorkerBrokerProcessFactory
{
    public IWorkerBrokerProcessHandle Start(WorkerBrokerProcessSpec spec)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(spec.StandardOutputLogPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(spec.StandardErrorLogPath)!);
        var startInfo = ChildProcessPolicy.CreateStartInfo(spec);
        var started = SuspendedProcess.StartSuspended(startInfo);
        try
        {
            _ = DrainChildOutputAsync(started, spec);
            return new WindowsWorkerBrokerProcessHandle(started);
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

    private static async Task DrainChildOutputAsync(SuspendedProcess.StartedProcess started, WorkerBrokerProcessSpec spec)
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

public sealed class WindowsWorkerBrokerProcessHandle : IWorkerBrokerProcessHandle
{
    private readonly SuspendedProcess.StartedProcess _started;
    private bool _resumed;

    internal WindowsWorkerBrokerProcessHandle(SuspendedProcess.StartedProcess started) => _started = started;

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

public sealed class WindowsWorkerBrokerJobFactory : IWorkerBrokerJobFactory
{
    public OwnedProcessTree Create() => new OwnedProcessTree();
}

/// <summary>
/// Probes both child endpoints with real TCP connects; the host must not publish a
/// runtime descriptor while a child has died or never started listening.
/// </summary>
public sealed class TcpReadinessProbe : IWorkerBrokerReadinessProbe
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollGap = TimeSpan.FromMilliseconds(250);

    public async Task<bool> WaitUntilReadyAsync(int agentWorkerPort, int brokerPort, CancellationToken cancellationToken)
    {
        var budget = Stopwatch.StartNew();
        while (!cancellationToken.IsCancellationRequested && budget.Elapsed < TotalBudget)
        {
            var agentOk = await CanConnectAsync(agentWorkerPort, cancellationToken).ConfigureAwait(false);
            var brokerOk = await CanConnectAsync(brokerPort, cancellationToken).ConfigureAwait(false);
            if (agentOk && brokerOk)
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
