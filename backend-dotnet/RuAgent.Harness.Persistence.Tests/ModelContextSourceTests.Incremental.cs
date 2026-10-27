using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Context;
using RuAgent.Harness.Persistence.Privacy;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed partial class ModelContextSourceTests
{
    [Fact]
    public async Task Version_30_upgrade_preserves_context_and_installs_mutation_invalidation()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var payload = TextPayload(1, HarnessPayloadKind.UserPrompt, "retained v30 input");
        var request = fixture.NewRun(1, 1, 1, payloads: [payload]);
        await fixture.CreateEventStore().TryCreateRunAsync(request, default);
        await fixture.PayloadStore.CaptureBaseContextAsync(request.Run.RunId, [], HarnessStoreTestFixture.Timestamp, default);
        await fixture.ResetContextProjectionSchemaToV30Async();
        var health = await HarnessSqliteSchema.InitializeAsync(fixture.Options, default);
        Assert.True(health.IsAvailable, health.ErrorCode);
        var source = new SqliteModelContextSource(fixture.Options, fixture.PayloadStore);
        Assert.Equal("retained v30 input", Assert.Single((await source.LoadAsync(request.Run.RunId, default)).RunLocal).Content);
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE agent_payloads SET authentication_tag=zeroblob(16) WHERE payload_id=$payload";
        command.Parameters.AddWithValue("$payload", payload.PayloadId.Value);
        await command.ExecuteNonQueryAsync();
        var error = await Assert.ThrowsAsync<HarnessStoreException>(() => source.LoadAsync(request.Run.RunId, default));
        Assert.Equal("payload_authentication_failed", error.Code);
    }

    [Fact]
    public async Task Warm_active_context_reads_only_new_payloads_and_matches_a_cold_rebuild()
    {
        var ownMeasurements = new AsyncLocal<bool>();
        var projectionLookups = new Dictionary<string, long>(StringComparer.Ordinal);
        using var metrics = new System.Diagnostics.Metrics.MeterListener();
        metrics.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == "ru.agent.context.projection.lookups") listener.EnableMeasurementEvents(instrument);
        };
        metrics.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            if (!ownMeasurements.Value) return;
            Assert.Single(tags.ToArray());
            Assert.Equal("result", tags[0].Key);
            var result = (string)tags[0].Value!;
            projectionLookups[result] = projectionLookups.GetValueOrDefault(result) + value;
        });
        metrics.Start();
        ownMeasurements.Value = true;
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var store = fixture.CreateEventStore();
        var request = fixture.NewRun(1, 1, 1);
        await store.TryCreateRunAsync(request, default);
        await fixture.PayloadStore.CaptureBaseContextAsync(request.Run.RunId, [], HarnessStoreTestFixture.Timestamp, default);
        const int count = 1100; // Beyond the old decrypted-entry cache capacity.
        var payloads = Enumerable.Range(0, count).Select(i => TextPayload(i + 100,
            HarnessPayloadKind.UserPrompt, new string('x', 2000))).ToArray();
        var events = payloads.Select((p, i) => new HarnessEventIntent(HarnessStoreTestFixture.Event(i + 100),
            HarnessEventKind.RunNoteAdded, payloadId: p.PayloadId, safeCode: "context_note")).ToArray();
        var current = NextRun(request.Run, 1, count + 1);
        Assert.Equal(HarnessCommitOutcome.Committed, (await store.CommitAsync(new HarnessCommit(
            current.RunId, 0, current, events, payloads, []), default)).Outcome);
        var keys = new CountingContextKeys();
        var payloadStore = new HarnessPayloadStore(fixture.Options, new HarnessPayloadCryptography(keys), KnownSecretRedactor.Empty);
        var warm = new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, payloadStore));
        var before = await warm.AssembleAsync(current.RunId, default);
        var keyReads = keys.Reads;
        Assert.Equal(before.CanonicalUtf8.ToArray(), (await warm.AssembleAsync(current.RunId, default)).CanonicalUtf8.ToArray());
        Assert.InRange(keys.Reads - keyReads, 1, 2);
        Assert.True(projectionLookups.GetValueOrDefault("miss") > 0);
        Assert.True(projectionLookups.GetValueOrDefault("hit") > 0);

        var appended = TextPayload(2000, HarnessPayloadKind.UserPrompt, "new evidence");
        var next = NextRun(current, 2, count + 2);
        await store.CommitAsync(new HarnessCommit(current.RunId, 1, next,
            [new HarnessEventIntent(HarnessStoreTestFixture.Event(2000), HarnessEventKind.RunNoteAdded,
                payloadId: appended.PayloadId, safeCode: "context_note")], [appended], []), default);
        keyReads = keys.Reads;
        var delta = await warm.AssembleAsync(current.RunId, default);
        Assert.InRange(keys.Reads - keyReads, 1, 3);
        var cold = await new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, payloadStore, enableEntryCache: false))
            .AssembleAsync(current.RunId, default);
        Assert.Equal(cold.CanonicalUtf8.ToArray(), delta.CanonicalUtf8.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compaction_can_replace_historical_base_and_replay_the_next_summary(bool warm)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var store = fixture.CreateEventStore();
        var request = fixture.NewRun(1, 1, 1);
        await store.TryCreateRunAsync(request, default);
        await fixture.PayloadStore.CaptureBaseContextAsync(request.Run.RunId,
            [new HarnessBaseContextEntry(HarnessStoreTestFixture.Payload(10), "base:turn:1:user", 1, ModelContextRole.User, "old request"),
             new HarnessBaseContextEntry(HarnessStoreTestFixture.Payload(11), "base:turn:2:assistant", 2, ModelContextRole.Assistant, "old answer")],
            HarnessStoreTestFixture.Timestamp, default);
        var assembler = new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore));
        if (warm) await assembler.AssembleAsync(request.Run.RunId, default);
        var time = HarnessStoreTestFixture.Timestamp.AddSeconds(1);
        var next = RunAt(request.Run, 1, 2, time);
        await CommitCompactionAsync(store, request.Run.RunId, next, 0, 20,
            "compact-base", "historical checkpoint", "base:turn:1:user", time);
        var first = await assembler.AssembleAsync(request.Run.RunId, default);
        Assert.DoesNotContain(first.Entries, e => e.SourceId == "base:turn:1:user");
        Assert.Equal("historical checkpoint", first.Entries[0].Content);
        Assert.Equal("base:turn:2:assistant", first.Entries[1].SourceId);
        var later = time.AddSeconds(1);
        await CommitCompactionAsync(store, request.Run.RunId, RunAt(next, 2, 3, later), 1, 21,
            "compact-again", "new checkpoint", "compact-base", later);
        var second = await assembler.AssembleAsync(request.Run.RunId, default);
        var cold = await new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore))
            .AssembleAsync(request.Run.RunId, default);
        Assert.Equal("new checkpoint", second.Entries[0].Content);
        Assert.Equal(cold.CanonicalUtf8.ToArray(), second.CanonicalUtf8.ToArray());
    }

    private sealed class CountingContextKeys : IHarnessKeyStore
    {
        public int Reads { get; private set; }
        public ValueTask<byte[]> GetOrCreateKeyAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Reads++;
            return ValueTask.FromResult(Enumerable.Repeat((byte)0x5a, 32).ToArray());
        }
    }
}
