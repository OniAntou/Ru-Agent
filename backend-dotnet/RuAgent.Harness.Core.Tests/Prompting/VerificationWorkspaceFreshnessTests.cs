using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using Xunit;

namespace RuAgent.Harness.Core.Tests.Prompting;

public sealed class VerificationWorkspaceFreshnessTests
{
    [Fact]
    public void Workspace_change_during_running_check_stales_its_later_zero_exit()
    {
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var pending = Project(startedAt, """{"state":"running","execution_id":"exec_11111111111111111111111111111111"}""",
            ToolExecutionOutcome.Succeeded, "ok");

        var invalidated = pending.ApplyWorkspaceFreshness(new Dictionary<DateTimeOffset, WorkspaceCheckFreshness>
        {
            [startedAt] = new(IsKnown: true, ChangedAfterStart: true),
        });
        var retainedPending = Assert.Single(invalidated.Checks);
        Assert.Equal(VerificationCheckStatus.Pending, retainedPending.Status);
        Assert.True(retainedPending.InvalidatedDuringExecution);

        using var readArguments = JsonDocument.Parse("""{"mode":"read_output","execution_id":"exec_11111111111111111111111111111111"}""");
        var read = ModelContextEntry.Assistant("assistant-read", 3,
            new AssistantStep(null, [new ModelToolCall("read-check", "bash", "1.0.0", readArguments.RootElement)],
                AssistantFinishReason.ToolCalls, 1));
        var receipt = new ToolExecutionReceipt(new ExecutionId("exec_11111111111111111111111111111111"),
            new ToolCallId("call_11111111111111111111111111111111"), ToolExecutionOutcome.Succeeded,
            "ok", null, false, providerCallId: "read-check");
        var result = ModelContextEntry.ToolResult("tool-read", 4, receipt, """{"exit_code":0,"state":"exited"}""");
        var completed = RunVerificationEvidence.Project([read, result], invalidated);

        var check = Assert.Single(completed.Checks);
        Assert.Equal(VerificationCheckStatus.Stale, check.Status);
        Assert.Equal("verification_stale", check.Code);
        Assert.Equal("verification_stale", completed.BlockingCode);
    }

    [Fact]
    public void Workspace_freshness_keeps_failed_evidence_and_marks_unknown_pass_stale()
    {
        var failed = Project(DateTimeOffset.UtcNow.AddMinutes(-2), """{"exit_code":1}""",
            ToolExecutionOutcome.Failed, "process_exit_nonzero");
        var passed = Project(null, """{"exit_code":0}""", ToolExecutionOutcome.Succeeded, "ok");

        var retainedFailure = failed.ApplyWorkspaceFreshness(new Dictionary<DateTimeOffset, WorkspaceCheckFreshness>());
        var unknownPass = passed.ApplyWorkspaceFreshness(new Dictionary<DateTimeOffset, WorkspaceCheckFreshness>());

        Assert.Equal(VerificationCheckStatus.Failed, Assert.Single(retainedFailure.Checks).Status);
        Assert.Equal("process_exit_nonzero", retainedFailure.BlockingCode);
        Assert.Equal(VerificationCheckStatus.Stale, Assert.Single(unknownPass.Checks).Status);
        Assert.Equal("verification_freshness_unknown", unknownPass.BlockingCode);
    }

    private static RunVerificationEvidence Project(DateTimeOffset? startedAtUtc, string output,
        ToolExecutionOutcome outcome, string code)
    {
        using var arguments = JsonDocument.Parse("""{"mode":"shell","script":"dotnet test","purpose":"verification"}""");
        var step = ModelContextEntry.Assistant("assistant-check", 1,
            new AssistantStep(null, [new ModelToolCall("check", "bash", "1.0.0", arguments.RootElement)],
                AssistantFinishReason.ToolCalls, 1));
        var receipt = new ToolExecutionReceipt(new ExecutionId("exec_11111111111111111111111111111111"),
            new ToolCallId("call_11111111111111111111111111111111"), outcome, code, null,
            sideEffectsPossible: false, providerCallId: "check");
        var result = ModelContextEntry.ToolResult("tool-check", 2, receipt, output, startedAtUtc);
        return RunVerificationEvidence.Project([step, result]);
    }
}
