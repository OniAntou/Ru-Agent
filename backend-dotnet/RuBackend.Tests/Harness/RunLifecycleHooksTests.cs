using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using RuAgent.Harness.Core;
using RuAgent.Protocol.Harness;
using RuBackend.Harness.Hooks;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class RunLifecycleHooksTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("ru-lifecycle-home").FullName;
    private readonly string _workspace = Directory.CreateTempSubdirectory("ru-lifecycle-ws").FullName;

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        Directory.Delete(_workspace, recursive: true);
    }

    [Fact]
    public async Task A_blocking_prompt_hook_refuses_the_prompt_with_its_message()
    {
        WriteHooks(_workspace, """{"hooks":{"UserPromptSubmit":[{"command":"check-prompt"}]}}""");
        var runner = new RecordingRunner(new HookResult(HookResult.BlockExitCode, "No deploys on Friday.", TimedOut: false));

        var refusal = await Hooks(runner).SubmitPromptAsync("deploy it", "session-1", _workspace, workspaceTrusted: true, CancellationToken.None);

        Assert.Equal("No deploys on Friday.", refusal);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(_workspace, call.WorkingDirectory);
        using var input = JsonDocument.Parse(call.Input);
        Assert.Equal("UserPromptSubmit", input.RootElement.GetProperty("event").GetString());
        Assert.Equal("deploy it", input.RootElement.GetProperty("prompt").GetString());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(HookResult.BlockExitCode, true)]
    public async Task Only_exit_code_two_blocks_and_a_timeout_never_does(int exitCode, bool timedOut)
    {
        WriteHooks(_workspace, """{"hooks":{"UserPromptSubmit":[{"command":"check-prompt"}]}}""");
        var runner = new RecordingRunner(new HookResult(exitCode, "message", timedOut));

        var refusal = await Hooks(runner).SubmitPromptAsync("hello", "session-1", _workspace, workspaceTrusted: true, CancellationToken.None);

        Assert.Null(refusal);
    }

    [Fact]
    public async Task An_untrusted_workspace_runs_only_the_users_hooks_from_the_profile_folder()
    {
        WriteHooks(_workspace, """{"hooks":{"UserPromptSubmit":[{"command":"workspace-hook"}]}}""");
        WriteHooks(_home, """{"hooks":{"UserPromptSubmit":[{"command":"user-hook"}]}}""");
        var runner = new RecordingRunner(new HookResult(0, string.Empty, TimedOut: false));

        await Hooks(runner).SubmitPromptAsync("hello", "session-1", _workspace, workspaceTrusted: false, CancellationToken.None);

        var call = Assert.Single(runner.Calls);
        Assert.Equal("user-hook", call.Command);
        Assert.Equal(_home, call.WorkingDirectory);
    }

    [Fact]
    public async Task A_large_prompt_is_left_out_of_the_hook_input()
    {
        WriteHooks(_workspace, """{"hooks":{"UserPromptSubmit":[{"command":"check-prompt"}]}}""");
        var runner = new RecordingRunner(new HookResult(0, string.Empty, TimedOut: false));

        await Hooks(runner).SubmitPromptAsync(
            new string('x', RunLifecycleHooks.MaxPromptBytes + 1), "session-1", _workspace, workspaceTrusted: true, CancellationToken.None);

        using var input = JsonDocument.Parse(Assert.Single(runner.Calls).Input);
        Assert.Equal(JsonValueKind.Null, input.RootElement.GetProperty("prompt").ValueKind);
        Assert.True(input.RootElement.GetProperty("prompt_omitted").GetBoolean());
    }

    [Fact]
    public async Task Stop_hooks_run_once_for_a_finished_run_and_ignore_tool_matchers()
    {
        WriteHooks(_workspace, """{"hooks":{"Stop":[{"matcher":"bash","command":"notify"}]}}""");
        var runner = new RecordingRunner(new HookResult(0, string.Empty, TimedOut: false));
        var hooks = Hooks(runner);
        var run = Run(HarnessRunState.Failed, "approval_denied");

        hooks.RunFinished(run, "failed", _workspace);
        hooks.RunFinished(run, "failed", _workspace);

        var call = await runner.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var input = JsonDocument.Parse(call.Input);
        Assert.Equal("Stop", input.RootElement.GetProperty("event").GetString());
        Assert.Equal("failed", input.RootElement.GetProperty("status").GetString());
        Assert.Equal("approval_denied", input.RootElement.GetProperty("terminal_error_code").GetString());
        await Task.Delay(200);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public void A_run_that_has_not_finished_starts_no_stop_hook()
    {
        WriteHooks(_workspace, """{"hooks":{"Stop":[{"command":"notify"}]}}""");
        var runner = new RecordingRunner(new HookResult(0, string.Empty, TimedOut: false));

        Hooks(runner).RunFinished(Run(HarnessRunState.Running, null), "running", _workspace);

        Assert.Empty(runner.Calls);
    }

    private RunLifecycleHooks Hooks(IHookRunner runner) =>
        new(runner, NullLogger<RunLifecycleHooks>.Instance, _home, CancellationToken.None);

    private static void WriteHooks(string root, string json)
    {
        Directory.CreateDirectory(Path.Combine(root, ".ru"));
        File.WriteAllText(Path.Combine(root, ".ru", "hooks.json"), json);
    }

    private static AgentRun Run(HarnessRunState state, string? errorCode)
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentRun(
            new RunId("run_" + Guid.NewGuid().ToString("N")),
            new SessionId(Guid.NewGuid().ToString("N")),
            new WorkspaceId("ws_" + Guid.NewGuid().ToString("N")),
            new HarnessRunStatus(state),
            version: 1,
            lastSequence: 1,
            budgetWindow: 1,
            modelStepsInWindow: 0,
            toolCallsInWindow: 0,
            AgentPermissionPreset.Standard,
            policySnapshotVersion: 1,
            policySnapshotSha256: new string('a', 64),
            createdAt: now,
            updatedAt: now,
            terminalAt: state is HarnessRunState.Running ? null : now,
            terminalErrorCode: errorCode);
    }

    private sealed record HookCall(string Command, string Input, string WorkingDirectory);

    private sealed class RecordingRunner(HookResult result) : IHookRunner
    {
        private readonly ConcurrentQueue<HookCall> _calls = new();

        public IReadOnlyCollection<HookCall> Calls => _calls.ToArray();

        public TaskCompletionSource<HookCall> FirstCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<HookResult> RunAsync(HookDefinition hook, string stdinJson, string workingDirectory, CancellationToken cancellationToken)
        {
            var call = new HookCall(hook.Command, stdinJson, workingDirectory);
            _calls.Enqueue(call);
            FirstCall.TrySetResult(call);
            return Task.FromResult(result);
        }
    }
}

public sealed class UserPromptSubmitEndpointTests
{
    [Fact]
    public async Task A_refused_prompt_answers_422_and_creates_no_run()
    {
        var home = Directory.CreateTempSubdirectory("ru-prompt-hook-home").FullName;
        var workspace = Directory.CreateTempSubdirectory("ru-prompt-hook-ws").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(home, ".ru"));
            File.WriteAllText(Path.Combine(home, ".ru", "hooks.json"), """{"hooks":{"UserPromptSubmit":[{"command":"guard"}]}}""");
            var hooks = new RunLifecycleHooks(
                new BlockingRunner(), NullLogger<RunLifecycleHooks>.Instance, home, CancellationToken.None);
            using var factory = new BackendWebApplicationFactory(configureServices: services =>
            {
                services.RemoveAll<RunLifecycleHooks>();
                services.AddSingleton(hooks);
            });
            using var client = factory.CreateAuthenticatedClient();
            using var sessionResponse = await client.PostAsync("/sessions/new", null);
            var sessionId = (await sessionResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("session_id").GetString()!;

            using var response = await client.PostAsJsonAsync("/agent/v1/runs", new
            {
                session_id = sessionId,
                workspace_path = workspace,
                prompt = "ship it",
                preset = "standard",
                idempotency_key = Guid.NewGuid().ToString("N"),
            });

            Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("prompt_blocked_by_hook", body.GetProperty("error_code").GetString());
            Assert.Equal("Not today.", body.GetProperty("message").GetString());
            using var runs = await client.GetAsync($"/agent/v1/sessions/{sessionId}/runs");
            Assert.DoesNotContain("run_", await runs.Content.ReadAsStringAsync());
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(workspace, recursive: true);
        }
    }

    private sealed class BlockingRunner : IHookRunner
    {
        public Task<HookResult> RunAsync(HookDefinition hook, string stdinJson, string workingDirectory, CancellationToken cancellationToken) =>
            Task.FromResult(new HookResult(HookResult.BlockExitCode, "Not today.", TimedOut: false));
    }
}