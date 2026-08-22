using RuAgent.Broker.Tools;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class ProcessTreeRunnerTests
{
    [Fact]
    public async Task Runner_bounds_output_and_reports_exit_status()
    {
        var result = await new ProcessTreeRunner().RunAsync(
            new ProcessRunSpec(
                "cmd.exe",
                ["/c", "echo hello"],
                Directory.GetCurrentDirectory(),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                10_000,
                128),
            CancellationToken.None);

        Assert.Equal("succeeded", result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Runner_kills_a_timed_out_process_tree()
    {
        var result = await new ProcessTreeRunner().RunAsync(
            new ProcessRunSpec(
                "cmd.exe",
                ["/c", "ping 127.0.0.1 -n 10 > nul"],
                Directory.GetCurrentDirectory(),
                TestEnvironment(),
                50,
                128),
            CancellationToken.None);

        Assert.Equal("timed_out", result.Status);
    }

    private static IReadOnlyDictionary<string, string> TestEnvironment() =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
            ["SYSTEMROOT"] = Environment.GetEnvironmentVariable("SYSTEMROOT") ?? string.Empty,
        };
}
