using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Broker.Tests;

/// <summary>
/// Runtime-level behavior for the transport-neutral execution authority: the
/// single execution path production and the in-process gateway use. Covers
/// capabilities, v1 and v2 evaluation/execution, approvals and grants, run
/// lifecycle, session tasks, cancellation, gate serialization and the
/// full_access boundaries that the deleted wire suite exercised.
/// </summary>
[Collection(nameof(WindowsProcessCollection))]
public sealed class BrokerExecutionRuntimeTests
{
    [Theory]
    [InlineData("run")]
    [InlineData("shell")]
    public async Task Standard_workspace_terminal_authorization_runs_both_modes_without_user_approval(string mode)
    {
        using var fixture = new RuntimeFixture();
        const string sessionId = "standard-terminal-autonomy";
        const string runId = "run_standard_terminal_autonomy111111";
        var opened = await fixture.Runtime.OpenRunAsync(
            RunOpen(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardEmpty()), default);
        Assert.Null(opened.Error);
        var arguments = new JsonObject { ["mode"] = mode, ["working_directory"] = ".", ["network_intent"] = true };
        if (mode == "shell") arguments["script"] = "Write-Output 'autonomy;|&'";
        else
        {
            arguments["executable"] = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            arguments["arguments"] = new JsonArray("-NoProfile", "-NonInteractive", "-Command", "Write-Output 'autonomy;|&'");
        }
        var decision = await fixture.Runtime.EvaluateAsync(Evaluate("evaluate-autonomy", sessionId, runId, "bash", arguments), default);
        Assert.Equal("allow", decision.Decision!.Decision);
        Assert.Equal("workspace_terminal_allow", decision.Decision.ReasonCode);
        Assert.Null(decision.Decision.ApprovalId);
        Assert.False(decision.Decision.AnalyzerRiskPending);
        var execution = await fixture.Runtime.ExecuteAuthorizedAsync(
            Execute("execute-autonomy", sessionId, runId, "bash", decision.Decision.AuthorizationToken!, arguments), default);
        Assert.Equal(ToolResultStatuses.Succeeded, execution.Receipt!.Status);
        Assert.Contains("autonomy;|&", execution.Receipt.Output["stdout_head"]!.GetValue<string>(), StringComparison.Ordinal);
        var replay = await fixture.Runtime.ExecuteAuthorizedAsync(
            Execute("replay-autonomy", sessionId, runId, "bash", decision.Decision.AuthorizationToken!, arguments), default);
        Assert.Equal("decision_stale", replay.ErrorCode);
    }

    [Fact]
    public async Task Evaluate_then_execute_reads_a_workspace_file()
    {
        using var fixture = new RuntimeFixture();
        File.WriteAllText(Path.Combine(fixture.Root, "probe.txt"), "workspace-probe");
        const string sessionId = "session-runtime-read";
        const string runId = "run_runtime_read1111111111111111";

        var opened = await fixture.Runtime.OpenRunAsync(
            RunOpen(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardEmpty()),
            default);
        Assert.Null(opened.Error);
        Assert.Equal(runId, opened.RunId);

        var arguments = new JsonObject { ["path"] = "probe.txt" };
        var decision = await fixture.Runtime.EvaluateAsync(
            Evaluate("evaluate-runtime-read", sessionId, runId, "read", arguments),
            default);
        Assert.Null(decision.Error);
        Assert.NotNull(decision.Decision);
        Assert.Equal("allow", decision.Decision!.Decision);
        Assert.Equal("standard_workspace_allow", decision.Decision.ReasonCode);
        Assert.False(string.IsNullOrWhiteSpace(decision.Decision.AuthorizationToken));

        var execution = await fixture.Runtime.ExecuteAuthorizedAsync(
            Execute("execute-runtime-read", sessionId, runId, "read", decision.Decision.AuthorizationToken!, arguments),
            default);
        Assert.NotNull(execution.Receipt);
        Assert.Equal(ToolResultStatuses.Succeeded, execution.Receipt!.Status);
        Assert.True(execution.Receipt.AdapterInvoked);
        Assert.Equal("workspace-probe", execution.Receipt.Output["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Ask_approval_once_authorizes_execute_and_replayed_token_is_stale()
    {
        using var fixture = new RuntimeFixture();
        const string sessionId = "session-runtime-approval";
        const string runId = "run_runtime_approval11111111111";

        var opened = await fixture.Runtime.OpenRunAsync(
            RunOpen(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardWithRules(PolicyEvalSnapshots.Rule("ask-terminal", "ask", PolicyEvalSnapshots.Matcher(toolId: "bash")))),
            default);
        Assert.Null(opened.Error);

        var arguments = new JsonObject
        {
            ["mode"] = "run",
            ["executable"] = "where.exe",
            ["arguments"] = new JsonArray("dotnet"),
            ["working_directory"] = ".",
        };
        var decision = await fixture.Runtime.EvaluateAsync(
            Evaluate("evaluate-runtime-approval", sessionId, runId, "bash", arguments),
            default);
        Assert.Null(decision.Error);
        Assert.NotNull(decision.Decision);
        Assert.Equal("ask", decision.Decision!.Decision);
        Assert.Equal("rule_asks", decision.Decision.ReasonCode);
        Assert.False(string.IsNullOrWhiteSpace(decision.Decision.ApprovalId));

        var approval = await fixture.Runtime.RespondApprovalAsync(
            ApprovalRespond("approve-runtime-approval", sessionId, runId, decision.Decision.ApprovalId!, "allow_once"),
            default);
        Assert.Null(approval.Refusal);
        Assert.Equal("authorized", approval.Status);
        Assert.False(string.IsNullOrWhiteSpace(approval.AuthorizationToken));

        var execution = await fixture.Runtime.ExecuteAuthorizedAsync(
            Execute("execute-runtime-approval", sessionId, runId, "bash", approval.AuthorizationToken!, arguments),
            default);
        Assert.NotNull(execution.Receipt);
        Assert.Equal(ToolResultStatuses.Succeeded, execution.Receipt!.Status);
        Assert.Equal(0, execution.Receipt.Output["exit_code"]!.GetValue<int>());

        // The authorization token is one-time: the replay fails exactly like the
        // wire path's decision_stale, before any adapter work.
        var replay = await fixture.Runtime.ExecuteAuthorizedAsync(
            Execute("execute-runtime-replay", sessionId, runId, "bash", approval.AuthorizationToken!, arguments),
            default);
        Assert.Null(replay.Receipt);
        Assert.Equal("decision_stale", replay.ErrorCode);
        Assert.Equal("authorization is no longer valid", replay.SafeSummary);
    }

    [Fact]
    public async Task Session_close_drops_session_grants_and_pending_approvals()
    {
        using var fixture = new RuntimeFixture();
        var external = Path.Combine(Path.GetTempPath(), "ru-runtime-external-" + Guid.NewGuid().ToString("N"));
        var externalNext = Path.Combine(Path.GetTempPath(), "ru-runtime-external-next-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        Directory.CreateDirectory(externalNext);
        File.WriteAllText(Path.Combine(external, "probe.txt"), "external");
        File.WriteAllText(Path.Combine(externalNext, "probe.txt"), "external-next");
        try
        {
            const string sessionId = "session-runtime-close";
            const string runId = "run_runtime_close11111111111111111";

            var opened = await fixture.Runtime.OpenRunAsync(
                RunOpen(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardEmpty()),
                default);
            Assert.Null(opened.Error);

            var firstAsk = await fixture.Runtime.EvaluateAsync(
                Evaluate("evaluate-runtime-close-1", sessionId, runId, "read", new JsonObject { ["path"] = Path.Combine(external, "probe.txt") }),
                default);
            Assert.Equal("ask", firstAsk.Decision!.Decision);
            var firstApprovalId = firstAsk.Decision.ApprovalId!;
            Assert.True(fixture.PendingV2.TryPeekV2(firstApprovalId, out _));

            var approval = await fixture.Runtime.RespondApprovalAsync(
                ApprovalRespond("approve-runtime-close", sessionId, runId, firstApprovalId, "allow_session_directory"),
                default);
            Assert.Equal("authorized", approval.Status);
            Assert.NotEmpty(fixture.Session.DirectoryGrants.GetGrantedDirectories(sessionId));

            // A second, unanswered approval proves the close also drops pending
            // v2 state instead of leaking it past the session lifetime.
            var secondAsk = await fixture.Runtime.EvaluateAsync(
                Evaluate("evaluate-runtime-close-2", sessionId, runId, "read", new JsonObject { ["path"] = Path.Combine(externalNext, "probe.txt") }),
                default);
            Assert.Equal("ask", secondAsk.Decision!.Decision);
            var secondApprovalId = secondAsk.Decision.ApprovalId!;
            Assert.True(fixture.PendingV2.TryPeekV2(secondApprovalId, out _));

            await fixture.Runtime.CloseSessionAsync(sessionId, default);

            Assert.Empty(fixture.Session.DirectoryGrants.GetGrantedDirectories(sessionId));
            Assert.False(fixture.PendingV2.TryPeekV2(secondApprovalId, out _));
        }
        finally
        {
            Directory.Delete(external, recursive: true);
            Directory.Delete(externalNext, recursive: true);
        }
    }

    [Fact]
    public async Task Run_lifecycle_open_rebind_close_matches_the_wire_contract()
    {
        using var fixture = new RuntimeFixture();
        const string sessionId = "session-runtime-run";
        const string runId = "run_runtime_lifecycle1111111111111";

        var opened = await fixture.Runtime.OpenRunAsync(
            RunOpen(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardEmpty(version: 1)),
            default);
        Assert.Null(opened.Error);
        Assert.Equal(runId, opened.RunId);
        Assert.Equal(1, opened.SnapshotVersion);
        Assert.Equal(1, opened.BindingVersion);

        var rebound = await fixture.Runtime.RebindRunPolicyAsync(
            RunOpen(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardEmpty(version: 2)),
            default);
        Assert.Null(rebound.Error);
        Assert.Equal(runId, rebound.RunId);
        Assert.Equal(2, rebound.SnapshotVersion);
        Assert.Equal(2, rebound.BindingVersion);

        var closed = await fixture.Runtime.CloseRunAsync(
            RunClose("close-runtime-run", sessionId, runId),
            default);
        Assert.True(closed.Closed);
        Assert.Equal(runId, closed.RunId);
        Assert.False(fixture.Session.TryGetRunLease(runId, out _));

        var afterClose = await fixture.Runtime.EvaluateAsync(
            Evaluate("evaluate-runtime-after-close", sessionId, runId, "read", new JsonObject { ["path"] = "probe.txt" }),
            default);
        Assert.NotNull(afterClose.Error);
        Assert.Equal("run_not_open", afterClose.Error!.ErrorCode);
    }

    [Fact]
    public async Task Session_open_validates_scopes_and_returns_the_grant()
    {
        using var fixture = new RuntimeFixture();

        var opened = await fixture.Runtime.OpenSessionAsync(
            "session-open-a",
            new HashSet<string>([ToolScope.WorkspaceWrite], StringComparer.Ordinal),
            default);
        Assert.True(opened.Opened);
        Assert.Equal(new[] { ToolScope.WorkspaceWrite }, opened.Scopes);

        // A second owner fails closed while the first session is open.
        var conflict = await fixture.Runtime.OpenSessionAsync(
            "session-open-b",
            new HashSet<string>([ToolScope.WorkspaceWrite], StringComparer.Ordinal),
            default);
        Assert.False(conflict.Opened);
        Assert.Equal("session_conflict", conflict.ErrorCode);

        await fixture.Runtime.CloseSessionAsync("session-open-a", default);

        var unavailable = await fixture.Runtime.OpenSessionAsync(
            "session-open-c",
            new HashSet<string>(["hyperdrive"], StringComparer.Ordinal),
            default);
        Assert.False(unavailable.Opened);
        Assert.Equal("scope_unavailable", unavailable.ErrorCode);
    }

    [Fact]
    public async Task Run_open_advertises_every_capability_and_rejects_a_tampered_snapshot()
    {
        using var fixture = new RuntimeFixture();
        const string sessionId = "session-runtime-capabilities";

        var opened = await fixture.Runtime.OpenRunAsync(
            RunOpen("run_caps1111111111111111111111111111", sessionId, fixture.Root, EnvelopeTestSnapshots.StandardEmpty()),
            default);
        Assert.Null(opened.Error);
        Assert.Equal(
            new[] { "bash", "edit", "glob", "grep", "help", "list", "read", "skill", "todo_write", "web_fetch", "web_search", "write" },
            opened.AdvertisedCapabilities!.OrderBy(capability => capability, StringComparer.Ordinal).ToArray());

        var tamperedPayload = EnvelopeTestSnapshots.RunOpenPayload(
            "run_tamper1111111111111111111111111",
            sessionId,
            fixture.Root,
            EnvelopeTestSnapshots.StandardEmpty());
        tamperedPayload["snapshot_hash"] = new string('e', 64);
        var tampered = await fixture.Runtime.OpenRunAsync(
            new AgentEnvelope(2, "open-tampered", AgentMessageTypes.RunOpen, sessionId, null, 10_000, tamperedPayload),
            default);
        Assert.NotNull(tampered.Error);
        Assert.Equal("snapshot_hash_mismatch", tampered.Error!.ErrorCode);
    }

    [Fact]
    public async Task Legacy_tool_request_reads_a_workspace_file()
    {
        using var fixture = new RuntimeFixture();
        File.WriteAllText(Path.Combine(fixture.Root, "probe.txt"), "workspace-probe");
        const string sessionId = "session-legacy-read";
        await OpenSessionAsync(fixture, sessionId, ToolScope.SystemRead, ToolScope.WorkspaceRead);

        var reply = await fixture.Runtime.ExecuteLegacyAsync(
            LegacyToolRequest("legacy-read-1", sessionId, "list", new JsonObject { ["path"] = "." }),
            default);

        Assert.NotNull(reply.Receipt);
        Assert.Equal(ToolResultStatuses.Succeeded, reply.Receipt!.Status);
        Assert.True(reply.Receipt.AdapterInvoked);
        Assert.NotNull(reply.Receipt.Output["entries"]);
    }

    [Fact]
    public async Task Legacy_terminal_request_requires_approval_then_executes_once()
    {
        using var fixture = new RuntimeFixture();
        const string sessionId = "session-legacy-terminal";
        await OpenSessionAsync(fixture, sessionId, ToolScope.TerminalExecute);
        var arguments = new JsonObject
        {
            ["mode"] = "run",
            ["executable"] = "where.exe",
            ["arguments"] = new JsonArray("dotnet"),
            ["working_directory"] = ".",
        };

        var pending = await fixture.Runtime.ExecuteLegacyAsync(
            LegacyToolRequest("legacy-terminal-1", sessionId, "bash", arguments),
            default);
        Assert.NotNull(pending.Receipt);
        Assert.Equal(ToolResultStatuses.ApprovalRequired, pending.Receipt!.Status);
        Assert.False(pending.Receipt.AdapterInvoked);
        var approvalId = pending.Receipt.Output["approval_id"]!.GetValue<string>();

        var approved = await fixture.Runtime.ExecuteLegacyAsync(
            LegacyApproval("legacy-terminal-approval-1", sessionId, approvalId),
            default);
        Assert.NotNull(approved.Receipt);
        Assert.Equal(ToolResultStatuses.Succeeded, approved.Receipt!.Status);
        Assert.True(approved.Receipt.AdapterInvoked);
        Assert.Contains(
            "dotnet",
            approved.Receipt.Output["stdout_head"]!.GetValue<string>(),
            StringComparison.OrdinalIgnoreCase);

        // The approval is single-use: a replay never reaches the adapter again.
        var replay = await fixture.Runtime.ExecuteLegacyAsync(
            LegacyApproval("legacy-terminal-approval-2", sessionId, approvalId),
            default);
        Assert.NotNull(replay.Receipt);
        Assert.Equal(ToolResultStatuses.Denied, replay.Receipt!.Status);
        Assert.Equal("approval_stale", replay.Receipt.ErrorCode);
        Assert.False(replay.Receipt.AdapterInvoked);
    }

    [Fact]
    public async Task Legacy_nonzero_terminal_exit_is_a_failed_receipt()
    {
        using var fixture = new RuntimeFixture();
        const string sessionId = "session-legacy-terminal-failed";
        await OpenSessionAsync(fixture, sessionId, ToolScope.TerminalExecute);

        var pending = await fixture.Runtime.ExecuteLegacyAsync(
            LegacyToolRequest("legacy-failed-1", sessionId, "bash", new JsonObject
            {
                ["mode"] = "run",
                ["executable"] = "where.exe",
                ["arguments"] = new JsonArray("definitely-not-a-real-tool-xyz"),
                ["working_directory"] = ".",
            }),
            default);
        Assert.Equal(ToolResultStatuses.ApprovalRequired, pending.Receipt!.Status);

        var result = await fixture.Runtime.ExecuteLegacyAsync(
            LegacyApproval(
                "legacy-failed-approval-1",
                sessionId,
                pending.Receipt.Output["approval_id"]!.GetValue<string>()),
            default);

        Assert.NotNull(result.Receipt);
        Assert.Equal(ToolResultStatuses.Failed, result.Receipt!.Status);
        Assert.Equal("process_exit_nonzero", result.Receipt.ErrorCode);
        Assert.True(result.Receipt.AdapterInvoked);
        Assert.False(result.Receipt.Output.AsObject().ContainsKey("status"));
    }

    [Fact]
    public async Task Evaluate_denies_unadvertised_tools_and_allows_standard_web()
    {
        using var fixture = new RuntimeFixture();
        const string sessionId = "session-runtime-unadvertised";
        const string runId = "run_unad1111111111111111111111111111";
        var opened = await fixture.Runtime.OpenRunAsync(
            RunOpen(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardEmpty()),
            default);
        Assert.Null(opened.Error);

        var unknown = await fixture.Runtime.EvaluateAsync(
            Evaluate("evaluate-unknown", sessionId, runId, "totally.unknown", new JsonObject { ["path"] = "." }),
            default);
        Assert.NotNull(unknown.Error);
        Assert.Equal("phase_not_available", unknown.Error!.ErrorCode);

        var web = await fixture.Runtime.EvaluateAsync(
            Evaluate("evaluate-web", sessionId, runId, "web_search", new JsonObject { ["query"] = "ru" }),
            default);
        Assert.Null(web.Error);
        Assert.Equal("allow", web.Decision!.Decision);
        Assert.Equal("standard_web_allow", web.Decision.ReasonCode);
    }

    [Fact]
    public async Task Full_access_external_read_and_terminal_execute_without_approval()
    {
        using var fixture = new RuntimeFixture();
        var external = Path.Combine(Path.GetTempPath(), "ru-runtime-full-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "probe.txt"), "external-file-content");
        try
        {
            const string sessionId = "session-runtime-full";
            const string runId = "run_full11111111111111111111111111111";
            var opened = await fixture.Runtime.OpenRunAsync(
                RunOpen(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.FullAccessEmpty()),
                default);
            Assert.Null(opened.Error);

            var readArguments = new JsonObject { ["path"] = Path.Combine(external, "probe.txt") };
            var readDecision = await fixture.Runtime.EvaluateAsync(
                Evaluate("evaluate-full-read", sessionId, runId, "read", readArguments),
                default);
            Assert.Equal("allow", readDecision.Decision!.Decision);
            Assert.Null(readDecision.Decision.ApprovalId);
            var read = await fixture.Runtime.ExecuteAuthorizedAsync(
                Execute("execute-full-read", sessionId, runId, "read", readDecision.Decision.AuthorizationToken!, readArguments),
                default);
            Assert.Equal(ToolResultStatuses.Succeeded, read.Receipt!.Status);
            Assert.Equal("external-file-content", read.Receipt.Output["content"]!.GetValue<string>());

            var terminalArguments = new JsonObject
            {
                ["mode"] = "run",
                ["executable"] = "dotnet",
                ["arguments"] = new JsonArray("--version"),
                ["working_directory"] = external,
                ["network_intent"] = true,
            };
            var terminalDecision = await fixture.Runtime.EvaluateAsync(
                Evaluate("evaluate-full-terminal", sessionId, runId, "bash", terminalArguments),
                default);
            Assert.Equal("allow", terminalDecision.Decision!.Decision);
            Assert.Null(terminalDecision.Decision.ApprovalId);
            var terminal = await fixture.Runtime.ExecuteAuthorizedAsync(
                Execute("execute-full-terminal", sessionId, runId, "bash", terminalDecision.Decision.AuthorizationToken!, terminalArguments),
                default);
            Assert.Equal(ToolResultStatuses.Succeeded, terminal.Receipt!.Status);
            Assert.Equal(0, terminal.Receipt.Output["exit_code"]!.GetValue<int>());
            Assert.Empty(fixture.Session.DirectoryGrants.GetGrantedDirectories(sessionId));
        }
        finally
        {
            Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public async Task Standard_workspace_shell_runs_without_approval_and_is_not_OS_confined()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new RuntimeFixture();
        var outside = CreateOutsideDirectory();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var acceptedTask = listener.AcceptTcpClientAsync(deadline.Token).AsTask();
        var tempLeaf = "ru-terminal-temp-" + Guid.NewGuid().ToString("N") + ".txt";
        try
        {
            const string sessionId = "session-standard-approved-shell";
            const string runId = "run_standard_approved_shell111111111111";
            var opened = await fixture.Runtime.OpenRunAsync(
                RunOpen(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardEmpty()),
                default);
            Assert.Null(opened.Error);

            var outsideFile = Path.Combine(outside, "escaped.txt");
            var tempFile = Path.Combine(Path.GetTempPath(), tempLeaf);
            var escapedOutside = outsideFile.Replace("'", "''");
            var script = $"Set-Content -LiteralPath '{escapedOutside}' -Value escaped -ErrorAction Stop; " +
                $"$tempFile = Join-Path $env:TEMP '{tempLeaf}'; " +
                "Set-Content -LiteralPath $tempFile -Value temp -ErrorAction Stop; " +
                "$client = New-Object Net.Sockets.TcpClient; " +
                $"$client.Connect('127.0.0.1', {port}); " +
                "$bytes = [Text.Encoding]::UTF8.GetBytes('loopback-ok' + [char]10); " +
                "$stream = $client.GetStream(); $stream.Write($bytes, 0, $bytes.Length); $client.Close(); 'PROBE_DONE'";
            var arguments = new JsonObject
            {
                ["mode"] = "shell",
                ["script"] = script,
                ["working_directory"] = ".",
                ["timeout_ms"] = 30_000,
            };

            var ask = await fixture.Runtime.EvaluateAsync(
                Evaluate("standard-shell-evaluate", sessionId, runId, "bash", arguments),
                default);
            Assert.Equal("allow", ask.Decision!.Decision);
            Assert.Null(ask.Decision.ApprovalId);

            var execution = await fixture.Runtime.ExecuteAuthorizedAsync(
                Execute("standard-shell-execute", sessionId, runId, "bash", ask.Decision.AuthorizationToken!, arguments),
                default);
            Assert.Equal(ToolResultStatuses.Succeeded, execution.Receipt!.Status);
            Assert.Contains("PROBE_DONE", execution.Receipt.Output["stdout_head"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.True(File.Exists(outsideFile), "an approved standard shell could not write outside the workspace");
            Assert.Equal("escaped", File.ReadAllText(outsideFile).Trim());
            Assert.True(File.Exists(tempFile), "an approved standard shell could not use the process temp directory");
            Assert.Equal("temp", File.ReadAllText(tempFile).Trim());

            using var accepted = await acceptedTask;
            using var reader = new StreamReader(accepted.GetStream());
            Assert.Equal("loopback-ok", await reader.ReadLineAsync(deadline.Token));
        }
        finally
        {
            listener.Stop();
            try { File.Delete(Path.Combine(Path.GetTempPath(), tempLeaf)); } catch (IOException) { }
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task Standard_managed_commit_runs_its_git_children()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new RuntimeFixture();
        var repo = Path.Combine(fixture.Root, "repo");
        Directory.CreateDirectory(repo);
        RunGit(repo, "init", "-q");
        RunGit(repo, "config", "user.email", "terminal@example.com");
        RunGit(repo, "config", "user.name", "Terminal Test");
        File.WriteAllText(Path.Combine(repo, "seed.txt"), "seed");
        RunGit(repo, "add", "seed.txt");
        RunGit(repo, "commit", "-q", "-m", "seed");
        File.WriteAllText(Path.Combine(repo, "next.txt"), "next");

        const string sessionId = "session-managed-commit";
        const string runId = "run_managed_commit111111111111111111111";
        var opened = await fixture.Runtime.OpenRunAsync(
            RunOpen(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardEmpty()),
            default);
        Assert.Null(opened.Error);

        var arguments = new JsonObject
        {
            ["mode"] = "run",
            ["executable"] = "git",
            ["arguments"] = new JsonArray("commit", "-m", "terminal commit"),
            ["working_directory"] = "repo",
        };
        var decision = await fixture.Runtime.EvaluateAsync(
            Evaluate("commit-evaluate", sessionId, runId, "bash", arguments),
            default);
        Assert.Equal("allow", decision.Decision!.Decision);

        var execution = await fixture.Runtime.ExecuteAuthorizedAsync(
            Execute("commit-execute", sessionId, runId, "bash", decision.Decision.AuthorizationToken!, arguments),
            default);
        Assert.Equal(ToolResultStatuses.Succeeded, execution.Receipt!.Status);
        Assert.Contains(
            "managed commit",
            execution.Receipt.Output["stdout_head"]!.GetValue<string>(),
            StringComparison.Ordinal);

        // The managed commit completed through its real git child processes.
        var head = RunGit(repo, "log", "--oneline", "-1");
        Assert.Contains("terminal commit", head, StringComparison.Ordinal);
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed: {stderr}{stdout}");
        }

        return stdout;
    }

    private static async Task<ToolExecutionReply> EvaluateApproveExecuteAsync(
        RuntimeFixture fixture,
        string sessionId,
        string runId,
        string prefix,
        JsonObject arguments)
    {
        var decision = await fixture.Runtime.EvaluateAsync(
            Evaluate(prefix + "-evaluate", sessionId, runId, "bash", arguments),
            default);
        Assert.Null(decision.Error);
        Assert.Equal("ask", decision.Decision!.Decision);
        Assert.NotNull(decision.Decision.ApprovalId);
        var approved = await fixture.Runtime.RespondApprovalAsync(
            ApprovalRespond(prefix + "-approve", sessionId, runId, decision.Decision.ApprovalId!, "allow_once"),
            default);
        Assert.Equal("authorized", approved.Status);
        return await fixture.Runtime.ExecuteAuthorizedAsync(
            Execute(prefix + "-execute", sessionId, runId, "bash", approved.AuthorizationToken!, arguments),
            default);
    }

    /// <summary>
    /// A PowerShell write probe through -EncodedCommand (no shell quoting
    /// surface): the write must be denied outside the granted paths and the
    /// marker proves the script ran. The encoded argv carries no external path,
    /// so the ask comes from the executable's risk class, not argv inspection.
    /// </summary>
    private static string CreateOutsideDirectory()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ru-terminal-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        return outside;
    }

    [Fact]
    public async Task Forged_execute_token_is_rejected_before_the_adapter_runs()
    {
        using var fixture = new RuntimeFixture();
        var probePath = Path.Combine(fixture.Root, "forged-probe.txt");
        File.WriteAllText(probePath, "must-not-change");
        const string sessionId = "session-forged-token";
        const string runId = "run_forged_token11111111111111111";
        var opened = await fixture.Runtime.OpenRunAsync(
            RunOpen(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.FullAccessEmpty()),
            default);
        Assert.Null(opened.Error);

        var execution = await fixture.Runtime.ExecuteAuthorizedAsync(
            Execute("execute-forged", sessionId, runId, "read", "forged-token", new JsonObject { ["path"] = "forged-probe.txt" }),
            default);

        Assert.Null(execution.Receipt);
        Assert.Equal("decision_stale", execution.ErrorCode);
        Assert.Equal("must-not-change", File.ReadAllText(probePath));
    }

    [Fact]
    public async Task Allow_always_authorizes_the_terminal_execution_and_creates_no_reusable_grant()
    {
        using var fixture = new RuntimeFixture();
        const string sessionId = "session-runtime-always";
        const string runId = "run_always111111111111111111111111111";
        var opened = await fixture.Runtime.OpenRunAsync(
            RunOpen(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardWithRules(PolicyEvalSnapshots.Rule("ask-terminal", "ask", PolicyEvalSnapshots.Matcher(toolId: "bash")))),
            default);
        Assert.Null(opened.Error);

        var arguments = new JsonObject
        {
            ["mode"] = "run",
            ["executable"] = "where.exe",
            ["arguments"] = new JsonArray("dotnet"),
            ["working_directory"] = ".",
        };
        var ask = await fixture.Runtime.EvaluateAsync(
            Evaluate("evaluate-always-1", sessionId, runId, "bash", arguments),
            default);
        Assert.Equal("ask", ask.Decision!.Decision);
        Assert.Equal("rule_asks", ask.Decision.ReasonCode);

        var approved = await fixture.Runtime.RespondApprovalAsync(
            ApprovalRespond("approve-always", sessionId, runId, ask.Decision.ApprovalId!, "allow_always"),
            default);
        Assert.Equal("authorized", approved.Status);

        var execution = await fixture.Runtime.ExecuteAuthorizedAsync(
            Execute("execute-always", sessionId, runId, "bash", approved.AuthorizationToken!, arguments),
            default);
        Assert.Equal(ToolResultStatuses.Succeeded, execution.Receipt!.Status);
        Assert.Equal(0, execution.Receipt.Output["exit_code"]!.GetValue<int>());

        // allow_always consumes the pending approval but adds no reusable grant:
        // the identical call asks again.
        var again = await fixture.Runtime.EvaluateAsync(
            Evaluate("evaluate-always-2", sessionId, runId, "bash", arguments),
            default);
        Assert.Equal("ask", again.Decision!.Decision);
        Assert.Equal("rule_asks", again.Decision.ReasonCode);
        Assert.Empty(fixture.Session.DirectoryGrants.GetGrantedDirectories(sessionId));
    }

    [Fact]
    public async Task Session_directory_grant_is_reused_by_another_tool_in_the_same_session()
    {
        using var fixture = new RuntimeFixture();
        var external = Path.Combine(Path.GetTempPath(), "ru-runtime-grant-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "probe.txt"), "external");
        try
        {
            const string sessionId = "session-runtime-grant";
            const string runId = "run_grant1111111111111111111111111111";
            var opened = await fixture.Runtime.OpenRunAsync(
                RunOpen(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardEmpty()),
                default);
            Assert.Null(opened.Error);

            var ask = await fixture.Runtime.EvaluateAsync(
                Evaluate("evaluate-grant-read", sessionId, runId, "read", new JsonObject { ["path"] = Path.Combine(external, "probe.txt") }),
                default);
            Assert.Equal("external_directory_required", ask.Decision!.ReasonCode);

            var approved = await fixture.Runtime.RespondApprovalAsync(
                ApprovalRespond("approve-grant", sessionId, runId, ask.Decision.ApprovalId!, "allow_session_directory"),
                default);
            Assert.Equal("authorized", approved.Status);

            // A different tool targeting the same granted directory now allows
            // without a second ask.
            var write = await fixture.Runtime.EvaluateAsync(
                Evaluate("evaluate-grant-write", sessionId, runId, "write", new JsonObject
                {
                    ["op"] = "write",
                    ["path"] = Path.Combine(external, "next.txt"),
                    ["content"] = "next",
                }),
                default);
            Assert.Null(write.Error);
            Assert.Equal("allow", write.Decision!.Decision);
        }
        finally
        {
            Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public async Task Policy_rebind_invalidates_a_decision_issued_under_the_old_binding()
    {
        using var fixture = new RuntimeFixture();
        File.WriteAllText(Path.Combine(fixture.Root, "probe.txt"), "rebind");
        const string sessionId = "session-runtime-rebind-token";
        const string runId = "run_rebind_token1111111111111111";

        var opened = await fixture.Runtime.OpenRunAsync(
            RunOpen(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardEmpty(version: 1)),
            default);
        Assert.Null(opened.Error);

        var arguments = new JsonObject { ["path"] = "probe.txt" };
        var decision = await fixture.Runtime.EvaluateAsync(
            Evaluate("evaluate-rebind", sessionId, runId, "read", arguments),
            default);
        var staleToken = decision.Decision!.AuthorizationToken!;

        var rebound = await fixture.Runtime.RebindRunPolicyAsync(
            RunOpen(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardEmpty(version: 2)),
            default);
        Assert.Null(rebound.Error);
        Assert.Equal(2, rebound.BindingVersion);

        var stale = await fixture.Runtime.ExecuteAuthorizedAsync(
            Execute("execute-rebind-stale", sessionId, runId, "read", staleToken, arguments),
            default);
        Assert.Null(stale.Receipt);
        Assert.Equal("decision_stale", stale.ErrorCode);
    }

    [Fact]
    public async Task Revoke_session_grants_removes_only_the_named_session()
    {
        using var fixture = new RuntimeFixture();
        var external = Path.Combine(Path.GetTempPath(), "ru-runtime-revoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "probe.txt"), "external");
        try
        {
            const string sessionId = "session-runtime-revoke";
            const string runId = "run_revoke111111111111111111111111111";
            var opened = await fixture.Runtime.OpenRunAsync(
                RunOpen(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardEmpty()),
                default);
            Assert.Null(opened.Error);

            var arguments = new JsonObject { ["path"] = Path.Combine(external, "probe.txt") };
            var ask = await fixture.Runtime.EvaluateAsync(
                Evaluate("evaluate-revoke-1", sessionId, runId, "read", arguments),
                default);
            var approved = await fixture.Runtime.RespondApprovalAsync(
                ApprovalRespond("approve-revoke", sessionId, runId, ask.Decision!.ApprovalId!, "allow_session_directory"),
                default);
            Assert.Equal("authorized", approved.Status);

            // Revoking a different session is a no-op for this session's grants.
            var other = await fixture.Runtime.RevokeSessionGrantsAsync(sessionId, "session-other", default);
            Assert.True(other.Revoked);
            var stillAllowed = await fixture.Runtime.EvaluateAsync(
                Evaluate("evaluate-revoke-2", sessionId, runId, "read", arguments),
                default);
            Assert.Equal("allow", stillAllowed.Decision!.Decision);

            var self = await fixture.Runtime.RevokeSessionGrantsAsync(sessionId, sessionId, default);
            Assert.True(self.Revoked);
            var asksAgain = await fixture.Runtime.EvaluateAsync(
                Evaluate("evaluate-revoke-3", sessionId, runId, "read", arguments),
                default);
            Assert.Equal("ask", asksAgain.Decision!.Decision);
            Assert.Equal("external_directory_required", asksAgain.Decision.ReasonCode);
        }
        finally
        {
            Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public async Task Stale_approval_cannot_add_a_session_grant()
    {
        using var fixture = new RuntimeFixture();
        var external = Path.Combine(Path.GetTempPath(), "ru-runtime-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "probe.txt"), "external");
        try
        {
            const string sessionId = "session-runtime-stale-grant";
            const string runId = "run_stale1111111111111111111111111111";
            var opened = await fixture.Runtime.OpenRunAsync(
                RunOpen(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardEmpty()),
                default);
            Assert.Null(opened.Error);

            var arguments = new JsonObject { ["path"] = Path.Combine(external, "probe.txt") };
            var ask = await fixture.Runtime.EvaluateAsync(
                Evaluate("evaluate-stale", sessionId, runId, "read", arguments),
                default);
            var approvalId = ask.Decision!.ApprovalId!;

            // Change the real binding rather than relying on a debug-only wire
            // override. A stale directory approval must not add a grant in either
            // Debug or the shipped Release composition.
            var rebound = await fixture.Runtime.RebindRunPolicyAsync(
                RunOpen(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardEmpty(version: 2)),
                default);
            Assert.Null(rebound.Error);
            var stale = await fixture.Runtime.RespondApprovalAsync(
                ApprovalRespond(
                    "approve-stale",
                    sessionId,
                    runId,
                    approvalId,
                    "allow_session_directory"),
                default);
            Assert.Equal(ToolResultStatuses.Denied, stale.Status);
            Assert.Equal("approval_stale", stale.ErrorCode);
            Assert.Empty(fixture.Session.DirectoryGrants.GetGrantedDirectories(sessionId));

            var again = await fixture.Runtime.EvaluateAsync(
                Evaluate("evaluate-stale-again", sessionId, runId, "read", arguments),
                default);
            Assert.Equal("ask", again.Decision!.Decision);
            Assert.Equal("external_directory_required", again.Decision.ReasonCode);
        }
        finally
        {
            Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public async Task Terminal_shell_ask_allow_once_then_execute()
    {
        using var fixture = new RuntimeFixture();
        const string sessionId = "session-runtime-shell";
        const string runId = "run_shell1111111111111111111111111111";
        var opened = await fixture.Runtime.OpenRunAsync(
            RunOpen(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardEmpty()),
            default);
        Assert.Null(opened.Error);

        var arguments = new JsonObject
        {
            ["mode"] = "shell",
            ["script"] = "Get-Date",
            ["working_directory"] = Path.GetTempPath(),
        };
        var ask = await fixture.Runtime.EvaluateAsync(
            Evaluate("evaluate-shell", sessionId, runId, "bash", arguments),
            default);
        Assert.Equal("ask", ask.Decision!.Decision);
        Assert.Equal("terminal_external_cwd_requires_approval", ask.Decision.ReasonCode);

        var approved = await fixture.Runtime.RespondApprovalAsync(
            ApprovalRespond("approve-shell", sessionId, runId, ask.Decision.ApprovalId!, "allow_once"),
            default);
        Assert.Equal("authorized", approved.Status);

        var execution = await fixture.Runtime.ExecuteAuthorizedAsync(
            Execute("execute-shell", sessionId, runId, "bash", approved.AuthorizationToken!, arguments),
            default);
        Assert.Equal(ToolResultStatuses.Succeeded, execution.Receipt!.Status);
        Assert.True(execution.Receipt.AdapterInvoked);
        Assert.Equal(0, execution.Receipt.Output["exit_code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Terminal_approval_creates_no_reusable_grant()
    {
        using var fixture = new RuntimeFixture();
        const string sessionId = "session-runtime-terminal-grant";
        const string runId = "run_terminal11111111111111111111111111";
        var opened = await fixture.Runtime.OpenRunAsync(
            RunOpen(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardWithRules(PolicyEvalSnapshots.Rule("ask-terminal", "ask", PolicyEvalSnapshots.Matcher(toolId: "bash")))),
            default);
        Assert.Null(opened.Error);

        JsonObject DeleteArguments() => new()
        {
            ["mode"] = "run",
            ["executable"] = "rm.exe",
            ["arguments"] = new JsonArray("target.txt"),
            ["working_directory"] = ".",
        };

        var ask = await fixture.Runtime.EvaluateAsync(
            Evaluate("evaluate-terminal-delete", sessionId, runId, "bash", DeleteArguments()),
            default);
        Assert.Equal("ask", ask.Decision!.Decision);
        Assert.Equal("rule_asks", ask.Decision.ReasonCode);

        var approved = await fixture.Runtime.RespondApprovalAsync(
            ApprovalRespond("approve-terminal-once", sessionId, runId, ask.Decision.ApprovalId!, "allow_once"),
            default);
        Assert.Equal("authorized", approved.Status);

        var again = await fixture.Runtime.EvaluateAsync(
            Evaluate("evaluate-terminal-again", sessionId, runId, "bash", DeleteArguments()),
            default);
        Assert.Equal("ask", again.Decision!.Decision);
        Assert.Equal("rule_asks", again.Decision.ReasonCode);
    }

    [Fact]
    public async Task Approval_respond_without_a_run_id_is_reserved()
    {
        using var fixture = new RuntimeFixture();
        const string sessionId = "session-runtime-approval-reserved";
        Assert.True(fixture.Session.Open(sessionId, new HashSet<string>(StringComparer.Ordinal)));

        var result = await fixture.Runtime.RespondApprovalAsync(
            new AgentEnvelope(
                2,
                "approval-reserved",
                AgentMessageTypes.ApprovalRespond,
                sessionId,
                null,
                10_000,
                new JsonObject { ["approval_id"] = "approval-1", ["approved"] = true }),
            default);

        Assert.Equal(ToolResultStatuses.Denied, result.Status);
        Assert.Equal("phase_not_available", result.ErrorCode);
    }

    [Fact]
    public async Task Session_tasks_list_and_kill_a_background_terminal_task()
    {
        using var fixture = new RuntimeFixture();
        const string sessionId = "session-runtime-tasks";
        const string runId = "run_tasks11111111111111111111111111111";
        var opened = await fixture.Runtime.OpenRunAsync(
            RunOpen(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.FullAccessEmpty()),
            default);
        Assert.Null(opened.Error);

        var arguments = new JsonObject
        {
            ["mode"] = "run",
            ["executable"] = "cmd.exe",
            ["arguments"] = new JsonArray("/c", "ping -n 30 127.0.0.1 > nul"),
            ["working_directory"] = fixture.Root,
            ["background"] = true,
        };
        var decision = await fixture.Runtime.EvaluateAsync(
            Evaluate("evaluate-tasks", sessionId, runId, "bash", arguments),
            default);
        Assert.Equal("allow", decision.Decision!.Decision);

        var execution = await fixture.Runtime.ExecuteAuthorizedAsync(
            Execute("execute-tasks", sessionId, runId, "bash", decision.Decision.AuthorizationToken!, arguments),
            default);
        Assert.Equal(ToolResultStatuses.Succeeded, execution.Receipt!.Status);
        var executionId = execution.Receipt.Output["execution_id"]!.GetValue<string>();
        Assert.Equal("running", execution.Receipt.Output["state"]!.GetValue<string>());

        var listed = await fixture.Runtime.SessionTasksAsync(sessionId, killExecutionId: null, default);
        Assert.Null(listed.Refusal);
        Assert.Equal(ToolResultStatuses.Succeeded, listed.Status);
        Assert.Contains(
            listed.Payload!["tasks"]!.AsArray(),
            task => string.Equals(task!["execution_id"]!.GetValue<string>(), executionId, StringComparison.Ordinal));

        var unknown = await fixture.Runtime.SessionTasksAsync(sessionId, "req-no-such-task", default);
        Assert.Equal(ToolResultStatuses.Failed, unknown.Status);
        Assert.Equal("unknown_background_task", unknown.ErrorCode);

        var killed = await fixture.Runtime.SessionTasksAsync(sessionId, executionId, default);
        Assert.Equal(ToolResultStatuses.Succeeded, killed.Status);
        Assert.True(killed.Payload!["killed"]!.GetValue<bool>());

        for (var attempt = 0; attempt < 100; attempt++)
        {
            var polled = await fixture.Runtime.SessionTasksAsync(sessionId, killExecutionId: null, default);
            var entry = polled.Payload!["tasks"]!.AsArray()
                .FirstOrDefault(task => string.Equals(task!["execution_id"]!.GetValue<string>(), executionId, StringComparison.Ordinal));
            if (entry is not null && string.Equals(entry["state"]!.GetValue<string>(), "killed", StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail("background task never reached the killed state");
    }

    [Fact]
    public async Task Session_tasks_rejects_an_over_long_kill_execution_id()
    {
        using var fixture = new RuntimeFixture();
        const string sessionId = "session-runtime-tasks-invalid";
        const string runId = "run_tasks_invalid111111111111111111";
        var opened = await fixture.Runtime.OpenRunAsync(
            RunOpen(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardEmpty()),
            default);
        Assert.Null(opened.Error);

        var rejected = await fixture.Runtime.SessionTasksAsync(
            sessionId,
            new string('x', TerminalLimits.MaxExecutionIdChars + 1),
            default);

        Assert.Equal(ToolResultStatuses.Failed, rejected.Status);
        Assert.Equal("invalid_arguments", rejected.ErrorCode);
        Assert.Equal("kill_execution_id is invalid.", rejected.SafeSummary);
    }

    [Fact]
    public async Task Cancel_run_terminates_an_in_flight_terminal_execution()
    {
        using var fixture = new RuntimeFixture();
        const string sessionId = "session-runtime-cancel";
        const string runId = "run_cancel1111111111111111111111111111";
        var opened = await fixture.Runtime.OpenRunAsync(
            RunOpen(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.FullAccessEmpty()),
            default);
        Assert.Null(opened.Error);

        var arguments = new JsonObject
        {
            ["mode"] = "run",
            ["executable"] = "ping",
            ["arguments"] = new JsonArray("-n", "61", "127.0.0.1"),
            ["working_directory"] = fixture.Root,
            ["timeout_ms"] = 600_000,
        };
        var decision = await fixture.Runtime.EvaluateAsync(
            Evaluate("evaluate-cancel", sessionId, runId, "bash", arguments),
            default);
        Assert.Equal("allow", decision.Decision!.Decision);

        var execution = fixture.Runtime.ExecuteAuthorizedAsync(
            Execute("execute-cancel", sessionId, runId, "bash", decision.Decision.AuthorizationToken!, arguments),
            default);
        await Task.Delay(2_000);

        var stopwatch = Stopwatch.StartNew();
        Assert.Equal(1, fixture.Runtime.CancelRun(runId));
        var receipt = (await execution.WaitAsync(TimeSpan.FromSeconds(20))).Receipt;
        stopwatch.Stop();

        Assert.NotNull(receipt);
        Assert.Equal(ToolResultStatuses.Cancelled, receipt!.Status);
        Assert.Equal("process_cancelled", receipt.ErrorCode);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(20),
            $"cancel took {stopwatch.Elapsed}; the process was not stopped by the cancel");
    }

    [Fact]
    public async Task Legacy_tool_requests_are_serialized_by_the_lifecycle_gate()
    {
        using var fixture = new RuntimeFixture();
        const string sessionId = "session-serialized-legacy";
        Assert.True(fixture.Session.Open(sessionId, new HashSet<string>([ToolScope.SystemRead], StringComparer.Ordinal)));
        var firstStarted = NewSignal();
        var secondStarted = NewSignal();
        var release = NewSignal();
        var active = 0;
        var maximumActive = 0;
        using var runtime = new BrokerExecutionRuntime(
            fixture.Session,
            new HashSet<string>(StringComparer.Ordinal),
            legacyToolHandler: async (request, cancellationToken) =>
            {
                var current = Interlocked.Increment(ref active);
                maximumActive = Math.Max(maximumActive, current);
                (request.RequestId == "legacy-serial-1" ? firstStarted : secondStarted).TrySetResult(true);
                try
                {
                    await release.Task.WaitAsync(cancellationToken);
                    return new ToolResult(
                        request.RequestId,
                        request.SessionId,
                        ToolResultStatuses.Succeeded,
                        new JsonObject(),
                        null,
                        "ok",
                        0);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });

        var first = runtime.ExecuteLegacyAsync(LegacyToolRequest("legacy-serial-1", sessionId, "read", new JsonObject()), default);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = runtime.ExecuteLegacyAsync(LegacyToolRequest("legacy-serial-2", sessionId, "read", new JsonObject()), default);
        await Task.Delay(50);
        Assert.False(secondStarted.Task.IsCompleted);
        Assert.Equal(1, maximumActive);

        release.TrySetResult(true);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, maximumActive);
    }

    [Fact]
    public async Task Approval_response_is_serialized_with_a_legacy_tool_request()
    {
        using var fixture = new RuntimeFixture();
        const string sessionId = "session-serialized-approval";
        Assert.True(fixture.Session.Open(sessionId, new HashSet<string>(StringComparer.Ordinal)));
        var firstStarted = NewSignal();
        var secondStarted = NewSignal();
        var release = NewSignal();
        var active = 0;
        var maximumActive = 0;
        using var runtime = new BrokerExecutionRuntime(
            fixture.Session,
            new HashSet<string>(StringComparer.Ordinal),
            legacyToolHandler: async (request, cancellationToken) =>
            {
                var current = Interlocked.Increment(ref active);
                maximumActive = Math.Max(maximumActive, current);
                (request.RequestId == "approval-serial-1" ? firstStarted : secondStarted).TrySetResult(true);
                try
                {
                    await release.Task.WaitAsync(cancellationToken);
                    return new ToolResult(
                        request.RequestId,
                        request.SessionId,
                        ToolResultStatuses.Succeeded,
                        new JsonObject(),
                        null,
                        "ok",
                        0);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });

        var first = runtime.ExecuteLegacyAsync(LegacyToolRequest("approval-serial-1", sessionId, "read", new JsonObject()), default);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = runtime.ExecuteLegacyAsync(LegacyApproval("approval-serial-2", sessionId, "approval-1"), default);
        await Task.Delay(50);
        Assert.False(secondStarted.Task.IsCompleted);
        Assert.Equal(1, maximumActive);

        release.TrySetResult(true);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, maximumActive);
    }

    [Fact]
    public async Task Tool_handler_exception_is_an_isolated_request_failure()
    {
        using var fixture = new RuntimeFixture();
        const string sessionId = "session-handler-failure";
        Assert.True(fixture.Session.Open(sessionId, new HashSet<string>(StringComparer.Ordinal)));
        using var runtime = new BrokerExecutionRuntime(
            fixture.Session,
            new HashSet<string>(StringComparer.Ordinal),
            legacyToolHandler: (request, _) =>
            {
                if (request.RequestId == "handler-failure")
                {
                    throw new InvalidOperationException("handler_failure");
                }

                return Task.FromResult(new ToolResult(
                    request.RequestId,
                    request.SessionId,
                    ToolResultStatuses.Succeeded,
                    new JsonObject(),
                    null,
                    "ok",
                    0));
            });

        var failure = await runtime.ExecuteLegacyAsync(
            LegacyToolRequest("handler-failure", sessionId, "read", new JsonObject()),
            default);
        Assert.Null(failure.Receipt);
        Assert.Equal("tool_handler_failed", failure.ErrorCode);

        var good = await runtime.ExecuteLegacyAsync(
            LegacyToolRequest("handler-good", sessionId, "read", new JsonObject()),
            default);
        Assert.NotNull(good.Receipt);
        Assert.Equal(ToolResultStatuses.Succeeded, good.Receipt!.Status);
    }

    [Fact]
    public async Task V2_execute_handlers_overlap_after_serialized_preflight()
    {
        using var fixture = new RuntimeFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "src"));
        File.WriteAllText(Path.Combine(fixture.Root, "src", "overlap.txt"), "overlap");
        var firstStarted = NewSignal();
        var secondStarted = NewSignal();
        var release = NewSignal();
        using var runtime = new BrokerExecutionRuntime(
            fixture.Session,
            new HashSet<string>(StringComparer.Ordinal),
            legacyToolHandler: async (request, cancellationToken) =>
            {
                (request.RequestId == "execute-overlap-1" ? firstStarted : secondStarted).TrySetResult(true);
                await release.Task.WaitAsync(cancellationToken);
                return new ToolResult(
                    request.RequestId,
                    request.SessionId,
                    ToolResultStatuses.Succeeded,
                    new JsonObject(),
                    null,
                    "ok",
                    0);
            });
        const string sessionId = "session-v2-overlap";
        const string runId = "run_overlap111111111111111111111111111";
        var opened = await runtime.OpenRunAsync(
            RunOpen(runId, sessionId, fixture.Root, EnvelopeTestSnapshots.StandardEmpty()),
            default);
        Assert.Null(opened.Error);

        var arguments = new JsonObject { ["path"] = "src/overlap.txt" };
        var firstDecision = await runtime.EvaluateAsync(
            Evaluate("evaluate-overlap-1", sessionId, runId, "read", arguments),
            default);
        var secondDecision = await runtime.EvaluateAsync(
            Evaluate("evaluate-overlap-2", sessionId, runId, "read", arguments),
            default);
        Assert.Equal("allow", firstDecision.Decision!.Decision);
        Assert.Equal("allow", secondDecision.Decision!.Decision);

        var first = runtime.ExecuteAuthorizedAsync(
            Execute("execute-overlap-1", sessionId, runId, "read", firstDecision.Decision.AuthorizationToken!, arguments),
            default);
        var second = runtime.ExecuteAuthorizedAsync(
            Execute("execute-overlap-2", sessionId, runId, "read", secondDecision.Decision.AuthorizationToken!, arguments),
            default);

        await Task.WhenAll(firstStarted.Task, secondStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult(true);
        var replies = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(replies, reply => Assert.Equal(ToolResultStatuses.Succeeded, reply.Receipt!.Status));
    }

    private static Task OpenSessionAsync(RuntimeFixture fixture, string sessionId, params string[] scopes)
    {
        Assert.True(fixture.Session.Open(sessionId, new HashSet<string>(scopes, StringComparer.Ordinal)));
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ClosingSessionRetriesCleanupAfterAuthorityWasAlreadyRemoved()
    {
        using var fixture = new RuntimeFixture();
        var attempts = 0;
        using var runtime = new BrokerExecutionRuntime(fixture.Session, new HashSet<string>(),
            sessionClosedHandler: _ => ++attempts == 1
                ? Task.FromException(new IOException("synthetic cleanup failure"))
                : Task.CompletedTask);
        await runtime.OpenSessionAsync("session-cleanup-retry", new HashSet<string>(), default);

        await Assert.ThrowsAsync<IOException>(() => runtime.CloseSessionAsync("session-cleanup-retry", default));
        Assert.False(fixture.Session.Owns("session-cleanup-retry"));
        await runtime.CloseSessionAsync("session-cleanup-retry", default);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task DisposingRuntimeCancelsExecutionsAndRejectsNewSessions()
    {
        using var fixture = new RuntimeFixture();
        var cancellations = new RunToolCancellationRegistry();
        using var runtime = new BrokerExecutionRuntime(fixture.Session, fixture.Tools, runCancellations: cancellations);
        using var registration = cancellations.Register("run_disposal", CancellationToken.None);
        runtime.Dispose();

        Assert.True(registration.Token.IsCancellationRequested);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => runtime.OpenSessionAsync(
            "session-after-disposal", new HashSet<string>(), CancellationToken.None));
    }

    [Fact]
    public async Task Closing_a_session_cancels_its_registered_executions_only()
    {
        using var fixture = new RuntimeFixture();
        var cancellations = new RunToolCancellationRegistry();
        using var runtime = new BrokerExecutionRuntime(fixture.Session, fixture.Tools, runCancellations: cancellations);
        const string runA = "run_close_session_a111111111111111";
        const string runB = "run_close_session_b111111111111111";
        Assert.Null((await runtime.OpenRunAsync(RunOpen(runA, "session-a", fixture.Root, EnvelopeTestSnapshots.StandardEmpty()), default)).Error);
        Assert.Null((await runtime.OpenRunAsync(RunOpen(runB, "session-b", fixture.Root, EnvelopeTestSnapshots.StandardEmpty()), default)).Error);
        using var first = cancellations.Register(runA, default);
        using var other = cancellations.Register(runB, default);

        await runtime.CloseSessionAsync("session-a", default);

        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(other.Token.IsCancellationRequested);
        Assert.True(fixture.Session.TryGetRunLease(runB, out _));
    }

    [Fact]
    public async Task Closing_a_foreign_run_does_not_cancel_or_remove_its_lease()
    {
        using var fixture = new RuntimeFixture();
        var cancellations = new RunToolCancellationRegistry();
        using var runtime = new BrokerExecutionRuntime(fixture.Session, fixture.Tools, runCancellations: cancellations);
        const string runA = "run_foreign_close_a111111111111111";
        const string runB = "run_foreign_close_b111111111111111";
        await runtime.OpenRunAsync(RunOpen(runA, "session-a", fixture.Root, EnvelopeTestSnapshots.StandardEmpty()), default);
        await runtime.OpenRunAsync(RunOpen(runB, "session-b", fixture.Root, EnvelopeTestSnapshots.StandardEmpty()), default);
        using var other = cancellations.Register(runB, default);

        var closed = await runtime.CloseRunAsync(RunClose("foreign-close", "session-a", runB), default);

        Assert.False(closed.Closed);
        Assert.False(other.Token.IsCancellationRequested);
        Assert.True(fixture.Session.TryGetRunLease(runB, out _));
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static AgentEnvelope LegacyToolRequest(
        string requestId,
        string sessionId,
        string toolId,
        JsonObject arguments) =>
        new(
            AgentEnvelopeCodec.ProtocolVersion,
            requestId,
            AgentMessageTypes.ToolRequest,
            sessionId,
            null,
            10_000,
            new JsonObject
            {
                ["tool_id"] = toolId,
                ["tool_version"] = "1.0.0",
                ["capability_class"] = "internal",
                ["arguments"] = arguments.DeepClone(),
            });

    private static AgentEnvelope LegacyApproval(
        string requestId,
        string sessionId,
        string approvalId) =>
        new(
            AgentEnvelopeCodec.ProtocolVersion,
            requestId,
            AgentMessageTypes.ApprovalResponse,
            sessionId,
            null,
            10_000,
            new JsonObject
            {
                ["approval_id"] = approvalId,
                ["approved"] = true,
            });

    private static AgentEnvelope RunOpen(
        string runId,
        string sessionId,
        string canonicalRoot,
        BrokerPolicySnapshotWire snapshot) =>
        new(
            2,
            "open-" + runId,
            AgentMessageTypes.RunOpen,
            sessionId,
            null,
            10_000,
            EnvelopeTestSnapshots.RunOpenPayload(runId, sessionId, canonicalRoot, snapshot));

    private static AgentEnvelope RunClose(string requestId, string sessionId, string runId) =>
        new(
            2,
            requestId,
            AgentMessageTypes.RunClose,
            sessionId,
            null,
            10_000,
            new JsonObject { ["run_id"] = runId });

    private static AgentEnvelope Evaluate(
        string requestId,
        string sessionId,
        string runId,
        string toolId,
        JsonObject arguments) =>
        new(
            2,
            requestId,
            AgentMessageTypes.ToolEvaluate,
            sessionId,
            null,
            10_000,
            new JsonObject
            {
                ["run_id"] = runId,
                ["tool_id"] = toolId,
                ["tool_version"] = "1.0.0",
                ["arguments"] = arguments.DeepClone(),
            });

    private static AgentEnvelope Execute(
        string requestId,
        string sessionId,
        string runId,
        string toolId,
        string authorizationToken,
        JsonObject arguments) =>
        new(
            2,
            requestId,
            AgentMessageTypes.ToolExecute,
            sessionId,
            null,
            10_000,
            new JsonObject
            {
                ["run_id"] = runId,
                ["tool_id"] = toolId,
                ["tool_version"] = "1.0.0",
                ["authorization_token"] = authorizationToken,
                ["arguments"] = arguments.DeepClone(),
            });

    private static AgentEnvelope ApprovalRespond(
        string requestId,
        string sessionId,
        string runId,
        string approvalId,
        string command,
        string? expectedIntentHash = null)
    {
        var payload = new JsonObject
        {
            ["run_id"] = runId,
            ["approval_id"] = approvalId,
            ["command"] = command,
        };
        if (expectedIntentHash is not null)
        {
            payload["expected_intent_hash"] = expectedIntentHash;
        }

        return new AgentEnvelope(
            2,
            requestId,
            AgentMessageTypes.ApprovalRespond,
            sessionId,
            null,
            10_000,
            payload);
    }

    /// <summary>
    /// A descriptor-free session, a real execution service and the runtime over
    /// a throwaway workspace. No process-global environment mutation.
    /// </summary>
    private sealed class RuntimeFixture : IDisposable
    {
        public RuntimeFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "ru-execution-runtime-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Session = BrokerSession.CreateForHost(Root, DateTimeOffset.UtcNow.AddMinutes(30));
            Tools = new ToolExecutionService(
                Session,
                Root);
            Runtime = new BrokerExecutionRuntime(Session, Tools, pendingV2Store: PendingV2);
        }

        public string Root { get; }
        public BrokerSession Session { get; }
        public ToolExecutionService Tools { get; }
        public BrokerExecutionRuntime Runtime { get; }
        public PendingApprovalStore PendingV2 { get; } = new();

        public void Dispose()
        {
            Runtime.Dispose();
            Tools.Dispose();
            DeleteFixtureRoot(Root);
        }

        /// <summary>
        /// Best-effort recursive delete: git writes loose objects read-only,
        /// which can make <see cref="Directory.Delete(string, bool)"/> fail.
        /// </summary>
        private static void DeleteFixtureRoot(string root)
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                    }
                }

                Directory.Delete(root, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }


    }
}
