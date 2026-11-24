using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class ProviderSelectionBatchTests
{
    [Fact]
    public async Task Mixed_pins_unpinned_and_missing_runs_match_single_reads_without_cross_run_metadata()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var ids = await CreateRunsAsync(fixture, 4);
        var store = new SqliteAgentRunProviderSelectionStore(fixture.Options);
        var anthropic = Selection("anthropic_messages", "claude-test", 200_000, "Provider A", "provider-a_api_key");
        var openai = Selection("openai_responses", "gpt-test", 128_000, "Provider B", "provider-b_api_key");
        await store.PinAsync(ids[0], anthropic, default);
        await store.PinAsync(ids[1], openai, default);
        await store.PinAsync(ids[3], Selection("chat_completions", "unrequested", 64_000), default);
        await SqlAsync(fixture, "UPDATE agent_run_provider_selections SET request_options_json='[]' WHERE run_id=$run", ids[3]);
        var missing = HarnessStoreTestFixture.Run(999);

        var states = await store.GetManyAsync([ids[2], ids[0], missing, ids[1], ids[0]], default);

        Assert.Equal(3, states.Count);
        Assert.Equal(anthropic, states[ids[0]].Selection);
        Assert.Equal(openai, states[ids[1]].Selection);
        Assert.True(states[ids[2]].Exists);
        Assert.Null(states[ids[2]].Selection);
        Assert.False(states.ContainsKey(missing));
        Assert.False(states.ContainsKey(ids[3]));
        foreach (var id in ids.Take(3)) Assert.Equal(await store.GetAsync(id, default), states[id]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unpinned_delegated_runs_are_absent_and_pinned_delegated_runs_remain_readable(bool attemptOnly)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var ids = await CreateRunsAsync(fixture, 2);
        var store = new SqliteAgentRunProviderSelectionStore(fixture.Options);
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = attemptOnly
                ? """
                    INSERT INTO agent_subagent_families(root_run_id,canceled) VALUES($root,0);
                    INSERT INTO agent_subagents(agent_id,root_run_id,internal_session_id,role,canceled,invocation_id,request_hash)
                    VALUES('agent_batch',$root,$session,0,0,'invocation_batch',$hash);
                    INSERT INTO agent_subagent_attempts(agent_id,attempt_number,run_id,created_at_utc)
                    VALUES('agent_batch',1,$run,'2026-11-24T00:00:00Z');
                    """
                : "INSERT INTO agent_internal_runs(run_id,root_run_id) VALUES($run,$root)";
            command.Parameters.AddWithValue("$run", ids[1].Value);
            command.Parameters.AddWithValue("$root", ids[0].Value);
            if (attemptOnly)
            {
                command.Parameters.AddWithValue("$session", HarnessStoreTestFixture.Session(3).Value);
                command.Parameters.AddWithValue("$hash", HarnessStoreTestFixture.Sha('a'));
            }
            await command.ExecuteNonQueryAsync();
        }

        var unpinned = await store.GetManyAsync(ids, default);

        Assert.Null(await store.GetAsync(ids[1], default));
        Assert.False(unpinned.ContainsKey(ids[1]));
        Assert.Null(unpinned[ids[0]].Selection);
        var pin = Selection("chat_completions", "delegated", 100_000);
        await store.PinAsync(ids[1], pin, default);

        var pinned = await store.GetManyAsync(ids, default);

        Assert.Equal(pin, pinned[ids[1]].Selection);
        Assert.Equal(await store.GetAsync(ids[1], default), pinned[ids[1]]);
        Assert.Null(pinned[ids[0]].Selection);
    }

    [Fact]
    public async Task Empty_input_never_opens_or_creates_a_database()
    {
        var options = UnavailableOptions();
        var store = new SqliteAgentRunProviderSelectionStore(options);

        Assert.Empty(await store.GetManyAsync(Array.Empty<RunId>(), default));

        Assert.False(Directory.Exists(Path.GetDirectoryName(options.DatabasePath)));
        Assert.False(File.Exists(options.DatabasePath));
    }

    [Fact]
    public async Task More_than_a_hundred_ids_are_rejected_before_opening_the_database()
    {
        var options = UnavailableOptions();
        var store = new SqliteAgentRunProviderSelectionStore(options);
        var ids = Enumerable.Range(1, 101).Select(HarnessStoreTestFixture.Run).ToArray();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.GetManyAsync(ids, default));

        Assert.False(Directory.Exists(Path.GetDirectoryName(options.DatabasePath)));
    }

    [Fact]
    public async Task A_hundred_ids_fit_one_page_and_missing_rows_are_omitted()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var store = new SqliteAgentRunProviderSelectionStore(fixture.Options);
        var ids = Enumerable.Range(1, 100).Select(HarnessStoreTestFixture.Run).ToArray();

        Assert.Empty(await store.GetManyAsync(ids, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_is_preserved_before_any_database_access(bool empty)
    {
        var options = UnavailableOptions();
        var store = new SqliteAgentRunProviderSelectionStore(options);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        RunId[] ids = empty ? [] : [HarnessStoreTestFixture.Run(1)];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetManyAsync(ids, cancellation.Token));

        Assert.False(Directory.Exists(Path.GetDirectoryName(options.DatabasePath)));
    }

    [Theory]
    [InlineData("context_window_tokens=0")]
    [InlineData("output_token_reserve=0")]
    [InlineData("request_options_json='[]'")]
    [InlineData("request_options_json='{broken'")]
    [InlineData("base_uri='https://user:password@provider.invalid/v1'")]
    public async Task Corrupt_requested_metadata_fails_with_the_same_named_error_as_a_single_read(string assignment)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var ids = await CreateRunsAsync(fixture, 2);
        var store = new SqliteAgentRunProviderSelectionStore(fixture.Options);
        await store.PinAsync(ids[0], Selection("chat_completions", "valid", 64_000), default);
        await store.PinAsync(ids[1], Selection("chat_completions", "corrupt", 64_000), default);
        await SqlAsync(fixture, $"UPDATE agent_run_provider_selections SET {assignment} WHERE run_id=$run", ids[1]);

        var single = await Assert.ThrowsAsync<HarnessStoreException>(() => store.GetAsync(ids[1], default));
        var batch = await Assert.ThrowsAsync<HarnessStoreException>(() => store.GetManyAsync(ids, default));

        Assert.Equal("provider_selection_invalid", single.Code);
        Assert.Equal(single.Code, batch.Code);
    }

    private static AgentRunProviderSelection Selection(string protocol, string model, long context,
        string label = "Test model", string? account = null) => new(
            "Custom OpenAI", "custom", protocol, "https://provider.invalid/v1", model, account,
            "{\"reasoning_effort\":\"high\"}", protocol == "anthropic_messages" ? 4096 : null,
            label, "native", context, "provider_catalog", "test-v1", 4096);

    private static async Task<RunId[]> CreateRunsAsync(HarnessStoreTestFixture fixture, int count)
    {
        await fixture.RegisterWorkspaceAsync(1);
        var store = fixture.CreateEventStore();
        var ids = new List<RunId>();
        for (var number = 1; number <= count; number++)
        {
            var run = fixture.NewRun(number, number, 1);
            Assert.Equal(CreateRunOutcome.Created, (await store.TryCreateRunAsync(run, default)).Outcome);
            ids.Add(run.Run.RunId);
        }
        return ids.ToArray();
    }

    private static async Task SqlAsync(HarnessStoreTestFixture fixture, string sql, RunId run)
    {
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$run", run.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static HarnessSqliteOptions UnavailableOptions() => new(Path.Combine(Path.GetTempPath(),
        "ru-provider-batch-tests", Guid.NewGuid().ToString("N"), "missing.db"));
}
