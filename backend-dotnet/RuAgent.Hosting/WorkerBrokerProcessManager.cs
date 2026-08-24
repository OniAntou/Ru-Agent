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
}

public interface IWorkerBrokerJobFactory
{
    OwnedProcessTree Create();
}

public interface IWorkerBrokerReadinessProbe
{
    Task<bool> WaitUntilReadyAsync(int workerPort, int brokerPort, CancellationToken cancellationToken);
}

public sealed class WorkerBrokerProcessManager : IDisposable
{
    private readonly object _gate = new();
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
        _readinessProbe = readinessProbe ?? new NoopReadinessProbe();
    }

    public int WorkerPort { get; private set; }
    public int BrokerPort { get; private set; }
    public string? BrokerDescriptorPath { get; private set; }
    public string? BrokerSessionRoot { get; private set; }
    public string? LastError { get; private set; }

    public bool IsRunning
    {
        get { lock (_gate) return _children.Count > 0 && _children.All(c => !c.HasExited); }
    }

    public async Task<bool> StartAsync(
        string projectRoot,
        string workerSecret,
        int workerPort,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_children.Count > 0) return IsRunning;
            LastError = null;
        }

        var startedChildren = new List<IWorkerBrokerProcessHandle>();
        OwnedProcessTree? job = null;
        WorkerBrokerLaunchPlan? plan = null;
        try
        {
            plan = WorkerBrokerLaunchPlan.Create(
                projectRoot,
                workerSecret,
                workerPort,
                brokerPort: BrokerPort == 0 ? null : BrokerPort,
                brokerExecutable: Environment.GetEnvironmentVariable("RU_AGENT_BROKER_EXE"));

            job = _jobFactory.Create();
            foreach (var spec in plan.Children)
            {
                var child = _processFactory.Start(spec);
                startedChildren.Add(child);
                job.Assign(child);
            }

            var ready = await _readinessProbe.WaitUntilReadyAsync(plan.WorkerPort, plan.BrokerPort, cancellationToken).ConfigureAwait(false);
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
                WorkerPort = plan.WorkerPort;
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
            var tasks = children.Where(c => !c.HasExited).Select(c => Task.Run(() => c.WaitForExit(Timeout.InfiniteTimeSpan))).ToList();
            if (tasks.Count > 0) await Task.WhenAny(tasks).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(_children, children)) return;
            }
            Exited?.Invoke(this, EventArgs.Empty);
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

    private static void DeleteOwnedArtifacts(string? descriptorPath, string? sessionRoot)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(descriptorPath) && File.Exists(descriptorPath))
                File.Delete(descriptorPath);
            if (!string.IsNullOrWhiteSpace(sessionRoot) && Directory.Exists(sessionRoot))
                Directory.Delete(sessionRoot, recursive: true);
        }
        catch { }
    }
}

public sealed class WindowsWorkerBrokerProcessFactory : IWorkerBrokerProcessFactory
{
    public IWorkerBrokerProcessHandle Start(WorkerBrokerProcessSpec spec)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(spec.StandardOutputLogPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(spec.StandardErrorLogPath)!);
        var startInfo = ChildProcessPolicy.CreateStartInfo(spec);
        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"Could not start {spec.Role}.");
            _ = DrainChildOutputAsync(process, spec);
            return new WindowsWorkerBrokerProcessHandle(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static async Task DrainChildOutputAsync(Process process, WorkerBrokerProcessSpec spec)
    {
        try
        {
            await Task.WhenAll(
                ChildProcessPolicy.DrainToSummaryAsync(process.StandardOutput, spec.StandardOutputLogPath, spec.Role, "stdout"),
                ChildProcessPolicy.DrainToSummaryAsync(process.StandardError, spec.StandardErrorLogPath, spec.Role, "stderr"));
        }
        catch { }
    }
}

public sealed class WindowsWorkerBrokerProcessHandle : IWorkerBrokerProcessHandle
{
    private readonly Process _process;
    public WindowsWorkerBrokerProcessHandle(Process process) => _process = process;
    public IntPtr Handle => _process.Handle;
    public bool HasExited => _process.HasExited;
    public void Kill() { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
    public bool WaitForExit(TimeSpan timeout)
    {
        var ms = timeout == Timeout.InfiniteTimeSpan ? Timeout.Infinite : (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);
        return _process.WaitForExit(ms);
    }
    public void Dispose() => _process.Dispose();
}

public sealed class WindowsWorkerBrokerJobFactory : IWorkerBrokerJobFactory
{
    public OwnedProcessTree Create() => new OwnedProcessTree();
}

public sealed class NoopReadinessProbe : IWorkerBrokerReadinessProbe
{
    public Task<bool> WaitUntilReadyAsync(int workerPort, int brokerPort, CancellationToken cancellationToken) => Task.FromResult(true);
}
