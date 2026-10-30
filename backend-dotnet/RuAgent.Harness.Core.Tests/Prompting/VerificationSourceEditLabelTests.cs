using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Harness.Core.Tests.Prompting;

/// <summary>
/// ADR 0039 summary label. verified needs a declared check executed after the last structured
/// source edit with no outstanding declared identity; not_applicable needs no source edit and no
/// executed declared check; everything else is unverified. Computer input never changes the label.
/// </summary>
public sealed class VerificationSourceEditLabelTests
{
    private const string DeclaredTest = "{\"mode\":\"shell\",\"script\":\"dotnet test\",\"purpose\":\"verification\"}";
    private const string SourceEdit = "{\"path\":\"src/Widget.cs\",\"content\":\"class Widget {}\"}";

    [Fact]
    public void Passing_check_then_a_source_edit_is_unverified_until_a_new_declared_check_passes()
    {
        Assert.Equal("unverified", Label(Passing(1).Concat(Edit(3))));
        Assert.Equal("verified", Label(Passing(1).Concat(Edit(3)).Concat(Passing(5))));
    }

    [Fact]
    public void A_source_edit_followed_by_a_passing_check_is_verified()
    {
        Assert.Equal("verified", Label(Edit(1).Concat(Passing(3))));
    }

    [Fact]
    public void A_failed_check_after_a_source_edit_is_unverified()
    {
        Assert.Equal("unverified", Label(Edit(1).Concat(Failing(3))));
    }

    [Fact]
    public void A_passing_declared_check_without_edits_is_verified()
    {
        Assert.Equal("verified", Label(Passing(1)));
    }

    [Fact]
    public void A_failed_declared_check_without_edits_is_unverified()
    {
        Assert.Equal("unverified", Label(Failing(1)));
    }

    [Fact]
    public void A_run_without_edits_or_executed_declared_checks_is_not_applicable()
    {
        Assert.Equal("not_applicable", Label(Call(1, "read", "read_file", "{\"path\":\"src/Widget.cs\"}")));
        Assert.Equal("not_applicable", Label(Call(1, "status", "bash", "{\"mode\":\"shell\",\"script\":\"git status\"}",
            ToolExecutionOutcome.Failed, "process_exit_nonzero", "{\"exit_code\":128}")));
    }

    [Fact]
    public void A_documentation_edit_alone_is_not_applicable()
    {
        Assert.Equal("not_applicable", Label(Call(1, "docs", "write_file", "{\"path\":\"docs/notes.md\",\"content\":\"x\"}")));
    }

    [Fact]
    public void Expected_failure_only_run_after_edits_with_its_exact_exit_is_verified()
    {
        Assert.Equal("verified", Label(Edit(1).Concat(Negative(3, exit: 7, observed: 7))));
    }

    [Fact]
    public void Expected_failure_only_run_after_edits_with_a_different_exit_is_unverified()
    {
        Assert.Equal("unverified", Label(Edit(1).Concat(Negative(3, exit: 7, observed: 1))));
    }

    [Fact]
    public void Expected_failure_only_run_without_edits_with_its_exact_exit_is_verified()
    {
        Assert.Equal("verified", Label(Negative(1, exit: 7, observed: 7)));
    }

    [Fact]
    public void A_rejected_declared_check_does_not_change_the_label()
    {
        var rejected = Call(3, "malformed", "run_process", "{\"executable\":\"python\",\"purpose\":\"verification\"}",
            ToolExecutionOutcome.Failed, "malformed_call", "{}");

        Assert.Equal("not_applicable", Label(rejected));
        Assert.Equal("unverified", Label(Edit(1).Concat(rejected)));
        Assert.Equal("verified", Label(Passing(1).Concat(rejected)));
    }

    [Fact]
    public void A_failed_check_is_not_retired_by_a_different_passing_check_for_the_label()
    {
        var entries = Failing(1).Concat(Edit(3))
            .Concat(Call(5, "lint", "bash", DeclaredTest.Replace("dotnet test", "dotnet format"), content: "{\"exit_code\":0}"));

        Assert.Equal("unverified", Label(entries));
    }

    [Fact]
    public void The_same_failed_check_passing_after_an_edit_makes_the_label_verified()
    {
        var entries = Failing(1).Concat(Edit(3)).Concat(Passing(5));

        Assert.Equal("verified", Label(entries));
    }

    [Fact]
    public void A_pending_check_started_after_an_edit_is_unverified_until_its_own_exit_passes()
    {
        var started = Edit(1).Concat(Call(3, "start", "bash", DeclaredTest,
            content: "{\"execution_id\":\"bg-1\",\"state\":\"running\"}")).ToArray();

        Assert.Equal("unverified", Label(started));
        Assert.Equal("verified", Label(started.Concat(Poll(5, exit: 0))));
    }

    [Fact]
    public void A_check_started_before_an_edit_does_not_verify_the_edited_code_even_when_it_later_passes()
    {
        var started = Call(1, "start", "bash", DeclaredTest, content: "{\"execution_id\":\"bg-1\",\"state\":\"running\"}")
            .Concat(Edit(3)).ToArray();

        Assert.Equal("unverified", Label(started.Concat(Poll(5, exit: 0))));
        Assert.Equal("verified", Label(started.Concat(Poll(5, exit: 0)).Concat(Passing(7))));
    }

    [Fact]
    public void Computer_input_after_a_passing_check_does_not_change_the_label()
    {
        Assert.Equal("verified", Label(Passing(1).Concat(SideEffecting(3, "computer", "{\"action\":\"left_click\"}"))));
    }

    [Fact]
    public void Computer_input_after_a_source_edit_does_not_verify_the_edit()
    {
        Assert.Equal("unverified", Label(Edit(1).Concat(SideEffecting(3, "computer", "{\"action\":\"left_click\"}"))));
    }

    [Fact]
    public void Deleting_a_scratch_file_after_a_passing_check_keeps_the_label_verified()
    {
        Assert.Equal("verified", Label(Passing(1).Concat(Call(3, "cleanup", "file_delete", "{\"path\":\"scratch/probe.py\"}"))));
    }

    [Fact]
    public void A_later_read_only_lookup_does_not_change_a_verified_label()
    {
        Assert.Equal("verified", Label(Edit(1).Concat(Passing(3))
            .Concat(Call(5, "read", "read_file", "{\"path\":\"src/Widget.cs\"}"))));
    }

    [Fact]
    public void Unknown_program_effects_make_the_label_unverified()
    {
        Assert.Equal("unverified", Label(Call(1, "program", "run_code", "{}")));
    }

    private static string Label(IEnumerable<ModelContextEntry> entries) =>
        AgentRunSummary.FromTerminalRun(CompletedRun(), "Workspace",
            verificationEvidence: RunVerificationEvidence.Project(entries.ToArray())).VerificationState;

    private static AgentRun CompletedRun()
    {
        var timestamp = DateTimeOffset.UnixEpoch;
        return new AgentRun(new RunId("run_" + new string('1', 32)), new SessionId(new string('1', 32)),
            new WorkspaceId("ws_" + new string('1', 32)), new HarnessRunStatus(HarnessRunState.Completed),
            1, 1, 1, 0, 0, AgentPermissionPreset.Standard, 1, new string('a', 64), timestamp, timestamp, timestamp, null);
    }

    private static ModelContextEntry[] Call(long sequence, string id, string tool, string arguments,
        ToolExecutionOutcome outcome = ToolExecutionOutcome.Succeeded, string code = "ok", string content = "{}") =>
        VerificationCompletionTests.Batch(sequence, id, tool, arguments, outcome, code, content);

    private static ModelContextEntry[] Passing(long sequence) =>
        Call(sequence, "check-" + sequence, "bash", DeclaredTest, content: "{\"exit_code\":0}");

    private static ModelContextEntry[] Failing(long sequence) =>
        Call(sequence, "check-" + sequence, "bash", DeclaredTest, ToolExecutionOutcome.Failed,
            "process_exit_nonzero", "{\"exit_code\":1}");

    private static ModelContextEntry[] Edit(long sequence) =>
        Call(sequence, "edit-" + sequence, "write_file", SourceEdit);

    private static ModelContextEntry[] Negative(long sequence, int exit, int observed) =>
        Call(sequence, "negative-" + sequence, "run_process",
            "{\"executable\":\"python\",\"arguments\":[\"negative.py\"],\"purpose\":\"expected_failure\",\"expected_exit_code\":" + exit + "}",
            ToolExecutionOutcome.Failed, "process_exit_nonzero",
            "{\"exit_code\":" + observed + "}");

    private static ModelContextEntry[] Poll(long sequence, int exit) =>
        Call(sequence, "poll-" + sequence, "bash", "{\"mode\":\"read_output\",\"execution_id\":\"bg-1\"}",
            content: "{\"state\":\"exited\",\"exit_code\":" + exit + "}");

    private static ModelContextEntry[] SideEffecting(long sequence, string tool, string arguments)
    {
        using var json = JsonDocument.Parse(arguments);
        var call = new ModelToolCall("input-" + sequence, tool, "1.0.0", json.RootElement);
        var receipt = new ToolExecutionReceipt(new ExecutionId("exec_" + new string('a', 32)),
            new ToolCallId("call_" + new string('b', 32)), ToolExecutionOutcome.Succeeded, "ok", null,
            sideEffectsPossible: true, providerCallId: "input-" + sequence);
        return
        [
            ModelContextEntry.Assistant("assistant-input-" + sequence, sequence,
                new AssistantStep(null, [call], AssistantFinishReason.ToolCalls, 1)),
            ModelContextEntry.ToolResult("result-input-" + sequence, sequence + 1, receipt, "{}"),
        ];
    }
}
