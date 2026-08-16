namespace RuApp.Services;

public sealed record BackendProcessSpec(
    string Role,
    string FileName,
    string Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    string StandardOutputLogPath,
    string StandardErrorLogPath);

public sealed class BackendLaunchPlan
{
    private BackendLaunchPlan(
        int publicPort,
        int workerPort,
        IReadOnlyList<BackendProcessSpec> children)
    {
        PublicPort = publicPort;
        WorkerPort = workerPort;
        Children = children;
    }

    public int PublicPort { get; }
    public int WorkerPort { get; }
    public IReadOnlyList<BackendProcessSpec> Children { get; }

    public static BackendLaunchPlan Create(
        string projectRoot,
        string authSecret,
        string workerSecret,
        int workerPort,
        string? edgeExecutable = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(authSecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerSecret);

        const int publicPort = 8000;
        if (workerPort is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(workerPort));
        }

        var root = Path.GetFullPath(projectRoot);
        var pythonExecutable = Path.Combine(root, ".venv", "Scripts", "python.exe");
        if (!File.Exists(pythonExecutable))
        {
            pythonExecutable = "python.exe";
        }

        var logRoot = Path.Combine(root, "logs");
        if (workerPort == publicPort)
        {
            throw new ArgumentException(
                "The ASP.NET Core worker port must differ from public port 8000.",
                nameof(workerPort));
        }

        var publishedEdge = edgeExecutable;
        if (string.IsNullOrWhiteSpace(publishedEdge))
        {
            publishedEdge = Path.Combine(
                root,
                "backend-dotnet",
                "RuBackend",
                "bin",
                "Release",
                "net10.0",
                "win-x64",
                "publish",
                "RuBackend.exe");
        }

        publishedEdge = Path.GetFullPath(publishedEdge);
        if (!File.Exists(publishedEdge))
        {
            throw new FileNotFoundException(
                $"Published RuBackend.exe was not found at '{publishedEdge}'. Run build.bat before selecting ASP.NET Core mode.",
                publishedEdge);
        }

        var workerEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RU_WORKER_TOKEN"] = workerSecret,
        };
        var edgeEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RU_DESKTOP_TOKEN"] = authSecret,
            ["RU_WORKER_TOKEN"] = workerSecret,
            ["RuBackend__BindUrl"] = $"http://127.0.0.1:{publicPort}",
            ["RuBackend__WorkerUrl"] = $"ws://127.0.0.1:{workerPort}/worker/v1",
        };
        var children = new List<BackendProcessSpec>
        {
            new(
                "python-runtime-worker",
                pythonExecutable,
                $"-m ru_runtime.worker.v1.server --host 127.0.0.1 --port {workerPort}",
                root,
                workerEnvironment,
                Path.Combine(logRoot, "backend_worker_out.log"),
                Path.Combine(logRoot, "backend_worker_err.log")),
        };
        children.Add(
            new BackendProcessSpec(
                "aspnetcore-edge",
                publishedEdge,
                $"--urls http://127.0.0.1:{publicPort}",
                root,
                edgeEnvironment,
                Path.Combine(logRoot, "backend_aspnetcore_out.log"),
                Path.Combine(logRoot, "backend_aspnetcore_err.log")));

        return new BackendLaunchPlan(publicPort, workerPort, children);
    }
}
