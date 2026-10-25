using System.Text.Json;
using RuAgent.Harness.Core;
using RuBackend.Harness.Hooks;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class HookConfigurationTests
{
    [Fact]
    public void Parses_events_matchers_and_clamps_timeouts_while_skipping_invalid_entries()
    {
        var hooks = HookConfiguration.Parse("""
            {"hooks":{
              "PreToolUse":[{"matcher":"run_process|edit_file","command":"a"},{"command":""},{"command":"b","timeout_seconds":9999},{"command":"c","timeout_seconds":0}],
              "PostToolUse":[{"command":"d"}],
              "Unknown":[{"command":"e"}]}}
            """);

        Assert.Equal(["a", "b", "d"], hooks.Select(hook => hook.Command));
        Assert.Equal(HookConfiguration.MaxTimeout, hooks[1].Timeout);
        Assert.True(hooks[0].Matches("EDIT_FILE"));
        Assert.False(hooks[0].Matches("read"));
        Assert.True(hooks[1].Matches("anything"));
    }

    [Fact]
    public void Invalid_regex_never_matches_and_never_throws()
    {
        var hook = new HookDefinition(HookEvent.PreToolUse, "(", "x", TimeSpan.FromSeconds(1));

        Assert.False(hook.Matches("run_process"));
    }

    [Fact]
    public void Load_merges_user_then_workspace_and_ignores_malformed_files()
    {
        var home = Directory.CreateTempSubdirectory("ru-hooks-home").FullName;
        var workspace = Directory.CreateTempSubdirectory("ru-hooks-ws").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(home, ".ru"));
            Directory.CreateDirectory(Path.Combine(workspace, ".ru"));
            File.WriteAllText(Path.Combine(home, ".ru", "hooks.json"), """{"hooks":{"PreToolUse":[{"command":"user"}]}}""");
            File.WriteAllText(Path.Combine(workspace, ".ru", "hooks.json"), "{ not json");

            var loaded = HookConfiguration.Load(workspace, home);

            Assert.Equal("user", Assert.Single(loaded.For(HookEvent.PreToolUse, "run_process")).Command);
            Assert.True(HookConfiguration.Load(workspace, null).IsEmpty);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(workspace, recursive: true);
        }
    }
}

public sealed class HookProcessRunnerTests
{
    private static HookDefinition Hook(string command, int seconds = 10) =>
        new(HookEvent.PreToolUse, null, command, TimeSpan.FromSeconds(seconds));

    [Fact]
    public async Task Exit_code_two_blocks_and_stderr_is_the_message()
    {
        var result = await new HookProcessRunner().RunAsync(
            Hook("echo not allowed 1>&2 & exit /b 2"), "{}", Path.GetTempPath(), CancellationToken.None);

        Assert.True(result.Blocks);
        Assert.Equal("not allowed", result.Message);
    }

    [Fact]
    public async Task Event_json_reaches_stdin_and_other_failures_do_not_block()
    {
        var output = Path.Combine(Path.GetTempPath(), "ru-hook-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var result = await new HookProcessRunner().RunAsync(
                Hook($"findstr \"^\" > \"{output}\" & exit /b 1"), """{"tool_id":"run_process"}""", Path.GetTempPath(), CancellationToken.None);

            Assert.False(result.Blocks);
            Assert.Equal(1, result.ExitCode);
            Assert.Contains("\"tool_id\":\"run_process\"", File.ReadAllText(output));
        }
        finally { File.Delete(output); }
    }

    [Fact]
    public async Task Timeout_kills_the_hook_and_reports_it_without_blocking()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = await new HookProcessRunner().RunAsync(
            Hook("ping -n 30 127.0.0.1 > nul", seconds: 1), "{}", Path.GetTempPath(), CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.False(result.Blocks);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Credential_environment_is_not_inherited()
    {
        Environment.SetEnvironmentVariable("RU_TEST_HOOK_API_KEY", "super-secret-value");
        try
        {
            var result = await new HookProcessRunner().RunAsync(
                Hook("if defined RU_TEST_HOOK_API_KEY (exit /b 2) else (exit /b 0)"), "{}", Path.GetTempPath(), CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
        }
        finally { Environment.SetEnvironmentVariable("RU_TEST_HOOK_API_KEY", null); }
    }
}

public sealed class HookToolGatewayTests
{
    private static readonly RunId Run = new("run_" + new string('a', 32));

    private static ToolProposal Proposal(string toolId, string call = "1") => new(
        Run, new ModelStepId("step_" + new string('0', 32)), new ToolCallId("call_" + new string(call[0], 32)),
        "provider_call_1", 1, toolId, "1.0.0", JsonSerializer.SerializeToElement(new { path = "a.txt" }));

    private static HookToolGateway Gateway(FakeGateway inner, FakeRunner runner, string json) =>
        new(inner, HookConfiguration.Load(WriteWorkspace(json), null), runner, Path.GetTempPath());

    private static string WriteWorkspace(string json)
    {
        var root = Directory.CreateTempSubdirectory("ru-hook-gw").FullName;
        Directory.CreateDirectory(Path.Combine(root, ".ru"));
        File.WriteAllText(Path.Combine(root, ".ru", "hooks.json"), json);
        return root;
    }

    [Fact]
    public async Task Blocking_pre_hook_denies_without_reaching_policy_evaluation()
    {
        var inner = new FakeGateway();
        var runner = new FakeRunner(new HookResult(2, "no edits on main", false));
        var gateway = Gateway(inner, runner, """{"hooks":{"PreToolUse":[{"matcher":"edit_file","command":"x"}]}}""");

        var decision = await gateway.EvaluateAsync(Proposal("edit_file"), CancellationToken.None);

        Assert.Equal(RuAgent.Harness.Core.Policy.PolicyDecision.Deny, decision.Decision);
        Assert.Equal(HookToolGateway.BlockedCode, decision.SafeCode);
        Assert.Equal("hook: no edits on main", decision.DenyHint);
        Assert.Equal(0, inner.Evaluations);
    }

    [Fact]
    public async Task Non_matching_tools_and_non_blocking_results_reach_the_inner_gateway()
    {
        var inner = new FakeGateway();
        var runner = new FakeRunner(new HookResult(1, "warn", false));
        var gateway = Gateway(inner, runner, """{"hooks":{"PreToolUse":[{"matcher":"edit_file","command":"x"}]}}""");

        await gateway.EvaluateAsync(Proposal("read"), CancellationToken.None);
        await gateway.EvaluateAsync(Proposal("edit_file"), CancellationToken.None);

        Assert.Equal(2, inner.Evaluations);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Post_hook_runs_once_after_execution_with_the_outcome()
    {
        var inner = new FakeGateway();
        var runner = new FakeRunner(new HookResult(0, string.Empty, false));
        var gateway = Gateway(inner, runner, """{"hooks":{"PostToolUse":[{"command":"fmt"}]}}""");
        var proposal = Proposal("write_file");
        await gateway.EvaluateAsync(proposal, CancellationToken.None);
        var prepared = new PreparedToolExecution(
            new ExecutionId("exec_" + new string('c', 32)), proposal.ToolCallId, 1, ToolAccessKind.Mutation, "token", []);

        await gateway.ExecuteAsync(prepared, _ => ValueTask.CompletedTask, CancellationToken.None);
        await gateway.ExecuteAsync(prepared, _ => ValueTask.CompletedTask, CancellationToken.None);

        var call = Assert.Single(runner.Calls);
        using var document = JsonDocument.Parse(call);
        Assert.Equal("PostToolUse", document.RootElement.GetProperty("event").GetString());
        Assert.Equal("Succeeded", document.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("a.txt", document.RootElement.GetProperty("arguments").GetProperty("path").GetString());
    }

    [Fact]
    public async Task Runner_start_failure_never_blocks_the_tool()
    {
        var inner = new FakeGateway();
        var gateway = Gateway(inner, new FakeRunner(null), """{"hooks":{"PreToolUse":[{"command":"x"}]}}""");

        var decision = await gateway.EvaluateAsync(Proposal("run_process"), CancellationToken.None);

        Assert.Equal(RuAgent.Harness.Core.Policy.PolicyDecision.Allow, decision.Decision);
    }

    private sealed class FakeRunner(HookResult? result) : IHookRunner
    {
        public List<string> Calls { get; } = [];

        public Task<HookResult> RunAsync(HookDefinition hook, string stdinJson, string workingDirectory, CancellationToken cancellationToken)
        {
            Calls.Add(stdinJson);
            return result is null ? throw new InvalidOperationException("hook_start_failed") : Task.FromResult(result);
        }
    }

    private sealed class FakeGateway : IToolGateway
    {
        public int Evaluations { get; private set; }

        public Task<ToolProposalDecision> EvaluateAsync(ToolProposal proposal, CancellationToken cancellationToken)
        {
            Evaluations++;
            return Task.FromResult(ToolProposalDecision.Allow(ToolAccessKind.ReadOnly, "intent"));
        }

        public Task<PreparedToolExecution> PrepareAsync(ApprovedToolExecution execution, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ToolExecutionReceipt> ExecuteAsync(
            PreparedToolExecution execution, Func<ToolOutputChunk, ValueTask> publishDelta, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolExecutionReceipt(
                execution.ExecutionId, execution.ToolCallId, ToolExecutionOutcome.Succeeded, "ok", null, false));

        public Task CancelAsync(ExecutionId executionId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
