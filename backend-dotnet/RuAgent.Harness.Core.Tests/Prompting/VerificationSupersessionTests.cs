using System.Text.Json;
using RuAgent.Harness.Core.Context;
using Xunit;

namespace RuAgent.Harness.Core.Tests.Prompting;

public sealed class VerificationSupersessionTests
{
    [Fact]
    public void Outstanding_guidance_identifies_scope_and_the_action_for_each_check()
    {
        var evidence = Project(CheckedThenChanged().Concat(Check(5, "failed", "required.py", exit: 1)));
        var guidance = evidence.DescribePending()!;
        Assert.Contains("cwd:", guidance);
        Assert.Contains("action: rerun or explicitly supersede", guidance);
        Assert.Contains("action: repair and rerun this check", guidance);
        Assert.All(evidence.Checks, check => Assert.Contains(check.Identity, guidance));
    }

    [Fact]
    public void Pending_guidance_does_not_tell_the_model_to_launch_a_duplicate_process()
    {
        var guidance = Project(Check(1, "running", "required.py", state: "running")).DescribePending()!;
        Assert.Contains("action: read terminal bg-probe until exit", guidance);
    }

    [Fact]
    public void Cleanup_can_be_followed_by_an_explicit_passing_replacement_without_erasing_history()
    {
        var before = CheckedThenChanged();
        Assert.Equal("verification_stale", Project(before).BlockingCode);
        var evidence = Project(before.Concat(Check(5, "final", "smoke.py", supersedes: ["probe"])));
        Assert.Null(evidence.BlockingCode);
        Assert.Null(evidence.DescribePending());
        Assert.Equal(2, evidence.Checks.Count);
        Assert.Equal(VerificationCheckStatus.Stale, evidence.Checks[0].Status);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("other-run-id")]
    public void Unrelated_passing_checks_do_not_implicitly_replace_old_checks(string id)
    {
        Assert.Equal("verification_stale", Project(CheckedThenChanged()
            .Concat(Check(5, "final", "smoke.py", supersedes: [id]))).BlockingCode);
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(0, "running")]
    public void Failed_or_pending_checks_cannot_be_retired(int exit, string? state)
    {
        var entries = Check(1, "probe", "test_fix.py", exit: exit, state: state)
            .Concat(Change(3)).Concat(Check(5, "final", "smoke.py", supersedes: ["probe"]));
        Assert.NotNull(Project(entries).BlockingCode);
    }

    [Fact]
    public void An_expected_exit_mismatch_cannot_be_replaced()
    {
        var entries = Check(1, "probe", "negative.py", exit: 1, expected: 7)
            .Concat(Change(3)).Concat(Check(5, "final", "smoke.py", supersedes: ["probe"]));
        Assert.Equal("expected_exit_mismatch", Project(entries).BlockingCode);
    }

    [Fact]
    public void Replacement_must_itself_pass_in_the_same_authoritative_directory()
    {
        Assert.NotNull(Project(CheckedThenChanged().Concat(Check(5, "final", "smoke.py",
            supersedes: ["probe"], exit: 1))).BlockingCode);
        Assert.NotNull(Project(CheckedThenChanged().Concat(Check(5, "final", "smoke.py",
            supersedes: ["probe"], cwd: "C:/elsewhere"))).BlockingCode);
        Assert.NotNull(Project(CheckedThenChanged().Concat(Check(5, "final", "smoke.py",
            supersedes: ["probe"], cwd: null))).BlockingCode);
    }

    [Fact]
    public void Canonical_directory_case_and_trailing_separator_do_not_split_replacement_scope()
    {
        Assert.Null(Project(CheckedThenChanged().Concat(Check(5, "final", "smoke.py",
            supersedes: ["probe"], cwd: "c:/WORKSPACE/"))).BlockingCode);
    }

    [Fact]
    public void A_later_mutation_reopens_the_replaced_checks_until_the_replacement_is_rerun()
    {
        var entries = CheckedThenChanged().Concat(Check(5, "final", "smoke.py", supersedes: ["probe"]));
        Assert.Null(Project(entries).BlockingCode);
        var changed = entries.Concat(Change(7));
        Assert.NotNull(Project(changed).BlockingCode);
        Assert.Null(Project(changed.Concat(Check(9, "rerun", "smoke.py", supersedes: ["probe"]))).BlockingCode);
    }

    [Fact]
    public void Unknown_authoritative_freshness_of_the_replacement_does_not_clear_the_old_check()
    {
        var evidence = Project(CheckedThenChanged().Concat(Check(5, "final", "smoke.py", supersedes: ["probe"])));
        Assert.Null(evidence.BlockingCode);
        Assert.NotNull(evidence.ApplyWorkspaceFreshness(new Dictionary<DateTimeOffset, WorkspaceCheckFreshness>()).BlockingCode);
    }

    [Fact]
    public void A_replacement_rerun_keeps_its_declared_obligations_without_repeating_metadata()
    {
        var entries = CheckedThenChanged().Concat(Check(5, "final", "smoke.py", supersedes: ["probe"]))
            .Concat(Change(7)).Concat(Check(9, "rerun", "smoke.py"));
        Assert.Null(Project(entries).BlockingCode);
    }

    [Fact]
    public void Incremental_projection_and_full_replay_agree()
    {
        var before = CheckedThenChanged();
        var after = Check(5, "final", "smoke.py", supersedes: ["probe"]);
        var incremental = RunVerificationEvidence.Project(after, Project(before));
        Assert.Null(incremental.BlockingCode);
        Assert.Equal(Project(before.Concat(after)).BlockingCode, incremental.BlockingCode);
        Assert.NotNull(RunVerificationEvidence.Project(Change(7), incremental).BlockingCode);
    }

    [Fact]
    public void Reused_provider_call_ids_cannot_ambiguously_retire_multiple_checks()
    {
        var entries = Check(1, "reused", "test_fix.py").Concat(Check(3, "reused", "another_check.py"))
            .Concat(Change(5));
        var before = Project(entries);
        var ambiguous = Project(entries.Concat(Check(7, "final", "smoke.py", supersedes: ["reused"])));
        Assert.NotNull(ambiguous.BlockingCode);
        var ids = before.Checks.Select(check => check.Identity).ToArray();
        Assert.All(ids, id => Assert.Contains(id, before.DescribePending()));
        var explicitChecks = Project(entries.Concat(Check(7, "final", "smoke.py", supersedes: ids)));
        Assert.Null(explicitChecks.BlockingCode);
    }

    [Fact]
    public void Replacement_bindings_cannot_be_mutated_through_the_public_evidence_snapshot()
    {
        var evidence = Project(CheckedThenChanged().Concat(Check(5, "final", "smoke.py", supersedes: ["probe"])));
        var bindings = Assert.Single(evidence.Checks.Where(check => check.ProviderCallId == "final")).SupersededCheckIdentities!;
        var list = Assert.IsAssignableFrom<IList<string>>(bindings);
        Assert.Throws<NotSupportedException>(() => list[0] = "forged");
        Assert.Null(evidence.BlockingCode);
    }

    private static RunVerificationEvidence Project(IEnumerable<ModelContextEntry> entries) => RunVerificationEvidence.Project(entries);
    private static IEnumerable<ModelContextEntry> CheckedThenChanged() => Check(1, "probe", "test_fix.py").Concat(Change(3));
    private static ModelContextEntry[] Change(long sequence) => VerificationCompletionTests.Batch(sequence,
        "cleanup-" + sequence, "file_delete", "{\"path\":\"test_fix.py\"}", ToolExecutionOutcome.Succeeded, "ok", "{}");

    private static ModelContextEntry[] Check(long sequence, string id, string script, string[]? supersedes = null,
        int exit = 0, string? state = null, string? cwd = "C:/workspace", int expected = 0) => VerificationCompletionTests.Batch(
        sequence, id, "run_process", JsonSerializer.Serialize(new
        {
            executable = "python", arguments = new[] { script }, purpose = expected == 0 ? "verification" : "expected_failure",
            expected_exit_code = expected == 0 ? (int?)null : expected, supersedes,
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }),
        exit == 0 ? ToolExecutionOutcome.Succeeded : ToolExecutionOutcome.Failed,
        exit == 0 ? "ok" : "process_exit_nonzero", JsonSerializer.Serialize(new
        {
            exit_code = exit, state, execution_id = state == "running" ? "bg-probe" : null,
            verification_identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(script))),
            verification_cwd = cwd,
        }));
}
