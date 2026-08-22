namespace RuAgent.Broker.Runtime;

public sealed class BrokerCleanup
{
    private readonly object _gate = new();
    private readonly Func<CancellationToken, ValueTask> _stopListener;
    private readonly Action _cancelRequests;
    private readonly IReadOnlyList<Action> _killChildren;
    private readonly string _descriptorPath;
    private readonly string _sessionRoot;
    private Task? _runTask;

    public BrokerCleanup(
        Func<CancellationToken, ValueTask> stopListener,
        Action cancelRequests,
        IReadOnlyList<Action> killChildren,
        string descriptorPath,
        string sessionRoot)
    {
        _stopListener = stopListener ?? throw new ArgumentNullException(nameof(stopListener));
        _cancelRequests = cancelRequests ?? throw new ArgumentNullException(nameof(cancelRequests));
        _killChildren = killChildren ?? throw new ArgumentNullException(nameof(killChildren));
        _descriptorPath = Path.GetFullPath(descriptorPath);
        _sessionRoot = Path.GetFullPath(sessionRoot);

        var descriptorDirectory = Path.GetDirectoryName(_descriptorPath)
            ?? throw new ArgumentException("Descriptor path must have a directory.", nameof(descriptorPath));
        if (!IsUnderRoot(descriptorDirectory, _sessionRoot))
        {
            throw new ArgumentException("Session root escapes the descriptor directory.", nameof(sessionRoot));
        }
    }

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return _runTask ??= RunCoreAsync(cancellationToken);
        }
    }

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        await _stopListener(cancellationToken).ConfigureAwait(false);
        _cancelRequests();
        foreach (var killChild in _killChildren)
        {
            killChild();
        }

        if (File.Exists(_descriptorPath))
        {
            File.Delete(_descriptorPath);
        }

        if (Directory.Exists(_sessionRoot))
        {
            Directory.Delete(_sessionRoot, recursive: true);
        }
    }

    private static bool IsUnderRoot(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return !Path.IsPathRooted(relative)
            && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
}
