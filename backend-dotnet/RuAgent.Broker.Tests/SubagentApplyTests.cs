using System.Text;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Subagents.Integration;
using RuAgent.Broker.Tools.Files;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class SubagentApplyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-apply-fixture-" + Guid.NewGuid().ToString("N"));
    public SubagentApplyTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Conflict_preflight_preserves_all_parent_files()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "before");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "user changed");
        var journal = new Journal();
        var result = await new SubagentApplyAdapter(journal).ApplyAsync(Lease(), Changes(), default);
        Assert.Equal(SubagentApplyStatus.Conflict, result.Status);
        Assert.Equal("before", File.ReadAllText(Path.Combine(_root, "a.txt")));
        Assert.Equal("user changed", File.ReadAllText(Path.Combine(_root, "b.txt")));
    }

    [Fact]
    public async Task Successful_apply_is_idempotent_and_does_not_overwrite_later_user_edit()
    {
        foreach (var path in new[] { "a.txt", "b.txt" }) File.WriteAllText(Path.Combine(_root, path), "before");
        var adapter = new SubagentApplyAdapter(new Journal());
        Assert.Equal(SubagentApplyStatus.Applied, (await adapter.ApplyAsync(Lease(), Changes(), default)).Status);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "new user edit");
        Assert.Equal(SubagentApplyStatus.Applied, (await adapter.ApplyAsync(Lease(), Changes(), default)).Status);
        Assert.Equal("new user edit", File.ReadAllText(Path.Combine(_root, "a.txt")));
    }

    [Fact]
    public async Task Pending_journal_after_crash_is_unknown_and_never_replayed()
    {
        var journal = new Journal { Outcome = new(SubagentApplyStatus.Pending, 1, "pending") };
        var outcome = await new SubagentApplyAdapter(journal).ApplyAsync(Lease(), Changes(), default);
        Assert.Equal(SubagentApplyStatus.Unknown, outcome.Status);
        Assert.False(File.Exists(Path.Combine(_root, "a.txt")));
    }

    private SubagentChangeSet Changes() => new("agent_one", "run_parent", _root, "snapshot_one", 1,
        new[] { "a.txt", "b.txt" }.Select(path => new SubagentFileChange(path, FileHashService.ComputeStringHash("before"),
            FileHashService.ComputeStringHash("after"), Encoding.UTF8.GetBytes("after"), 100644)).ToArray());

    private BrokerRunLease Lease()
    {
        var bound = BoundPolicySnapshot.Parse(EnvelopeTestSnapshots.StandardEmpty());
        return new("run_parent", "parent", bound.WorkspaceId, _root, bound.VolumeSerialNumber,
            bound.RootFileId, bound.DisplayIdentityHash, bound.Preset, bound.Version, bound.Sha256,
            DateTimeOffset.UtcNow.AddMinutes(15), [], bound, 1);
    }

    private sealed class Journal : ISubagentApplyJournal
    {
        public SubagentApplyOutcome? Outcome { get; set; }
        public Task<SubagentApplyOutcome?> BeginAsync(SubagentChangeSet changes, string fingerprint, CancellationToken ct)
        {
            var previous = Outcome;
            Outcome ??= new(SubagentApplyStatus.Pending, 0, "pending");
            return Task.FromResult(previous);
        }
        public Task RecordFileIntentAsync(SubagentChangeSet changes, int fileIndex, CancellationToken ct) => Task.CompletedTask;
        public Task RecordFileReceiptAsync(SubagentChangeSet changes, int fileIndex, CancellationToken ct) => Task.CompletedTask;
        public Task FinishAsync(SubagentChangeSet changes, SubagentApplyOutcome outcome, CancellationToken ct)
        { Outcome = outcome; return Task.CompletedTask; }
    }

    public void Dispose() => Directory.Delete(_root, true);
}
