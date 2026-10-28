using System.Text.Json;
using RuAgent.Harness.Core.Context;
using Xunit;

namespace RuAgent.Harness.Core.Tests.Prompting;

public sealed class VerificationInvocationRepairTests
{
    [Fact]
    public void Corrected_passing_inline_probe_resolves_only_the_invalid_invocation()
    {
        var entries = Failed(invalid: true).Concat(Passed());
        var snapshot = VerificationCompletionTests.Snapshot(entries);
        Assert.Null(snapshot.VerificationEvidence.BlockingCode);
        Assert.Equal(VerificationCheckStatus.Passed, Assert.Single(snapshot.VerificationEvidence.Checks).Status);
        Assert.Equal(ToolExecutionOutcome.Failed, snapshot.Entries[1].ToolReceipt!.Outcome);
        var changed = entries.Concat(VerificationCompletionTests.Batch(5, "edit", "edit_file", "{}", ToolExecutionOutcome.Succeeded, "ok", "{}"));
        Assert.Equal("verification_stale", VerificationCompletionTests.Snapshot(changed).VerificationEvidence.BlockingCode);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void Genuine_failed_checks_different_families_and_negative_mismatches_stay_failed(bool invalid, bool differentFamily, bool negative)
    {
        var snapshot = VerificationCompletionTests.Snapshot(Failed(invalid, negative).Concat(Passed(differentFamily)));
        Assert.NotNull(snapshot.VerificationEvidence.BlockingCode);
    }

    private static ModelContextEntry[] Failed(bool invalid, bool negative = false) => VerificationCompletionTests.Batch(
        1, "bad-inline", "run_process", Arguments("from a b import value", negative), ToolExecutionOutcome.Failed,
        "process_exit_nonzero", JsonSerializer.Serialize(new
        {
            exit_code = 1, verification_identity = new string('a', 64), verification_repair_identity = new string('b', 64),
            verification_invocation_invalid = invalid,
        }));

    private static ModelContextEntry[] Passed(bool differentFamily = false) => VerificationCompletionTests.Batch(
        3, "corrected-inline", "run_process", Arguments("import importlib; print('verified')", false), ToolExecutionOutcome.Succeeded,
        "ok", JsonSerializer.Serialize(new
        {
            exit_code = 0, verification_identity = new string('c', 64), verification_repair_identity = new string(differentFamily ? 'd' : 'b', 64),
        }));

    private static string Arguments(string script, bool negative) => JsonSerializer.Serialize(new
    {
        executable = "python", arguments = new[] { "-c", script }, working_directory = "C:/workspace",
        purpose = negative ? "expected_failure" : "verification", expected_exit_code = negative ? 7 : 0,
    });
}
