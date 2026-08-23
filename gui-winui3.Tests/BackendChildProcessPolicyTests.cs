using System.Text;
using System.Text.Json;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class BackendChildProcessPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ru-child-policy-" + Guid.NewGuid().ToString("N"));

    public BackendChildProcessPolicyTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Child_receives_role_environment_without_parent_credentials()
    {
        const string sentinelName = "RU_PARENT_SENTINEL_TEST";
        var previous = Environment.GetEnvironmentVariable(sentinelName);
        var previousProviderKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable(sentinelName, "must-not-inherit");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "must-not-inherit");
        var markerPath = Path.Combine(_root, "environment-result.txt");
        var script = $$"""
            $value = if ($env:{{sentinelName}} -or $env:OPENAI_API_KEY) { 'leaked' } elseif ($env:RU_REQUIRED_TEST -eq 'child-value' -and $env:SystemRoot) { 'ok' } else { 'missing' }
            [IO.File]::WriteAllText('{{PowerShellLiteral(markerPath)}}', $value)
            """;
        try
        {
            using var process = new WindowsBackendProcessFactory().Start(
                Spec(PowerShellArguments(script), new Dictionary<string, string>
                {
                    ["RU_REQUIRED_TEST"] = "child-value",
                }));

            Assert.True(process.WaitForExit(TimeSpan.FromSeconds(10)));
            Assert.True(SpinWait.SpinUntil(() => File.Exists(markerPath), TimeSpan.FromSeconds(2)));
            Assert.Equal("ok", File.ReadAllText(markerPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(sentinelName, previous);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previousProviderKey);
        }
    }

    [Fact]
    public void Raw_child_output_is_drained_but_only_a_structured_summary_is_persisted()
    {
        const string stdoutSecret = "raw-stdout-secret-control";
        const string stderrSecret = "raw-stderr-secret-control";
        var spec = Spec(PowerShellArguments(
            $"[Console]::Out.WriteLine('{stdoutSecret}'); [Console]::Error.WriteLine('{stderrSecret}')"));

        using var process = new WindowsBackendProcessFactory().Start(spec);
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(10)));
        Assert.True(SpinWait.SpinUntil(
            () => File.Exists(spec.StandardOutputLogPath)
                && File.Exists(spec.StandardErrorLogPath)
                && new FileInfo(spec.StandardOutputLogPath).Length > 0
                && new FileInfo(spec.StandardErrorLogPath).Length > 0,
            TimeSpan.FromSeconds(2)));

        AssertSummary(spec.StandardOutputLogPath, "stdout", stdoutSecret);
        AssertSummary(spec.StandardErrorLogPath, "stderr", stderrSecret);
    }

    [Fact]
    public async Task Summary_counters_saturate_without_stopping_the_drain()
    {
        var path = Path.Combine(_root, "saturated.jsonl");
        using var reader = new StringReader("first\nsecond\nthird\n");

        await BackendChildProcessPolicy.DrainToSummaryAsync(
            reader,
            path,
            "python-runtime-worker",
            "stdout",
            maxLineCount: 2,
            maxByteCount: 8);

        Assert.Equal(-1, reader.Peek());
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var summary = document.RootElement;
        Assert.Equal(2, summary.GetProperty("line_count").GetInt64());
        Assert.Equal(8, summary.GetProperty("byte_count").GetInt64());
        Assert.True(summary.GetProperty("truncated").GetBoolean());
        Assert.DoesNotContain("first", File.ReadAllText(path), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private BackendProcessSpec Spec(
        string arguments,
        IReadOnlyDictionary<string, string>? environment = null) => new(
        "python-runtime-worker",
        "powershell.exe",
        arguments,
        _root,
        environment ?? new Dictionary<string, string>(),
        Path.Combine(_root, "backend_worker_out.jsonl"),
        Path.Combine(_root, "backend_worker_err.jsonl"));

    private static void AssertSummary(string path, string stream, string rawSecret)
    {
        var persisted = File.ReadAllText(path);
        Assert.DoesNotContain(rawSecret, persisted, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(persisted);
        var root = document.RootElement;
        Assert.Equal("python-runtime-worker", root.GetProperty("role").GetString());
        Assert.Equal(stream, root.GetProperty("stream").GetString());
        Assert.True(root.GetProperty("line_count").GetInt64() >= 1);
        Assert.True(root.GetProperty("byte_count").GetInt64() >= 1);
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.True(root.TryGetProperty("ts", out _));
    }

    private static string PowerShellArguments(string script) =>
        "-NoProfile -NonInteractive -EncodedCommand "
        + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    private static string PowerShellLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
