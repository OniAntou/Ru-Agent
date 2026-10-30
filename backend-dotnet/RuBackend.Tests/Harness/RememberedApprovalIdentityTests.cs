extern alias Broker;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Approval;
using RuAgent.Harness.Core.Policy;
using RuBackend.Harness.Policy;
using Xunit;
using BrokerContextFactory = Broker::RuAgent.Broker.Policy.PolicyContextFactory;
using BrokerEvaluator = Broker::RuAgent.Broker.Policy.PolicySnapshotEvaluator;
using BrokerInvariants = Broker::RuAgent.Broker.Policy.PolicyRequestInvariants;
using BrokerRememberPolicy = Broker::RuAgent.Broker.Policy.ApprovalRememberPolicy;
using BrokerRunLease = Broker::RuAgent.Broker.Runtime.BrokerRunLease;
using BrokerPolicyContracts = RuAgent.Protocol.Harness.BrokerPolicyContracts;
using BrokerRuleWire = RuAgent.Protocol.Harness.BrokerPolicyRuleWire;
using BrokerSnapshotWire = RuAgent.Protocol.Harness.BrokerPolicySnapshotWire;
using BrokerWorkspaceWire = RuAgent.Protocol.Harness.BrokerPolicyWorkspaceIdentityWire;

namespace RuBackend.Tests.Harness;

/// <summary>
/// Remembered-approval identity over the real Host -> Broker approval path.
///
/// Each "remembered" case first asks through a real run, approves it through the
/// approval endpoint, then checks a later call in the same session (or a new one,
/// for project-scope rules). The asserted contract: a remembered approval covers
/// only the identity that was approved (server, tool, executable, argv, cwd,
/// computer apps, canonical file path). The Broker builds that identity from the
/// call's PolicyContext and returns it with the grant; the Host only stores it.
/// Controls and documented current design are marked as such.
///
/// The Host and Broker run in process, so this suite shares the process-heavy
/// collection with the other approval suites.
/// </summary>
[Collection(nameof(TerminalProcessHeavyCollection))]
public sealed partial class RememberedApprovalIdentityTests
{
    // ---- Case 1: MCP allow_once is session memory --------------------------

    [Fact]
    public async Task Case1_Control_identical_mcp_call_after_allow_once_is_not_asked_again()
    {
        await using var env = await Scenario.StartAsync(mcp: true);
        var session = await env.NewSessionAsync();
        await AllowAsync(env, session, "alpha read first", ToolCall("mcp", McpCall("alpha", "read_note")), "allow_once");

        var again = await RunUntilAskOrEndAsync(env, session, "alpha read again", ToolCall("mcp", McpCall("alpha", "read_note")));

        Assert.True(again.ApprovalId is null,
            $"identical alpha/read_note asked again in the same session (status {again.Status}); session memory should allow it.");
        Assert.True(again.EventCount("tool.execution_completed") == 1,
            "identical alpha/read_note did not execute after its session allow.");
    }

    [Fact]
    public async Task Case1_Mcp_allow_once_on_alpha_read_does_not_cover_beta_write_in_the_same_session()
    {
        await using var env = await Scenario.StartAsync(mcp: true);
        var session = await env.NewSessionAsync();
        await AllowAsync(env, session, "alpha read first", ToolCall("mcp", McpCall("alpha", "read_note")), "allow_once");

        var beta = await RunUntilAskOrEndAsync(env, session, "beta write", ToolCall("mcp", McpCall("beta", "write_note")));

        Assert.True(beta.ApprovalId is not null,
            $"beta/write_note ran without asking after alpha/read_note was allowed once in this session (status {beta.Status}): the remembered MCP matcher carries no server or tool.");
    }

    [Fact]
    public async Task Case1_Mcp_allow_once_on_alpha_read_does_not_cover_alpha_write_in_the_same_session()
    {
        await using var env = await Scenario.StartAsync(mcp: true);
        var session = await env.NewSessionAsync();
        await AllowAsync(env, session, "alpha read first", ToolCall("mcp", McpCall("alpha", "read_note")), "allow_once");

        var sameServer = await RunUntilAskOrEndAsync(env, session, "alpha write", ToolCall("mcp", McpCall("alpha", "write_note")));

        Assert.True(sameServer.ApprovalId is not null,
            $"alpha/write_note ran without asking after alpha/read_note was allowed once (status {sameServer.Status}): the remembered MCP matcher carries no tool.");
    }

    // ---- Case 2: MCP allow_always is a project rule ------------------------

    [Fact]
    public async Task Case2_Mcp_allow_always_on_alpha_read_covers_that_call_in_a_new_session()
    {
        await using var env = await Scenario.StartAsync(mcp: true);
        var first = await env.NewSessionAsync();
        var saved = await AllowAsync(env, first, "alpha read always", ToolCall("mcp", McpCall("alpha", "read_note")), "allow_always");
        Assert.True(saved.EventCount("tool.execution_completed") == 1, "allow_always alpha/read_note did not execute.");

        var second = await env.NewSessionAsync();
        var later = await RunUntilAskOrEndAsync(env, second, "alpha read in a new session", ToolCall("mcp", McpCall("alpha", "read_note")));

        Assert.True(later.ApprovalId is null,
            $"alpha/read_note asked again in a new session after allow_always (status {later.Status}); the project rule should cover it.");
    }

    [Fact]
    public async Task Case2_Mcp_allow_always_on_alpha_read_does_not_cover_beta_write_in_a_new_session()
    {
        await using var env = await Scenario.StartAsync(mcp: true);
        var first = await env.NewSessionAsync();
        await AllowAsync(env, first, "alpha read always", ToolCall("mcp", McpCall("alpha", "read_note")), "allow_always");

        var second = await env.NewSessionAsync();
        var beta = await RunUntilAskOrEndAsync(env, second, "beta write in a new session", ToolCall("mcp", McpCall("beta", "write_note")));

        Assert.True(beta.ApprovalId is not null,
            $"beta/write_note ran without asking in a new session after allow_always on alpha/read_note (status {beta.Status}): the durable MCP rule carries no server or tool.");
    }

    // ---- Case 3: list_tools approval must not authorize calls --------------

    [Fact]
    public async Task Case3_Mcp_allow_always_on_list_tools_does_not_authorize_a_call_on_that_server()
    {
        await using var env = await Scenario.StartAsync(mcp: true);
        var session = await env.NewSessionAsync();
        await AllowAsync(env, session, "alpha list tools always", ToolCall("mcp", McpListTools("alpha")), "allow_always");

        var call = await RunUntilAskOrEndAsync(env, session, "alpha read after list", ToolCall("mcp", McpCall("alpha", "read_note")));

        Assert.True(call.ApprovalId is not null,
            $"alpha/read_note ran without asking after allow_always on alpha list_tools (status {call.Status}): the remembered rule has an empty argv and matches every MCP call.");
    }

    // ---- Case 4: computer request_access grants are per app list -----------

    [Fact]
    public async Task Case4_Control_computer_request_access_is_not_asked_again_for_the_same_app()
    {
        await using var env = await Scenario.StartAsync(computer: true);
        var session = await env.NewSessionAsync();
        await AllowAsync(env, session, "notepad access", ToolCall("computer", RequestAccess("notepad.exe")), "allow_once");

        var again = await RunUntilAskOrEndAsync(env, session, "notepad access again", ToolCall("computer", RequestAccess("notepad.exe")));

        Assert.True(again.ApprovalId is null,
            $"request_access notepad.exe asked again in the same session (status {again.Status}); session memory should allow it.");
    }

    [Fact]
    public async Task Case4_Computer_request_access_allow_once_for_notepad_does_not_grant_calc()
    {
        await using var env = await Scenario.StartAsync(computer: true);
        var session = await env.NewSessionAsync();
        await AllowAsync(env, session, "notepad access", ToolCall("computer", RequestAccess("notepad.exe")), "allow_once");

        var calc = await RunUntilAskOrEndAsync(env, session, "calc access", ToolCall("computer", RequestAccess("calc.exe")));

        Assert.True(calc.ApprovalId is not null,
            $"request_access calc.exe ran without asking after notepad.exe was allowed once (status {calc.Status}): the remembered computer matcher carries no app list.");
    }

    [Fact]
    public async Task Case4_Computer_click_after_an_approved_grant_is_checked_against_grants_not_asked()
    {
        // Documents the current design: in standard, computer actions other than
        // request_access are WorkspaceTerminal allow decisions and are checked against
        // the session app grants when they execute (ADR 0031). No approval is ever
        // requested for them, so there is nothing to remember for a click.
        await using var env = await Scenario.StartAsync(computer: true);
        var session = await env.NewSessionAsync();
        await AllowAsync(env, session, "notepad access", ToolCall("computer", RequestAccess("notepad.exe")), "allow_once");

        var click = await RunUntilAskOrEndAsync(env, session, "click", ToolCall("computer", JsonSerializer.Serialize(new { action = "click", x = 10, y = 10 })));

        Assert.True(click.ApprovalId is null,
            $"computer click was asked for approval (status {click.Status}); in standard it is expected to be grant-checked at execution.");
    }

    // ---- Case 6: file approvals ---------------------------------------------

    [Fact]
    public async Task Case6_Premise_write_inside_the_workspace_is_not_approval_gated_in_standard()
    {
        // Premise check for the file cases: AgentPolicyProfileContracts allows
        // WorkspaceMutation in standard, so an in-workspace write never produces an
        // approval whose identity could be remembered. The remembered-file question
        // therefore only arises outside the workspace (case 6b and 6c below).
        await using var env = await Scenario.StartAsync();
        Directory.CreateDirectory(Path.Combine(env.Workspace, "src"));
        var session = await env.NewSessionAsync();

        env.Runner.Script(ToolCall("write_file", WriteFile("src/a.cs", "class A { }")), FinalStep("done"));
        var run = await env.StartRunAsync(session, "write a");
        var view = await env.WaitForAskOrEndAsync(run.Id);

        Assert.True(view.ApprovalId is null,
            $"in-workspace write_file src/a.cs was asked for approval (status {view.Status}); standard is expected to allow workspace mutations.");
        Assert.True(File.Exists(Path.Combine(env.Workspace, "src", "a.cs")), "src/a.cs was not written.");
    }

    [Fact]
    public async Task Case6_Write_allow_always_outside_the_workspace_covers_the_folder_in_a_new_session_only_there()
    {
        await using var env = await Scenario.StartAsync();
        var folderA = Path.Combine(env.Root, "external-a");
        var folderB = Path.Combine(env.Root, "external-b");
        Directory.CreateDirectory(folderA);
        Directory.CreateDirectory(folderB);
        var first = await env.NewSessionAsync();

        await AllowAsync(env, first, "external write a", ToolCall("write_file", WriteFile(Path.Combine(folderA, "a.txt"), "a")), "allow_always");

        // Policy outcome only. The durable folder rule must allow the canonical path in a new session.
        // The write itself is not asserted: in a new session the file tool's root selection uses
        // session-keyed directory grants (FileTools.cs), a separate open issue and a follow-up.
        var second = await env.NewSessionAsync();
        var sameFolder = await RunUntilAskOrEndAsync(env, second, "external write b", ToolCall("write_file", WriteFile(Path.Combine(folderA, "b.txt"), "b")));
        Assert.True(sameFolder.ApprovalId is null,
            $"write to the approved external folder asked again in a new session (status {sameFolder.Status}); the folder rule should cover it.");

        // Probes are reads: an ask is denied, so neither probe can change the filesystem if the policy is wrong.
        var sibling = await RunUntilAskOrEndAsync(env, second, "external read sibling", ToolCall("read_file", ReadFile(Path.Combine(folderB, "c.txt"))));
        Assert.True(sibling.ApprovalId is not null,
            $"read in a sibling external folder ran without asking (status {sibling.Status}); the folder rule must not widen to siblings.");
        await DenyAndEndAsync(env, sibling);

        var driveRoot = Path.Combine(Path.GetPathRoot(folderA)!, "ru-remembered-drive-root-probe.txt");
        var root = await RunUntilAskOrEndAsync(env, second, "external read drive root", ToolCall("read_file", ReadFile(driveRoot)));
        Assert.True(root.ApprovalId is not null,
            $"read at the drive root ran without asking (status {root.Status}); a folder rule must never cover a whole volume.");
        await DenyAndEndAsync(env, root);
    }

    [Fact]
    public async Task Case6_Relative_escape_from_the_workspace_is_refused_without_an_approval()
    {
        await using var env = await Scenario.StartAsync();
        var session = await env.NewSessionAsync();

        var view = await RunUntilAskOrEndAsync(env, session, "escape", ToolCall("write_file", WriteFile("..\\outside.txt", "x")));

        Assert.True(view.ApprovalId is null,
            $"relative escape ..\\outside.txt asked for approval (status {view.Status}); it is expected to be refused before any approval.");
        Assert.True(!File.Exists(Path.Combine(env.Root, "outside.txt")), "the escape wrote outside the workspace.");
    }

    // ---- Case 7: terminal baseline (must stay green) -----------------------

    [Fact]
    public async Task Case7_Terminal_allow_always_on_dotnet_test_covers_later_test_and_not_build_or_another_dotnet()
    {
        await using var env = await Scenario.StartAsync();
        var session = await env.NewSessionAsync();
        var cwd = Path.Combine(env.Root, "dotnet-cwd");
        var otherFolder = Path.Combine(env.Root, "other");
        Directory.CreateDirectory(cwd);
        Directory.CreateDirectory(otherFolder);
        var otherDotnet = Path.Combine(otherFolder, "dotnet.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "where.exe"), otherDotnet);

        await AllowAsync(env, session, "dotnet test filter",
            ToolCall("run_process", RunProcess("dotnet", ["test", "--filter", "x"], cwd)), "allow_always");

        var test = await RunUntilAskOrEndAsync(env, session, "dotnet test", ToolCall("run_process", RunProcess("dotnet", ["test"], cwd)));
        Assert.True(test.ApprovalId is null,
            $"dotnet test asked again after allow_always on dotnet test --filter (status {test.Status}); the test subcommand prefix should cover it.");

        var build = await RunUntilAskOrEndAsync(env, session, "dotnet build", ToolCall("run_process", RunProcess("dotnet", ["build"], cwd)));
        Assert.True(build.ApprovalId is not null,
            $"dotnet build ran without asking after allow_always on dotnet test (status {build.Status}).");
        await DenyAndEndAsync(env, build);

        var otherExecutable = await RunUntilAskOrEndAsync(env, session, "other dotnet", ToolCall("run_process", RunProcess(otherDotnet, ["test"], cwd)));
        Assert.True(otherExecutable.ApprovalId is not null,
            $"a different dotnet.exe in another folder ran without asking (status {otherExecutable.Status}).");
    }

    [Fact]
    public async Task Case7_Extra_allow_always_from_the_workspace_cwd_does_not_cover_an_external_cwd()
    {
        // Extra finding, now covered by the Broker plan: the remembered terminal matcher carries the
        // canonical cwd of the approved call, so an allow from the workspace root cannot cover an
        // external folder. The first call is asked for a non-cwd reason (timeout above the standard
        // window) and runs in the workspace root.
        await using var env = await Scenario.StartAsync();
        var session = await env.NewSessionAsync();
        var external = Path.Combine(env.Root, "dotnet-cwd");
        Directory.CreateDirectory(external);

        await AllowAsync(env, session, "dotnet test from workspace root",
            ToolCall("run_process", RunProcess("dotnet", ["test"], workingDirectory: null, timeoutMs: 1_000_000)), "allow_always");

        var outside = await RunUntilAskOrEndAsync(env, session, "dotnet test from external folder",
            ToolCall("run_process", RunProcess("dotnet", ["test"], external)));
        Assert.True(outside.ApprovalId is not null,
            $"dotnet test in an external folder ran without asking after allow_always from the workspace root (status {outside.Status}): the remembered rule has no cwd.");
    }

    // ---- Case 10: remembered terminal target in a new session --------------

    [Fact]
    public async Task Case10_Terminal_allow_always_covers_the_same_target_in_a_new_session_and_not_a_sibling_folder()
    {
        await using var env = await Scenario.StartAsync();
        var first = await env.NewSessionAsync();
        var cwd = Path.Combine(env.Root, "dotnet-cwd");
        var sibling = Path.Combine(env.Root, "dotnet-sibling");
        Directory.CreateDirectory(cwd);
        Directory.CreateDirectory(sibling);

        await AllowAsync(env, first, "dotnet test always",
            ToolCall("run_process", RunProcess("dotnet", ["test", "--filter", "x"], cwd)), "allow_always");

        var second = await env.NewSessionAsync();
        var same = await RunUntilAskOrEndAsync(env, second, "dotnet test in a new session",
            ToolCall("run_process", RunProcess("dotnet", ["test"], cwd)));
        Assert.True(same.ApprovalId is null,
            $"dotnet test in the approved folder asked again in a new session (status {same.Status}); the project rule should cover it.");

        var otherFolder = await RunUntilAskOrEndAsync(env, second, "dotnet test in a sibling folder",
            ToolCall("run_process", RunProcess("dotnet", ["test"], sibling)));
        Assert.True(otherFolder.ApprovalId is not null,
            $"dotnet test in a sibling folder ran without asking (status {otherFolder.Status}); the remembered cwd must not widen to siblings.");
        await DenyAndEndAsync(env, otherFolder);
    }

    // ---- Case 11: a drive root is never remembered -------------------------

    [Fact]
    public async Task Case11_Allow_always_on_a_drive_root_read_is_one_time_and_grants_no_session_folder()
    {
        await using var env = await Scenario.StartAsync();
        var session = await env.NewSessionAsync();
        var volume = Path.GetPathRoot(env.Root)!;
        var first = Path.Combine(volume, "ru-remembered-root-a-" + Guid.NewGuid().ToString("N") + ".txt");
        var sibling = Path.Combine(volume, "ru-remembered-root-b-" + Guid.NewGuid().ToString("N") + ".txt");

        var view = await RunUntilAskOrEndAsync(env, session, "drive root read", ToolCall("read_file", ReadFile(first)));
        Assert.True(view.ApprovalId is not null, $"the drive-root read was not asked (status {view.Status}).");

        using var decision = await env.DecideAsync(view.RunId, view.ApprovalId!, "allow_always");
        var body = await decision.Content.ReadAsStringAsync();
        Assert.True(decision.StatusCode == HttpStatusCode.OK,
            $"allow_always on a drive-root read returned {(int)decision.StatusCode}, expected 200 with a one-time approval: {body}");
        var json = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.False(json.GetProperty("rule_saved").GetBoolean(), "allow_always on a drive-root read saved a folder rule.");
        await env.WaitForEndAsync(view.RunId);

        var next = await RunUntilAskOrEndAsync(env, session, "drive root sibling read", ToolCall("read_file", ReadFile(sibling)));
        Assert.True(next.ApprovalId is not null,
            $"a sibling file at the drive root ran without asking (status {next.Status}): the volume was granted for the session.");
        await DenyAndEndAsync(env, next);
    }

    // ---- Case 8: ordering when the Broker grant fails ----------------------

    [Fact]
    public async Task Case8_Failed_broker_grant_after_allow_always_leaves_no_durable_rule()
    {
        await using var env = await Scenario.StartAsync(failGrants: true);
        var session = await env.NewSessionAsync();
        var cwd = Path.Combine(env.Root, "cwd");
        Directory.CreateDirectory(cwd);

        env.Runner.Script(ToolCall("run_process", RunProcess("cmd.exe", ["/d", "/c", "echo", "order"], cwd)), FinalStep("done"));
        var started = await env.StartRunAsync(session, "durable ordering");
        var view = await env.WaitForAskOrEndAsync(started.Id);
        Assert.True(view.ApprovalId is not null, $"the terminal call was not asked (status {view.Status}).");

        using var decision = await env.DecideAsync(started.Id, view.ApprovalId!, "allow_always");
        var body = await decision.Content.ReadAsStringAsync();
        Assert.True(decision.StatusCode == HttpStatusCode.BadGateway,
            $"allow_always with a failed Broker grant returned {(int)decision.StatusCode}, expected 502: {body}");

        var rules = await env.ProjectRuleCountAsync(started.WorkspaceId);
        Assert.True(rules == 0,
            $"a durable project rule was saved although the Broker never authorized the call ({rules} rule(s)); the rule is written before the grant.");
    }

    [Fact]
    public async Task Case8_Failed_broker_grant_after_allow_once_leaves_no_session_memory()
    {
        await using var env = await Scenario.StartAsync(failGrants: true);
        var session = await env.NewSessionAsync();
        var cwd = Path.Combine(env.Root, "cwd");
        Directory.CreateDirectory(cwd);

        var view = await RunUntilAskOrEndAsync(env, session, "session ordering",
            ToolCall("run_process", RunProcess("cmd.exe", ["/d", "/c", "echo", "order"], cwd)));
        Assert.True(view.ApprovalId is not null, $"the terminal call was not asked (status {view.Status}).");

        using var decision = await env.DecideAsync(view.RunId, view.ApprovalId!, "allow_once");
        Assert.True(decision.StatusCode == HttpStatusCode.BadGateway,
            $"allow_once with a failed Broker grant returned {(int)decision.StatusCode}, expected 502.");

        var remembered = SessionApprovalLedger.Entries(session, DateTimeOffset.UtcNow).Count;
        Assert.True(remembered == 0,
            $"{remembered} session memory entr(ies) remain after the Broker refused the grant: session memory is written before the grant.");
    }

    [Fact]
    public async Task Case8_Failed_broker_grant_after_allow_always_leaves_no_session_memory()
    {
        await using var env = await Scenario.StartAsync(failGrants: true);
        var session = await env.NewSessionAsync();
        var cwd = Path.Combine(env.Root, "cwd");
        Directory.CreateDirectory(cwd);

        var view = await RunUntilAskOrEndAsync(env, session, "session ordering always",
            ToolCall("run_process", RunProcess("cmd.exe", ["/d", "/c", "echo", "order"], cwd)));
        Assert.True(view.ApprovalId is not null, $"the terminal call was not asked (status {view.Status}).");

        using var decision = await env.DecideAsync(view.RunId, view.ApprovalId!, "allow_always");
        Assert.True(decision.StatusCode == HttpStatusCode.BadGateway,
            $"allow_always with a failed Broker grant returned {(int)decision.StatusCode}, expected 502.");

        var remembered = SessionApprovalLedger.Entries(session, DateTimeOffset.UtcNow).Count;
        Assert.True(remembered == 0,
            $"{remembered} session memory entr(ies) remain after the Broker refused the grant.");
    }

    // ---- Case 9 (extra): MCP calls that carry arguments --------------------

    [Fact]
    public async Task Case9_Extra_mcp_call_that_carries_arguments_can_be_approved()
    {
        // Extra finding, now fixed: the MCP arguments field is a JSON object encoded as a string. The
        // Host used to re-parse the call and reject it (approval_stale); the Broker binds the call from
        // its PolicyContext, so the approval resolves.
        await using var env = await Scenario.StartAsync(mcp: true);
        var session = await env.NewSessionAsync();
        var call = JsonSerializer.Serialize(new
        {
            op = "call",
            server = "alpha",
            tool = "read_note",
            arguments = "{\"note\":\"x\"}",
        });

        var view = await RunUntilAskOrEndAsync(env, session, "alpha read with arguments", ToolCall("mcp", call));
        Assert.True(view.ApprovalId is not null, $"the MCP call with arguments was not asked (status {view.Status}).");

        using var decision = await env.DecideAsync(view.RunId, view.ApprovalId!, "allow_once");
        var body = await decision.Content.ReadAsStringAsync();
        Assert.True(decision.StatusCode == HttpStatusCode.OK,
            $"allow_once on an MCP call with arguments returned {(int)decision.StatusCode}: {body}");
    }

    // ---- helpers --------------------------------------------------------------

    private static async Task<RunView> RunUntilAskOrEndAsync(Scenario env, string sessionId, string prompt, AssistantStep call)
    {
        env.Runner.Script(call, FinalStep("done"));
        var started = await env.StartRunAsync(sessionId, prompt);
        return await env.WaitForAskOrEndAsync(started.Id);
    }

    /// <summary>Asks, decides with <paramref name="command"/>, and waits for the run to end. The first ask must happen.</summary>
    private static async Task<RunView> AllowAsync(Scenario env, string sessionId, string prompt, AssistantStep call, string command)
    {
        var view = await RunUntilAskOrEndAsync(env, sessionId, prompt, call);
        Assert.True(view.ApprovalId is not null, $"'{prompt}' was not asked for approval (status {view.Status}).");

        using var decision = await env.DecideAsync(view.RunId, view.ApprovalId!, command);
        var body = await decision.Content.ReadAsStringAsync();
        Assert.True(decision.StatusCode == HttpStatusCode.OK,
            $"'{prompt}' {command} returned {(int)decision.StatusCode}: {body}");
        if (command == "allow_always")
        {
            var json = JsonSerializer.Deserialize<JsonElement>(body);
            Assert.True(json.GetProperty("rule_saved").GetBoolean(), $"'{prompt}' allow_always did not save a project rule.");
        }

        return await env.WaitForEndAsync(view.RunId);
    }

    private static async Task DenyAndEndAsync(Scenario env, RunView view)
    {
        if (view.ApprovalId is null)
        {
            return;
        }

        using var decision = await env.DecideAsync(view.RunId, view.ApprovalId, "deny");
        await env.WaitForEndAsync(view.RunId);
    }

    private static string WriteFile(string path, string content) =>
        JsonSerializer.Serialize(new { path, content, overwrite = false });

    private static string ReadFile(string path) => JsonSerializer.Serialize(new { path });

}
