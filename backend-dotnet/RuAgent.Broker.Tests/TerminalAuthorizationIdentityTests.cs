using System.Diagnostics;
using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools;
using RuAgent.Broker.Tools.Terminal;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

[Collection(nameof(WindowsProcessCollection))]
public sealed class TerminalAuthorizationIdentityTests
{
    [Theory]
    [InlineData("deny")]
    [InlineData("ask")]
    public void Restrictive_absolute_executable_rules_still_match_the_requested_alias(string decision)
    {
        var canonical = Path.GetFullPath("canonical-runner.exe");
        var alias = Path.GetFullPath("alias-runner.exe");
        var context = new PolicyContext("run_process", "1.0.0", Path.GetFullPath("."), null, null,
            canonical, [], Path.GetFullPath("."), "terminal", "none", false, new string('a', 64))
        {
            RequestedExecutable = alias,
        };
        var matcher = new MatcherSpec(null, null, null, alias, [], null, null, null);
        var rule = new PolicyRuleSpec("restrict-alias", "project", decision, matcher, 1, null);

        Assert.True(PolicyRuleMatcher.Matches(rule, context, DateTimeOffset.UtcNow));
        Assert.False(PolicyRuleMatcher.Matches(rule with { Decision = "allow" }, context, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Bare_executable_rules_match_the_requested_label_and_full_paths_match_the_canonical_target()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "ru-policy-executable-rules-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var lease = new BrokerRunLease("run", "session", "workspace", root, "v", "f", new string('a', 64),
                "standard", 1, new string('b', 64), DateTimeOffset.UtcNow.AddMinutes(5), ["run_process"], null!, 1);
            var factory = new PolicyContextFactory(lease);
            var bareRequest = factory.Create("run_process", "1.0.0", new JsonObject
            {
                ["executable"] = "cmd",
            });
            var explicitExtensionRequest = factory.Create("run_process", "1.0.0", new JsonObject
            {
                ["executable"] = "cmd.exe",
            });

            Assert.Equal("cmd", bareRequest.RequestedExecutable);
            Assert.True(MatchesExecutableRule("cmd", bareRequest));
            Assert.Equal("cmd.exe", explicitExtensionRequest.RequestedExecutable);
            Assert.False(MatchesExecutableRule("cmd", explicitExtensionRequest));
            Assert.True(MatchesExecutableRule("cmd.exe", explicitExtensionRequest));
            Assert.True(MatchesExecutableRule(explicitExtensionRequest.ResolvedExecutable!, explicitExtensionRequest));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Approval_identity_changes_when_a_relative_executable_link_is_retargeted()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "ru-policy-executable-" + Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "runner.cmd"), "@echo first\r\n");
        File.WriteAllText(Path.Combine(second, "runner.cmd"), "@echo second\r\n");
        var link = Path.Combine(root, "bin");
        try
        {
            Link(link, first);
            var lease = new BrokerRunLease("run", "session", "workspace", root, "v", "f", new string('a', 64),
                "standard", 1, new string('b', 64), DateTimeOffset.UtcNow.AddMinutes(5), ["run_process"], null!, 1);
            var factory = new PolicyContextFactory(lease);
            var args = new JsonObject { ["executable"] = @"bin\runner.cmd" };
            var before = factory.Create("run_process", "1.0.0", args);
            var tokens = new ApprovalTokenService();
            var token = tokens.Issue("request", "session", "run_process", "1.0.0", before.IntentHash, TimeSpan.FromMinutes(1));
            Directory.Delete(link);
            Link(link, second);
            var after = factory.Create("run_process", "1.0.0", args);

            Assert.NotEqual(before.IntentHash, after.IntentHash);
            Assert.Equal(Path.Combine(first, "runner.cmd"), before.ResolvedExecutable, ignoreCase: true);
            Assert.Equal(Path.Combine(second, "runner.cmd"), after.ResolvedExecutable, ignoreCase: true);
            Assert.True(MatchesExecutableRule(before.ResolvedExecutable!, before));
            Assert.False(MatchesExecutableRule(Path.Combine(root, "bin", "runner.cmd"), before));
            Assert.False(tokens.TryConsume(token,
                new ApprovalBinding("request", "session", "run_process", "1.0.0", after.IntentHash, DateTimeOffset.UtcNow), out _));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Approved_dispatch_uses_the_authorized_executable_after_bin_junction_retarget()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "ru-terminal-authorized-dispatch-" + Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        var link = Path.Combine(root, "bin");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        var systemCmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var firstExecutable = Path.Combine(first, "runner.exe");
        var secondExecutable = Path.Combine(second, "runner.exe");

        try
        {
            File.Copy(systemCmd, firstExecutable);
            File.Copy(systemCmd, secondExecutable);
            Link(link, first);
            var adapter = new TerminalAdapter(
                new WorkspacePathGuard(root),
                intentResolver: new CommandIntentResolver(root));
            var request = new TerminalRunRequest(
                "cmd",
                ".",
                @"bin\runner.exe",
                ["/d", "/c", "echo", "%CMDCMDLINE%"],
                TimeoutMs: 10_000,
                Approved: true);
            var authorizedIntent = adapter.CanonicalizeRun(request);
            Assert.Equal(firstExecutable, authorizedIntent.ResolvedExecutable, ignoreCase: true);

            Directory.Delete(link);
            Link(link, second);

            var result = await adapter.RunAsync(
                new PolicyGrant("session", new HashSet<string>([ToolScope.TerminalExecute], StringComparer.Ordinal),
                    DateTimeOffset.UtcNow.AddMinutes(5)),
                request with { AuthorizedIntent = authorizedIntent },
                confineWorkspace: true,
                CancellationToken.None);

            Assert.Equal("succeeded", result.Status);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains(firstExecutable, result.StdoutHead, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secondExecutable, result.StdoutHead, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool MatchesExecutableRule(string pattern, PolicyContext context) => PolicyRuleMatcher.MatchesMatcher(
        new MatcherSpec(null, null, null, pattern, Array.Empty<string>(), null, null, null),
        context);

    private static void Link(string link, string target)
    {
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "/d", "/c", "mklink", "/J", link, target }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        Assert.True(process.WaitForExit(5_000));
        Assert.Equal(0, process.ExitCode);
    }
}
