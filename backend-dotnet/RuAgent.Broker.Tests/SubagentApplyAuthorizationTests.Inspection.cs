using RuAgent.Broker.Subagents.Integration;
using RuAgent.Broker.Tools.Files;
using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed partial class SubagentApplyAuthorizationTests
{
    [Theory]
    [InlineData("before", "base")]
    [InlineData("after", "proposed")]
    [InlineData("later user edit", "diverged")]
    public async Task Inspection_observes_exact_frozen_intent_without_fabricating_receipts(string current, string match)
    {
        await Open(EnvelopeTestSnapshots.StandardEmpty());
        var changes = Changes();
        _session.TryGetRunLease("run_parent", out var lease);
        var preparation = SubagentApplyPreparation.Create(lease!, changes);
        var pending = new SubagentApplyJournalState(preparation.Fingerprint,
            new(SubagentApplyStatus.Pending, 0, "pending"),
            [new(0, changes.Files[0].RelativePath, changes.Files[0].BaseHash, changes.Files[0].ProposedHash, 100644, false)]);
        File.WriteAllText(Path.Combine(_root, "a.txt"), current);
        var observed = await _runtime.InspectSubagentApplyAsync("parent", "run_parent", changes, pending, default);
        Assert.Equal(preparation.Fingerprint, observed.Fingerprint);
        Assert.Equal(match, observed.Files[0].Match);
        Assert.Equal(FileHashService.ComputeStringHash(current), observed.Files[0].CurrentHash);
        Assert.False(observed.Files[0].Receipted);
        Assert.Equal("base", observed.Files[1].Match);
        Assert.False(observed.Files[1].IntentRecorded);
        Assert.Equal(SubagentApplyStatus.Pending, pending.Outcome.Status);
        Assert.Equal(0, _journal.Receipts);
        Assert.Equal(current, File.ReadAllText(Path.Combine(_root, "a.txt")));
        var changed = pending with { Fingerprint = new string('f', 64) };
        var rejected = await Assert.ThrowsAsync<AgentProtocolException>(() =>
            _runtime.InspectSubagentApplyAsync("parent", "run_parent", changes, changed, default));
        Assert.Equal("subagent_apply_journal_invalid", rejected.Code);
    }

    [Fact]
    public async Task Inspection_cannot_claim_absence_when_parent_directory_cannot_be_opened()
    {
        await Open(EnvelopeTestSnapshots.StandardEmpty());
        var changes = Changes();
        changes = changes with { Files = [changes.Files[0] with { RelativePath = "not-created/new.txt", BaseHash = FileHashService.Missing }] };
        _session.TryGetRunLease("run_parent", out var lease);
        var pending = new SubagentApplyJournalState(SubagentApplyPreparation.Create(lease!, changes).Fingerprint,
            new(SubagentApplyStatus.Pending, 0, "pending"), []);
        var result = await _runtime.InspectSubagentApplyAsync("parent", "run_parent", changes, pending, default);
        var file = Assert.Single(result.Files);
        Assert.Null(file.CurrentHash);
        Assert.Equal("unavailable", file.Match);
        Assert.False(Directory.Exists(Path.Combine(_root, "not-created")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inspection_bounds_current_parent_targets_without_mutating_them(bool directory)
    {
        await Open(EnvelopeTestSnapshots.StandardEmpty());
        var changes = Changes();
        _session.TryGetRunLease("run_parent", out var lease);
        var pending = new SubagentApplyJournalState(SubagentApplyPreparation.Create(lease!, changes).Fingerprint,
            new(SubagentApplyStatus.Unknown, 0, "unknown"), []);
        var path = Path.Combine(_root, "a.txt");
        File.Delete(path);
        if (directory) Directory.CreateDirectory(path);
        else
        {
            using var file = File.Create(path);
            file.SetLength(FileMutationGuard.MaxRollbackBytes + 1L);
        }
        var error = await Assert.ThrowsAsync<AgentProtocolException>(() =>
            _runtime.InspectSubagentApplyAsync("parent", "run_parent", changes, pending, default));
        Assert.Equal(directory ? "subagent_inspection_target_invalid" : "subagent_inspection_too_large", error.Code);
        Assert.Equal(0, _journal.Receipts);
        if (directory) Assert.True(Directory.Exists(path));
        else Assert.Equal(FileMutationGuard.MaxRollbackBytes + 1L, new FileInfo(path).Length);
    }

    [Fact]
    public async Task Inspection_preserves_receipts_when_crash_preceded_aggregate_finish()
    {
        await Open(EnvelopeTestSnapshots.StandardEmpty());
        var changes = Changes();
        _session.TryGetRunLease("run_parent", out var lease);
        var pending = new SubagentApplyJournalState(SubagentApplyPreparation.Create(lease!, changes).Fingerprint,
            new(SubagentApplyStatus.Pending, 0, "pending"),
            [new(0, "a.txt", changes.Files[0].BaseHash, changes.Files[0].ProposedHash, 100644, true)]);
        var result = await _runtime.InspectSubagentApplyAsync("parent", "run_parent", changes, pending, default);
        Assert.True(result.Files[0].Receipted);
        Assert.Equal("base", result.Files[0].Match);
        Assert.Equal(0, pending.Outcome.AppliedFiles);
    }

    [Theory]
    [InlineData("ask")]
    [InlineData("deny")]
    public async Task Inspection_does_not_bypass_read_policy(string effect)
    {
        await Open(EnvelopeTestSnapshots.StandardWithRules(PolicyEvalSnapshots.Rule("read-rule", effect,
            PolicyEvalSnapshots.Matcher(toolId: "read"))));
        var changes = Changes();
        _session.TryGetRunLease("run_parent", out var lease);
        var pending = new SubagentApplyJournalState(SubagentApplyPreparation.Create(lease!, changes).Fingerprint,
            new(SubagentApplyStatus.Unknown, 0, "unknown"), []);
        var error = await Assert.ThrowsAsync<AgentProtocolException>(() =>
            _runtime.InspectSubagentApplyAsync("parent", "run_parent", changes, pending, default));
        Assert.Equal("subagent_inspection_read_denied", error.Code);
        Assert.Equal("before", File.ReadAllText(Path.Combine(_root, "a.txt")));
    }

    [Fact]
    public async Task Inspection_rejects_foreign_or_rewritten_intents_and_cancelled_authority()
    {
        await Open(EnvelopeTestSnapshots.StandardEmpty());
        var changes = Changes();
        _session.TryGetRunLease("run_parent", out var lease);
        var preparation = SubagentApplyPreparation.Create(lease!, changes);
        var pending = new SubagentApplyJournalState(preparation.Fingerprint,
            new(SubagentApplyStatus.Unknown, 0, "subagent_apply_reconciliation_required"),
            [new(0, "other.txt", changes.Files[0].BaseHash, changes.Files[0].ProposedHash, 100644, false)]);
        await Assert.ThrowsAsync<AgentProtocolException>(() =>
            _runtime.InspectSubagentApplyAsync("parent", "run_parent", changes, pending, default));
        await Assert.ThrowsAsync<AgentProtocolException>(() =>
            _runtime.InspectSubagentApplyAsync("other_session", "run_parent", changes, pending, default));
        _runtime.CancelRun("run_parent");
        await Assert.ThrowsAsync<AgentProtocolException>(() =>
            _runtime.InspectSubagentApplyAsync("parent", "run_parent", changes, pending, default));
    }
}
