using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Context;
using RuAgent.Harness.Persistence.Privacy;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class ContextEntryCacheTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("replaced")]
    [InlineData("tampered")]
    [InlineData("forbidden")]
    public async Task Warm_context_still_validates_the_current_key_and_encrypted_row(string failure)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var payload = new HarnessPayloadIntent(HarnessStoreTestFixture.Payload(1), HarnessPayloadKind.UserPrompt,
            HarnessPayloadAudience.ModelContext | HarnessPayloadAudience.AuthenticatedClient,
            HarnessPayloadEncoding.Utf8Text, 4096, Encoding.UTF8.GetBytes("synthetic context"));
        var request = fixture.NewRun(1, 1, 1, payloads: [payload]);
        await fixture.CreateEventStore().TryCreateRunAsync(request, default);
        await fixture.PayloadStore.CaptureBaseContextAsync(request.Run.RunId, [], HarnessStoreTestFixture.Timestamp, default);
        var keys = new ChangingKeys();
        var store = new HarnessPayloadStore(fixture.Options, new HarnessPayloadCryptography(keys), KnownSecretRedactor.Empty);
        var source = new SqliteModelContextSource(fixture.Options, store);
        var assembler = new AgentContextAssembler(source);
        var first = await assembler.AssembleAsync(request.Run.RunId, default);
        int before = keys.Reads;
        var warm = await assembler.AssembleAsync(request.Run.RunId, default);
        Assert.Equal(first.CanonicalUtf8.ToArray(), warm.CanonicalUtf8.ToArray());
        Assert.Equal(1, keys.Reads - before); // Current key checked; cached rows need no second key/decrypt.
        var uncached = await new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, store, enableEntryCache: false))
            .AssembleAsync(request.Run.RunId, default);
        Assert.Equal(warm.CanonicalUtf8.ToArray(), uncached.CanonicalUtf8.ToArray());

        if (failure is "missing" or "replaced") keys.Failure = failure;
        else
        {
            await using var connection = fixture.CreateRawConnection();
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = failure == "tampered"
                ? "UPDATE agent_payloads SET authentication_tag = zeroblob(16) WHERE payload_id = $payload"
                : "UPDATE agent_payloads SET audience = 1 WHERE payload_id = $payload";
            command.Parameters.AddWithValue("$payload", payload.PayloadId.Value);
            await command.ExecuteNonQueryAsync();
        }
        var error = await Assert.ThrowsAsync<HarnessStoreException>(() => assembler.AssembleAsync(request.Run.RunId, default));
        Assert.Equal(failure switch
        {
            "missing" => "harness_key_missing",
            "forbidden" => "context_payload_forbidden",
            _ => "payload_authentication_failed",
        }, error.Code);
    }

    [Fact]
    public void Cache_has_a_bounded_capacity_and_clear_removes_parsed_rows()
    {
        var cache = new ContextEntryCache();
        var entry = ModelContextEntry.Text("synthetic", 1, ModelContextRole.User, "data");
        for (int index = 0; index <= ContextEntryCache.MaximumEntries; index++) cache.Add(index.ToString(), entry, 10);
        Assert.True(cache.TryGet("0", out _));
        Assert.False(cache.TryGet(ContextEntryCache.MaximumEntries.ToString(), out _));
        cache.Clear();
        Assert.False(cache.TryGet("0", out _));
    }

    [Fact]
    public void Repeated_over_capacity_scans_reuse_the_admitted_prefix_instead_of_thrashing()
    {
        var cache = new ContextEntryCache();
        var entry = ModelContextEntry.Text("synthetic", 1, ModelContextRole.User, "data");
        int hits = 0;
        for (int scan = 0; scan < 2; scan++)
            for (int index = 0; index <= ContextEntryCache.MaximumEntries; index++)
                if (cache.TryGet(index.ToString(), out _)) hits++;
                else cache.Add(index.ToString(), entry, 10);
        Assert.Equal(ContextEntryCache.MaximumEntries, hits);
    }

    private sealed class ChangingKeys : IHarnessKeyStore
    {
        public int Reads;
        public string? Failure;
        public ValueTask<byte[]> GetOrCreateKeyAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Reads++;
            if (Failure == "missing") throw new HarnessStoreException("harness_key_missing");
            return ValueTask.FromResult(Enumerable.Repeat(Failure == "replaced" ? (byte)0x7b : (byte)0x5a, 32).ToArray());
        }
    }
}
