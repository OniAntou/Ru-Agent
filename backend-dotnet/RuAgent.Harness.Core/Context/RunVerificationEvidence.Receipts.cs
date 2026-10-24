using System.Text.Json;

namespace RuAgent.Harness.Core.Context;

public sealed partial class RunVerificationEvidence
{
    private static VerificationCheckEvidence Assess(VerificationCheckEvidence check,
        ToolExecutionReceipt receipt, string? content, string providerId, bool killed)
    {
        var failed = check with { ProviderCallId = providerId, Status = VerificationCheckStatus.Failed,
            Code = killed ? "verification_cancelled" : receipt.Code };
        if (killed || receipt.Outcome is ToolExecutionOutcome.Cancelled or ToolExecutionOutcome.TimedOut
            or ToolExecutionOutcome.OutcomeUnknown) return failed;
        try
        {
            using var json = JsonDocument.Parse(content ?? "{}");
            var output = json.RootElement;
            if (output.ValueKind != JsonValueKind.Object) return failed with { Code = "verification_result_missing" };
            string? state = StringArg(output, "state");
            string? execution = StringArg(output, "execution_id") ?? check.ExecutionId;
            if (receipt.Outcome == ToolExecutionOutcome.Succeeded
                && (state == "running" || output.TryGetProperty("background", out var background)
                    && background.ValueKind == JsonValueKind.True && state is null))
                return check with { ProviderCallId = providerId, ExecutionId = execution,
                    Status = VerificationCheckStatus.Pending, Code = "verification_pending" };
            if (state is "killed" or "expired") return failed with { Code = "verification_cancelled" };
            bool normalExit = receipt.Outcome == ToolExecutionOutcome.Succeeded && receipt.Code == "ok"
                || receipt.Outcome == ToolExecutionOutcome.Failed && receipt.Code == "process_exit_nonzero";
            if (!normalExit) return failed;
            if (!output.TryGetProperty("exit_code", out var value) || value.ValueKind != JsonValueKind.Number
                || !value.TryGetInt32(out var exitCode))
                return receipt.Outcome == ToolExecutionOutcome.Failed ? failed
                    : failed with { Code = "verification_result_missing" };
            if (receipt.Outcome == ToolExecutionOutcome.Failed && exitCode == 0)
                return failed with { Code = "verification_result_missing" };
            return check with { ProviderCallId = providerId, ExecutionId = execution,
                Status = exitCode == check.ExpectedExitCode ? VerificationCheckStatus.Passed : VerificationCheckStatus.Failed,
                Code = exitCode == check.ExpectedExitCode ? "ok"
                    : check.ExpectedExitCode != 0 ? "expected_exit_mismatch" : "process_exit_nonzero" };
        }
        catch (JsonException) { return failed with { Code = "verification_result_missing" }; }
    }
}
