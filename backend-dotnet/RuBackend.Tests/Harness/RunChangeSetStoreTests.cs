using RuBackend.Harness.Workspace;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class RunChangeSetStoreTests
{
    [Fact]
    public void Initial_observed_hash_recorded_and_retrievable()
    {
        var store = new RunChangeSetStore();
        var runId = "run_" + new string('a', 32);
        var initial = new Dictionary<string, string>
        {
            ["a.txt"] = new string('a', 64),
            ["b.txt"] = new string('b', 64),
        };
        store.InitializeRun(runId, initial);
        var changeSet = store.GetChangeSet(runId);
        Assert.Empty(changeSet);

        // Initial hashes are not considered mutations until RecordMutation
        Assert.False(store.Contains(runId, "a.txt"));
    }

    [Fact]
    public void Mutation_records_before_after_hashes_and_initial()
    {
        var store = new RunChangeSetStore();
        var runId = "run_" + new string('b', 32);
        var initialHash = new string('c', 64);
        store.InitializeRun(runId, new Dictionary<string, string> { ["a.txt"] = initialHash });

        var before = new string('d', 64);
        var after = new string('e', 64);
        store.RecordMutation(runId, "a.txt", before, after);

        var entry = Assert.Single(store.GetChangeSet(runId));
        Assert.Equal("a.txt", entry.Path);
        Assert.Equal(initialHash, entry.InitialHash);
        Assert.Equal(before, entry.BeforeHash);
        Assert.Equal(after, entry.AfterHash);
    }

    [Fact]
    public void Mutation_without_prior_initial_uses_before_as_initial()
    {
        var store = new RunChangeSetStore();
        var runId = "run_" + new string('c', 32);
        var before = new string('a', 64);
        var after = new string('b', 64);
        store.RecordMutation(runId, "new.txt", before, after);
        var entry = Assert.Single(store.GetChangeSet(runId));
        Assert.Equal(before, entry.InitialHash);
    }

    [Fact]
    public void Final_change_set_includes_only_paths_changed_by_that_run()
    {
        var store = new RunChangeSetStore();
        var run1 = "run_" + new string('1', 32);
        var run2 = "run_" + new string('2', 32);

        store.InitializeRun(run1, new Dictionary<string, string> { ["shared.txt"] = new string('a', 64) });
        store.InitializeRun(run2, new Dictionary<string, string> { ["shared.txt"] = new string('a', 64) });

        var before = new string('a', 64);
        var after1 = new string('b', 64);
        var after2 = new string('c', 64);

        store.RecordMutation(run1, "a.txt", before, after1);
        store.RecordMutation(run2, "b.txt", before, after2);

        var set1 = store.GetChangeSet(run1);
        Assert.Single(set1);
        Assert.Equal("a.txt", set1[0].Path);
        Assert.DoesNotContain(set1, e => e.Path == "b.txt");

        var set2 = store.GetChangeSet(run2);
        Assert.Single(set2);
        Assert.Equal("b.txt", set2[0].Path);
    }

    [Fact]
    public void Unrelated_dirty_files_remain_absent()
    {
        var store = new RunChangeSetStore();
        var runId = "run_" + new string('d', 32);
        // Simulate unrelated dirty file observed initially but never mutated by run
        store.InitializeRun(runId, new Dictionary<string, string>
        {
            ["unrelated.txt"] = new string('f', 64),
            ["target.txt"] = new string('a', 64),
        });

        store.RecordMutation(runId, "target.txt", new string('a', 64), new string('b', 64));

        var changed = store.GetChangedPaths(runId);
        Assert.Contains("target.txt", changed);
        Assert.DoesNotContain("unrelated.txt", changed);

        var all = store.GetChangeSet(runId);
        Assert.DoesNotContain(all, e => e.Path == "unrelated.txt");
    }

    [Fact]
    public void ObserveInitial_and_multiple_mutations_track_correctly()
    {
        var store = new RunChangeSetStore();
        var runId = "run_" + new string('e', 32);
        store.ObserveInitial(runId, "obs.txt", new string('a', 64));
        store.RecordMutation(runId, "obs.txt", new string('a', 64), new string('b', 64));
        store.RecordMutation(runId, "obs.txt", new string('b', 64), new string('c', 64));

        var entry = Assert.Single(store.GetChangeSet(runId));
        Assert.Equal("obs.txt", entry.Path);
        Assert.Equal(new string('a', 64), entry.InitialHash);
        Assert.Equal(new string('b', 64), entry.BeforeHash);
        Assert.Equal(new string('c', 64), entry.AfterHash);
    }

    [Fact]
    public void Different_runs_isolated()
    {
        var store = new RunChangeSetStore();
        var runA = "run_" + new string('a', 32);
        var runB = "run_" + new string('b', 32);
        store.RecordMutation(runA, "a.txt", new string('a', 64), new string('b', 64));
        store.RecordMutation(runB, "a.txt", new string('c', 64), new string('d', 64));

        var aSet = store.GetChangeSet(runA);
        Assert.Single(aSet);
        Assert.Equal(new string('b', 64), aSet[0].AfterHash);

        var bSet = store.GetChangeSet(runB);
        Assert.Single(bSet);
        Assert.Equal(new string('d', 64), bSet[0].AfterHash);
    }

    [Fact]
    public void Missing_hash_handling_for_create()
    {
        var store = new RunChangeSetStore();
        var runId = "run_" + new string('f', 32);
        store.InitializeRun(runId, new Dictionary<string, string> { ["new.txt"] = "missing" });
        store.RecordMutation(runId, "new.txt", "missing", new string('a', 64));
        var entry = Assert.Single(store.GetChangeSet(runId));
        Assert.Equal("missing", entry.BeforeHash);
        Assert.Equal("missing", entry.InitialHash);
    }
}
