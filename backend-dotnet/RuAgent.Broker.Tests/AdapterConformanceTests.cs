using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools;
using RuAgent.Broker.Tools.Desktop;
using RuAgent.Broker.Tools.Terminal;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class AdapterConformanceTests
{
    [Fact]
    public async Task Temporary_workspace_adapters_round_trip_and_cleanup()
    {
        string root = TemporaryRoot();
        try
        {
            var guard = new WorkspacePathGuard(root);
            var files = new FileTools(guard);
            files.Write("data/value.txt", "bounded", overwrite: false);

            JsonObject read = files.Read("data/value.txt");
            var terminal = new TerminalAdapter(
                guard,
                new ShellRuntimeResolver());
            StreamingProcessResult result = await terminal.RunAsync(
                MakeGrant(),
                new TerminalRunRequest("dotnet", ".", "dotnet", ["--version"], 10_000),
                confineWorkspace: true,
                CancellationToken.None);

            Assert.Equal("bounded", read["content"]!.GetValue<string>());
            Assert.Equal("succeeded", result.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task Process_results_never_promote_nonzero_timeout_or_start_failure()
    {
        string root = TemporaryRoot();
        try
        {
            var runner = new ProcessTreeRunner();
            ProcessRunResult nonzero = await runner.RunAsync(
                Spec("cmd.exe", ["/c", "exit", "7"], root, 5_000),
                CancellationToken.None);
            ProcessRunResult timeout = await runner.RunAsync(
                Spec("cmd.exe", ["/c", "ping", "127.0.0.1", "-n", "10"], root, 50),
                CancellationToken.None);
            ProcessRunResult startFailure = await runner.RunAsync(
                Spec("ru-harness-missing-executable.exe", [], root, 5_000),
                CancellationToken.None);

            Assert.Equal("failed", nonzero.Status);
            Assert.Equal(7, nonzero.ExitCode);
            Assert.Equal("timed_out", timeout.Status);
            Assert.Equal("failed", startFailure.Status);
            Assert.Null(startFailure.ExitCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string TemporaryRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "ru-adapter-conformance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static PolicyGrant MakeGrant() => new(
        "session-conformance-" + Guid.NewGuid().ToString("N"),
        new HashSet<string>([ToolScope.TerminalExecute], StringComparer.Ordinal),
        DateTimeOffset.UtcNow.AddMinutes(30));

    private static ProcessRunSpec Spec(
        string executable,
        IReadOnlyList<string> arguments,
        string root,
        int timeoutMs) => new(
            executable,
            arguments,
            root,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
                ["PATHEXT"] = Environment.GetEnvironmentVariable("PATHEXT") ?? string.Empty,
                ["SYSTEMROOT"] = Environment.GetEnvironmentVariable("SYSTEMROOT") ?? string.Empty,
            },
            timeoutMs,
            4_096);

}
