using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using Xunit;

namespace RuAgent.Harness.Core.Tests.Prompting;

public sealed class FailedToolCompletionGuardTests
{
    [Fact]
    public void Repetition_advice_does_not_turn_failed_verification_into_success()
    {
        var entries = CreateContext(Receipt(ToolExecutionOutcome.Failed, "process_exit_nonzero")).Entries;
        var note = ModelContextEntry.Text("repetition-advice", entries[^1].Sequence + 1,
            ModelContextRole.System, ConsecutiveToolCallGuard.BuildRepairNote("process_exit_nonzero"));
        var context = new ModelContextSnapshot([.. entries, note], "[]"u8.ToArray());
        Assert.True(FailedToolCompletionGuard.TryGetFailure(context, out var code));
        Assert.Equal("process_exit_nonzero", code);
    }

    [Theory]
    [InlineData("edit_no_match", "re-read the same file")]
    [InlineData("edit_not_unique", "expected_occurrences")]
    [InlineData("workspace_changed", "reconcile the change")]
    [InlineData("process_exit_nonzero", "verification diagnostics")]
    [InlineData("approval_denied", "report the blocker")]
    public void Unverified_recovery_guidance_is_specific_and_does_not_bypass_policy(string code, string expected)
    {
        var note = AgentRecoveryNotes.BuildUnverifiedNote(code);

        Assert.True(note.Contains(expected, StringComparison.OrdinalIgnoreCase), note);
        Assert.DoesNotContain("use a different tool", note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Trailing_failed_receipt_returns_failure_code()
    {
        var context = CreateContext(
            Receipt(ToolExecutionOutcome.Failed, "workspace_path_outside_root"));

        Assert.True(FailedToolCompletionGuard.TryGetFailure(context, out var code));
        Assert.Equal("workspace_path_outside_root", code);
    }

    [Fact]
    public void Trailing_successful_receipt_returns_false()
    {
        var context = CreateContext(
            Receipt(ToolExecutionOutcome.Succeeded, "ok"));

        Assert.False(FailedToolCompletionGuard.TryGetFailure(context, out _));
    }

    [Fact]
    public void Mixed_trailing_receipts_returns_first_failure_in_model_order()
    {
        var context = CreateContext(
            Receipt(ToolExecutionOutcome.Succeeded, "ok"),
            Receipt(ToolExecutionOutcome.Failed, "workspace_path_outside_root"),
            Receipt(ToolExecutionOutcome.Failed, "another_failure"));

        Assert.True(FailedToolCompletionGuard.TryGetFailure(context, out var code));
        Assert.Equal("workspace_path_outside_root", code);
    }

    [Fact]
    public void All_successful_trailing_receipts_returns_false()
    {
        var context = CreateContext(
            Receipt(ToolExecutionOutcome.Succeeded, "ok"),
            Receipt(ToolExecutionOutcome.Succeeded, "ok"));

        Assert.False(FailedToolCompletionGuard.TryGetFailure(context, out _));
    }

    [Fact]
    public void Non_tool_trailing_entry_returns_false_even_if_prior_tool_failed()
    {
        var failedTool = ModelContextEntry.ToolResult(
            "tool-1",
            2,
            Receipt(ToolExecutionOutcome.Failed, "workspace_path_outside_root"),
            "{}");
        var userText = ModelContextEntry.Text("user-1", 3, ModelContextRole.User, "proceed");
        var context = new ModelContextSnapshot([failedTool, userText], "[]"u8.ToArray());

        Assert.False(FailedToolCompletionGuard.TryGetFailure(context, out _));
    }

    [Fact]
    public void Missing_receipt_falls_back_to_tool_failed()
    {
        var entryWithoutReceipt = CreateToolResultEntry(null);
        var context = new ModelContextSnapshot([entryWithoutReceipt], "[]"u8.ToArray());

        Assert.True(FailedToolCompletionGuard.TryGetFailure(context, out var code));
        Assert.Equal("tool_failed", code);
    }

    [Fact]
    public void Blank_receipt_code_falls_back_to_tool_failed()
    {
        var blankReceipt = CreateReceiptWithCode(ToolExecutionOutcome.Failed, " ");
        var entry = CreateToolResultEntry(blankReceipt);
        var context = new ModelContextSnapshot([entry], "[]"u8.ToArray());

        Assert.True(FailedToolCompletionGuard.TryGetFailure(context, out var code));
        Assert.Equal("tool_failed", code);
    }

    [Fact]
    public void Empty_context_returns_false()
    {
        var context = new ModelContextSnapshot([], "[]"u8.ToArray());

        Assert.False(FailedToolCompletionGuard.TryGetFailure(context, out _));
    }

    [Fact]
    public void First_policy_deny_returns_false_so_model_can_adjust()
    {
        var context = CreateContext(
            Receipt(ToolExecutionOutcome.Failed, "policy_denied"));

        Assert.False(FailedToolCompletionGuard.TryGetFailure(context, out _));
    }

    [Fact]
    public void Repeated_policy_deny_returns_failure()
    {
        var first = ModelContextEntry.ToolResult(
            "tool-1",
            2,
            Receipt(ToolExecutionOutcome.Failed, "policy_denied"),
            "{}");
        var retryAssistant = ModelContextEntry.Assistant(
            "assistant-2",
            3,
            new AssistantStep(null, [Call("files.read", "{}")], AssistantFinishReason.ToolCalls, 1));
        var second = ModelContextEntry.ToolResult(
            "tool-2",
            4,
            Receipt(ToolExecutionOutcome.Failed, "policy_denied"),
            "{}");
        var context = new ModelContextSnapshot([first, retryAssistant, second], "[]"u8.ToArray());

        Assert.True(FailedToolCompletionGuard.TryGetFailure(context, out var code));
        Assert.Equal("policy_denied", code);
    }

    [Fact]
    public void Policy_deny_mixed_with_other_failure_returns_first_failure()
    {
        var context = CreateContext(
            Receipt(ToolExecutionOutcome.Failed, "policy_denied"),
            Receipt(ToolExecutionOutcome.Failed, "transport_failure"));

        Assert.True(FailedToolCompletionGuard.TryGetFailure(context, out var code));
        Assert.Equal("policy_denied", code);
    }

    [Fact]
    public void Null_context_throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => FailedToolCompletionGuard.TryGetFailure(null!, out _));
    }

    [Fact]
    public void Prior_failed_tool_followed_by_successful_recovery_tool_returns_false()
    {
        var failedTool = ModelContextEntry.ToolResult(
            "tool-1",
            2,
            Receipt(ToolExecutionOutcome.Failed, "workspace_path_outside_root"),
            "{}");
        var recoveryAssistant = ModelContextEntry.Assistant(
            "assistant-2",
            3,
            new AssistantStep(null, [Call("files.read", "{}")], AssistantFinishReason.ToolCalls, 1));
        var successfulTool = ModelContextEntry.ToolResult(
            "tool-2",
            4,
            Receipt(ToolExecutionOutcome.Succeeded, "ok"),
            "{}");
        var context = new ModelContextSnapshot([failedTool, recoveryAssistant, successfulTool], "[]"u8.ToArray());

        Assert.False(FailedToolCompletionGuard.TryGetFailure(context, out _));
    }

    [Fact]
    public void Trailing_recovery_notes_do_not_hide_the_failed_receipt()
    {
        var failedTool = ModelContextEntry.ToolResult(
            "tool-1",
            2,
            Receipt(ToolExecutionOutcome.Failed, "workspace_path_outside_root"),
            "{}");
        var firstNote = ModelContextEntry.Text(
            "note-1",
            3,
            ModelContextRole.System,
            AgentRecoveryNotes.BuildUnverifiedNote("workspace_path_outside_root"));
        var secondNote = ModelContextEntry.Text(
            "note-2",
            4,
            ModelContextRole.System,
            AgentRecoveryNotes.BuildUnverifiedNote("workspace_path_outside_root"));
        var context = new ModelContextSnapshot([failedTool, firstNote, secondNote], "[]"u8.ToArray());

        Assert.True(FailedToolCompletionGuard.TryGetFailure(context, out var code));
        Assert.Equal("workspace_path_outside_root", code);
    }

    [Fact]
    public void Non_recovery_system_text_still_hides_the_prior_failure()
    {
        var failedTool = ModelContextEntry.ToolResult(
            "tool-1",
            2,
            Receipt(ToolExecutionOutcome.Failed, "workspace_path_outside_root"),
            "{}");
        var systemText = ModelContextEntry.Text(
            "system-1",
            3,
            ModelContextRole.System,
            "plain system instruction");
        var context = new ModelContextSnapshot([failedTool, systemText], "[]"u8.ToArray());

        Assert.False(FailedToolCompletionGuard.TryGetFailure(context, out _));
    }

    private static ModelContextSnapshot CreateContext(params ToolExecutionReceipt[] receipts)
    {
        var entries = new List<ModelContextEntry>
        {
            ModelContextEntry.Assistant(
                "assistant-prior",
                1,
                new AssistantStep(null, [Call("files.read", "{}")], AssistantFinishReason.ToolCalls, 1)),
        };

        for (var i = 0; i < receipts.Length; i++)
        {
            entries.Add(ModelContextEntry.ToolResult(
                $"tool-{i + 1}",
                i + 2,
                receipts[i],
                "{}"));
        }

        return new ModelContextSnapshot(entries, "[]"u8.ToArray());
    }

    private static ModelContextEntry CreateToolResultEntry(ToolExecutionReceipt? receipt)
    {
        var ctor = typeof(ModelContextEntry).GetConstructors(
            BindingFlags.NonPublic | BindingFlags.Instance)[0];
        return (ModelContextEntry)ctor.Invoke([
            "tool-1",
            1L,
            ModelContextRole.Tool,
            ModelContextEntryKind.ToolResult,
            "{}",
            null,
            receipt,
            null]);
    }

    private static ToolExecutionReceipt CreateReceiptWithCode(ToolExecutionOutcome outcome, string code)
    {
        var receipt = (ToolExecutionReceipt)RuntimeHelpers.GetUninitializedObject(typeof(ToolExecutionReceipt));
        typeof(ToolExecutionReceipt).GetField($"<{nameof(ToolExecutionReceipt.Outcome)}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(receipt, outcome);
        typeof(ToolExecutionReceipt).GetField($"<{nameof(ToolExecutionReceipt.Code)}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(receipt, code);
        return receipt;
    }

    private static ToolExecutionReceipt Receipt(ToolExecutionOutcome outcome, string code) =>
        new(
            new ExecutionId("exec_11111111111111111111111111111111"),
            new ToolCallId("call_11111111111111111111111111111111"),
            outcome,
            code,
            null,
            false,
            providerCallId: "provider_call");

    private static ModelToolCall Call(string toolId, string arguments)
    {
        using var document = JsonDocument.Parse(arguments);
        return new ModelToolCall("provider_call", toolId, "1.0.0", document.RootElement);
    }
}
