using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools;
using RuAgent.Broker.Tests.Support;
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
                guard);
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

    /// <summary>
    /// The bounded-status contract of the production terminal runner: a non-zero exit is
    /// never promoted to success, a killed command reports <c>timed_out</c> rather than a
    /// bare failure, and a command the shell cannot resolve still reports the real child
    /// exit code instead of being laundered into success.
    /// </summary>
    [Fact]
    public async Task Process_results_never_promote_nonzero_timeout_or_start_failure()
    {
        var runner = new StreamingProcessRunner();
        string root = TemporaryRoot();
        try
        {
            StreamingProcessResult nonzero = await runner.RunAsync(
                Spec("exit", "7", root),
                null,
                CancellationToken.None);
            StreamingProcessResult timeout = await runner.RunAsync(
                Spec("ping", "127.0.0.1 -n 10 > nul", root, timeoutMs: 50),
                null,
                CancellationToken.None);
            // A command the shell cannot resolve is not a start failure: the runner must
            // report the real child exit code instead of laundering it into success, and
            // it must never fabricate a null exit code for a process that did run.
            StreamingProcessResult missingCommand = await runner.RunAsync(
                Spec("ru-harness-missing-" + Guid.NewGuid().ToString("N"), string.Empty, root),
                null,
                CancellationToken.None);

            Assert.Equal("failed", nonzero.Status);
            Assert.Equal(7, nonzero.ExitCode);
            Assert.Equal("timed_out", timeout.Status);
            Assert.Equal("failed", missingCommand.Status);
            Assert.NotNull(missingCommand.ExitCode);
            Assert.NotEqual(0, missingCommand.ExitCode);
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
        string command,
        string arguments,
        string root,
        int timeoutMs = 5_000) => new(
            Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            ["/d", "/s", "/c", string.Join(' ', command, arguments).Trim()],
            root,
            new AgentCommandEnvironment().ChildEnvironment,
            timeoutMs,
            4_096);
}
