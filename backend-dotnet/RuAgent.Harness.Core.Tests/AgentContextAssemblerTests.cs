using System.Text.Json;
using RuAgent.Harness.Core.Context;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class AgentContextAssemblerTests
{
    private static readonly RunId RunId =
        new("run_11111111111111111111111111111111");

    [Fact]
    public async Task Session_base_precedes_run_local_and_each_partition_uses_durable_sequence()
    {
        var source = new StubContextSource(
            new[]
            {
                ModelContextEntry.Text("session-2", 2, ModelContextRole.User, "second"),
                ModelContextEntry.Text("session-1", 1, ModelContextRole.System, "first"),
            },
            new[]
            {
                ToolResultEntry("run-4", 4),
                AssistantEntry("run-3", 3),
            });
        var assembler = new AgentContextAssembler(source);

        var snapshot = await assembler.AssembleAsync(RunId, CancellationToken.None);

        Assert.Equal(
            new[] { "session-1", "session-2", "run-3", "run-4" },
            snapshot.Entries.Select(entry => entry.SourceId));
        Assert.Equal(4, snapshot.Entries.Count);
        Assert.Equal(ModelContextEntryKind.AssistantStep, snapshot.Entries[2].Kind);
        Assert.Equal(2, snapshot.Entries[2].AssistantStep!.ToolCalls.Count);
        Assert.Equal(ModelContextEntryKind.ToolResult, snapshot.Entries[3].Kind);
        Assert.Equal(ToolExecutionOutcome.Succeeded, snapshot.Entries[3].ToolReceipt!.Outcome);
    }

    [Fact]
    public async Task Restart_with_equivalent_durable_entries_produces_byte_equivalent_input()
    {
        var first = await new AgentContextAssembler(NewEquivalentSource())
            .AssembleAsync(RunId, CancellationToken.None);
        var restarted = await new AgentContextAssembler(NewEquivalentSource())
            .AssembleAsync(RunId, CancellationToken.None);

        Assert.Equal(first.CanonicalUtf8.ToArray(), restarted.CanonicalUtf8.ToArray());
        Assert.Equal(
            first.Entries.Select(entry => entry.SourceId),
            restarted.Entries.Select(entry => entry.SourceId));
    }

    [Fact]
    public async Task Duplicate_source_id_across_partitions_fails_closed()
    {
        var source = new StubContextSource(
            new[] { ModelContextEntry.Text("duplicate", 1, ModelContextRole.User, "a") },
            new[] { ModelContextEntry.Text("duplicate", 2, ModelContextRole.User, "b") });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AgentContextAssembler(source).AssembleAsync(RunId, CancellationToken.None));

        Assert.Equal("duplicate_context_source_id", error.Message);
    }

    [Fact]
    public async Task Duplicate_durable_sequence_in_one_partition_fails_closed()
    {
        var source = new StubContextSource(
            Array.Empty<ModelContextEntry>(),
            new[]
            {
                ModelContextEntry.Text("run-a", 1, ModelContextRole.User, "a"),
                ModelContextEntry.Text("run-b", 1, ModelContextRole.User, "b"),
            });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AgentContextAssembler(source).AssembleAsync(RunId, CancellationToken.None));

        Assert.Equal("duplicate_context_sequence", error.Message);
    }

    [Fact]
    public async Task Snapshot_is_immutable_and_no_durable_entry_is_silently_dropped()
    {
        var session = new List<ModelContextEntry>
        {
            ModelContextEntry.Text("session-1", 1, ModelContextRole.System, "system"),
        };
        var run = new List<ModelContextEntry>
        {
            AssistantEntry("run-2", 2),
            ToolResultEntry("run-3", 3),
        };
        var sourceSnapshot = new ModelContextSourceSnapshot(session, run);
        session.Clear();
        run.Clear();

        var snapshot = await new AgentContextAssembler(
                new FixedSnapshotSource(sourceSnapshot))
            .AssembleAsync(RunId, CancellationToken.None);

        Assert.Equal(3, snapshot.Entries.Count);
        Assert.Equal(
            new[] { "session-1", "run-2", "run-3" },
            snapshot.Entries.Select(entry => entry.SourceId));
        Assert.False(snapshot.Entries is ModelContextEntry[]);
        Assert.DoesNotContain(
            typeof(AgentContextAssembler).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name is "RuApp" or "RuAgent.Protocol");
    }

    private static StubContextSource NewEquivalentSource() =>
        new(
            new[]
            {
                ModelContextEntry.Text("session-1", 1, ModelContextRole.System, "system"),
                ModelContextEntry.Text("session-2", 2, ModelContextRole.User, "request"),
            },
            new[]
            {
                AssistantEntry("run-3", 3),
                ToolResultEntry("run-4", 4),
            });

    private static ModelContextEntry AssistantEntry(string sourceId, long sequence)
    {
        var calls = new[]
        {
            new ModelToolCall(
                "provider-call-1",
                "files.read",
                "1",
                Json("{\"path\":\"README.md\"}")),
            new ModelToolCall(
                null,
                "files.list",
                "1",
                Json("{\"path\":\"docs\"}")),
        };
        return ModelContextEntry.Assistant(
            sourceId,
            sequence,
            new AssistantStep("checking", calls, AssistantFinishReason.ToolCalls, 128));
    }

    private static ModelContextEntry ToolResultEntry(string sourceId, long sequence) =>
        ModelContextEntry.ToolResult(
            sourceId,
            sequence,
            new ToolExecutionReceipt(
                new ExecutionId("exec_11111111111111111111111111111111"),
                new ToolCallId("call_11111111111111111111111111111111"),
                ToolExecutionOutcome.Succeeded,
                "ok",
                new PayloadId("payload_11111111111111111111111111111111"),
                sideEffectsPossible: false),
            "{\"content\":\"bounded result\"}");

    private static JsonElement Json(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private sealed class StubContextSource : IModelContextSource
    {
        private readonly IReadOnlyList<ModelContextEntry> _session;
        private readonly IReadOnlyList<ModelContextEntry> _run;

        public StubContextSource(
            IReadOnlyList<ModelContextEntry> session,
            IReadOnlyList<ModelContextEntry> run)
        {
            _session = session;
            _run = run;
        }

        public Task<ModelContextSourceSnapshot> LoadAsync(
            RunId runId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ModelContextSourceSnapshot(_session, _run));
    }

    private sealed class FixedSnapshotSource : IModelContextSource
    {
        private readonly ModelContextSourceSnapshot _snapshot;

        public FixedSnapshotSource(ModelContextSourceSnapshot snapshot) =>
            _snapshot = snapshot;

        public Task<ModelContextSourceSnapshot> LoadAsync(
            RunId runId,
            CancellationToken cancellationToken) =>
            Task.FromResult(_snapshot);
    }
}
