using Microsoft.Extensions.DependencyInjection;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuBackend.Business.Sessions;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class HeadlessCodingLoopTests
{
    [Fact]
    public async Task Exit_mismatch_is_held_three_times_then_the_run_completes_without_a_blocked_report()
    {
        using var workspace = GitWorkspace.Create();
        var runner = new ScriptedRunner();
        runner.Enqueue(ToolCall("run_process", """{"executable":"cmd.exe","arguments":["/d","/c","exit","7"],"purpose":"expected_failure","expected_exit_code":9}"""));
        for (var attempt = 0; attempt <= AgentRecoveryNotes.MaximumUnverifiedNotes; attempt++)
            runner.Enqueue(FinalStep("All checks passed."));
        using var factory = new BackendWebApplicationFactory(dataPaths: workspace.DataPaths, modelStepRunner: runner);
        using var client = factory.CreateAuthenticatedClient();
        var runId = await CreateTrustedRunAsync(client, workspace.Path);
        Assert.True(await WaitForStatusAsync(client, runId,
            new HashSet<string>(StringComparer.Ordinal) { "failed", "completed" }, TimeSpan.FromSeconds(30)));
        var store = factory.Services.GetRequiredService<IHarnessEventStore>();
        var run = await store.GetRunAsync(new RunId(runId), default);
        Assert.Equal(HarnessRunState.Completed, run!.State);
        Assert.Null(run.TerminalErrorCode);
        var events = await ReadEventsAsync(client, runId);
        Assert.Equal(AgentRecoveryNotes.MaximumUnverifiedNotes, events.Count(e =>
            e.GetProperty("safe_summary").GetString() == "unverified_after_tool_failure_advised"));
        Assert.DoesNotContain(events, e => e.GetProperty("safe_summary").GetString() == "blocked_report");
    }

    [Fact]
    public async Task Blocked_reply_after_an_exit_mismatch_fails_the_run_with_the_model_report_durably()
    {
        using var workspace = GitWorkspace.Create();
        var runner = new ScriptedRunner();
        runner.Enqueue(ToolCall("run_process", """{"executable":"cmd.exe","arguments":["/d","/c","exit","7"],"purpose":"expected_failure","expected_exit_code":9}"""));
        runner.Enqueue(FinalStep("[BLOCKED] All checks passed."));
        using var factory = new BackendWebApplicationFactory(dataPaths: workspace.DataPaths, modelStepRunner: runner);
        using var client = factory.CreateAuthenticatedClient();
        var runId = await CreateTrustedRunAsync(client, workspace.Path);
        Assert.True(await WaitForStatusAsync(client, runId,
            new HashSet<string>(StringComparer.Ordinal) { "failed", "completed" }, TimeSpan.FromSeconds(30)));
        var store = factory.Services.GetRequiredService<IHarnessEventStore>();
        var run = await store.GetRunAsync(new RunId(runId), default);
        Assert.Equal(HarnessRunState.Failed, run!.State);
        Assert.Equal("expected_exit_mismatch", run.TerminalErrorCode);
        var events = await ReadEventsAsync(client, runId);
        Assert.DoesNotContain(events, e => e.GetProperty("safe_summary").GetString() == "unverified_after_tool_failure_advised");
        var reportEvent = Assert.Single(events, e => e.GetProperty("event_type").GetString() == "model.step_completed"
            && e.GetProperty("safe_summary").GetString() == "blocked_report");
        var payloadId = reportEvent.GetProperty("payload_ref").GetString();
        var payload = await client.GetStringAsync($"/agent/v1/runs/{runId}/payloads/{payloadId}");
        Assert.Contains("All checks passed.", payload);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        var history = "";
        var sessions = factory.Services.GetRequiredService<ISessionStore>();
        while (DateTime.UtcNow < deadline)
        {
            var document = await sessions.GetAsync(run.SessionId.Value, default);
            history = string.Join("\n", document!.Messages.Select(message => message.Content));
            if (history.Contains("All checks passed.", StringComparison.Ordinal)) break;
            await Task.Delay(100);
        }
        Assert.Contains("All checks passed.", history);
        Assert.Equal(HarnessRunState.Failed, (await store.GetRunAsync(new RunId(runId), default))!.State);
    }
}
