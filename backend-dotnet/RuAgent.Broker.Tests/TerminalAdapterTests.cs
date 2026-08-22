using RuAgent.Broker.Tools;
using RuAgent.Broker.Tools.Terminal;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class TerminalAdapterTests
{
    [Fact]
    public void Adapter_rejects_unregistered_commands_and_network_requests()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-terminal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var adapter = new TerminalAdapter(
                new WorkspacePathGuard(root),
                new ProcessTreeRunner(),
                new ShellRuntimeResolver());
            Assert.Throws<UnauthorizedAccessException>(() =>
            {
                _ = adapter.RunAsync(
                    new TerminalRunRequest("powershell", ".", "format.exe", [], 1_000),
                    CancellationToken.None);
            });
            Assert.Throws<UnauthorizedAccessException>(() =>
            {
                _ = adapter.RunAsync(
                    new TerminalRunRequest("powershell", ".", "rg", ["--json"], 1_000, Network: true),
                    CancellationToken.None);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Raw_shell_requires_a_bound_one_time_approval_token()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-terminal-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var approvals = new RuAgent.Broker.Policy.ApprovalTokenService();
            var argumentsHash = "script-hash";
            var token = approvals.Issue(
                "request-1",
                "session-1",
                "terminal.shell",
                "1.0.0",
                argumentsHash,
                TimeSpan.FromSeconds(30));
            var adapter = new TerminalAdapter(
                new WorkspacePathGuard(root),
                new ProcessTreeRunner(),
                new ShellRuntimeResolver(),
                approvals);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                adapter.RunApprovedShellAsync(
                    new TerminalShellRequest(
                        "powershell",
                        ".",
                        "Get-Date",
                        1_000,
                        token,
                        "request-1",
                        "session-1",
                        "different-hash"),
                    CancellationToken.None));

            var result = await adapter.RunApprovedShellAsync(
                new TerminalShellRequest(
                    "powershell",
                    ".",
                    "Get-Date",
                    1_000,
                    token,
                    "request-1",
                    "session-1",
                    argumentsHash),
                CancellationToken.None);
            Assert.Equal("succeeded", result.Status);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                adapter.RunApprovedShellAsync(
                    new TerminalShellRequest(
                        "powershell",
                        ".",
                        "Get-Date",
                        1_000,
                        token,
                        "request-1",
                        "session-1",
                        argumentsHash),
                    CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
