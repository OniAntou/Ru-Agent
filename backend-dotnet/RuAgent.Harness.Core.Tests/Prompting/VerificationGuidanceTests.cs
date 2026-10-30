using System.Text.Json;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using Xunit;

namespace RuAgent.Harness.Core.Tests.Prompting;

public sealed class VerificationGuidanceTests
{
    private static readonly string DigestA = new('A', 64);

    [Fact]
    public void Outstanding_guidance_names_each_check_its_directory_and_its_repair_action_without_retirement_language()
    {
        var content = JsonSerializer.Serialize(new { exit_code = 1, verification_identity = DigestA, verification_cwd = "C:/workspace" });
        var guidance = Snapshot(VerificationCompletionTests.Batch(1, "probe", "run_process",
            "{\"executable\":\"python\",\"arguments\":[\"required.py\"],\"purpose\":\"verification\"}",
            ToolExecutionOutcome.Failed, "process_exit_nonzero", content)).VerificationEvidence.DescribePending()!;

        Assert.Contains("[cwd: C:/workspace]", guidance, StringComparison.Ordinal);
        Assert.Contains("action: repair and rerun this check", guidance, StringComparison.Ordinal);
        Assert.DoesNotContain("supersede", guidance, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stale", guidance, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("check ID", guidance, StringComparison.Ordinal);
    }

    [Fact]
    public void Pending_guidance_tells_the_model_to_read_the_running_check_instead_of_starting_a_duplicate()
    {
        var guidance = Snapshot(VerificationCompletionTests.Batch(1, "probe", "run_process",
            "{\"executable\":\"python\",\"arguments\":[\"probe.py\"],\"purpose\":\"verification\"}",
            ToolExecutionOutcome.Succeeded, "ok", "{\"execution_id\":\"bg-probe\",\"state\":\"running\"}"))
            .VerificationEvidence.DescribePending()!;

        Assert.Contains("action: read terminal bg-probe until exit", guidance, StringComparison.Ordinal);
        Assert.DoesNotContain("supersede", guidance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Expected_exit_mismatch_guidance_reports_the_observed_exit_and_forbids_forcing_the_code()
    {
        var guidance = Snapshot(VerificationCompletionTests.Batch(1, "negative", "run_process",
            "{\"executable\":\"python\",\"arguments\":[\"negative.py\"],\"purpose\":\"expected_failure\",\"expected_exit_code\":7}",
            ToolExecutionOutcome.Failed, "process_exit_nonzero", "{\"exit_code\":1}")).VerificationEvidence.DescribePending()!;

        Assert.Contains("(expected_exit_mismatch)", guidance, StringComparison.Ordinal);
        Assert.Contains("report the observed exit mismatch; do not force the expected code", guidance, StringComparison.Ordinal);
        Assert.Contains("Do not keep rerunning a deliberately failing command", guidance, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_facing_execution_contract_no_longer_documents_supersession()
    {
        var block = RuAgentPromptBlocks.ExecutionContractBlock();

        Assert.DoesNotContain("supersedes", block, StringComparison.Ordinal);
        Assert.DoesNotContain("retired", block, StringComparison.Ordinal);
        Assert.Contains("purpose=verification", block, StringComparison.Ordinal);
        Assert.Contains("failed checks remain required until the same check passes", block, StringComparison.Ordinal);
        Assert.Contains("After later edits, rerun the relevant check before finishing.", block, StringComparison.Ordinal);
    }

    private static ModelContextSnapshot Snapshot(IEnumerable<ModelContextEntry> entries) =>
        VerificationCompletionTests.Snapshot(entries);
}
