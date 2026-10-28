using RuBackend.Business;
using RuBackend.Business.Sessions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace RuBackend.Tests.Business;

public sealed class SessionStoreDurableHistoryTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-session-history-" + Guid.NewGuid().ToString("N"));
    private DataPaths Paths => new(Path.Combine(_root, "settings.json"), Path.Combine(_root, "sessions"), Path.Combine(_root, "activity.jsonl"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
               {
                   DataSource = Paths.AgentHarnessDatabase,
                   Mode = SqliteOpenMode.ReadWriteCreate,
                   Pooling = true,
                   Cache = SqliteCacheMode.Private,
                   DefaultTimeout = 5,
               }.ToString()))
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Message_pages_remain_stable_on_append_and_become_stale_after_rewind()
    {
        var store = new SessionStore(Paths);
        var sessionId = (await store.CreateAsync(default))!;
        var initial = Enumerable.Range(0, 230)
            .Select(index => new SessionMessage("user", $"message-{index}"))
            .ToArray();
        Assert.True(await store.AppendAsync(sessionId, initial, default));

        var compatibility = await store.GetAsync(sessionId, default);
        Assert.Equal(30, compatibility!.MessageOffset);
        Assert.Equal(230, compatibility.TotalMessageCount);
        Assert.Equal("message-30", compatibility.Messages[0].Content);

        var first = await store.GetMessagePageAsync(sessionId, null, 40, default);
        Assert.Equal(190, first!.StartIndex);
        Assert.Equal(230, first.TotalMessages);
        Assert.Equal("message-229", first.Messages[^1].Content);
        Assert.NotNull(first.NextCursor);
        Assert.True(await store.AppendAsync(sessionId, [new("user", "appended-later")], default));
        var second = await store.GetMessagePageAsync(sessionId, first.NextCursor, 40, default);
        Assert.Equal(150, second!.StartIndex);
        Assert.Equal(230, second.TotalMessages);
        Assert.Equal("message-150", second.Messages[0].Content);

        var invalidated = await store.RewindAsync(sessionId, 90, default);
        Assert.Equal(91, invalidated!.TotalMessageCount);
        Assert.Equal(1, invalidated.HistoryEpoch);
        var stale = await Assert.ThrowsAsync<SessionHistoryException>(() =>
            store.GetMessagePageAsync(sessionId, first.NextCursor, 40, default));
        Assert.Equal("session_history_cursor_stale", stale.Code);
    }

    [Fact]
    public async Task Rewind_keeps_source_receipts_and_delete_cascades_messages_and_search_rows()
    {
        var store = new SessionStore(Paths);
        var sessionId = (await store.CreateAsync(default))!;
        var sourced = new SessionMessage("user", "preserved source", "evt_replay", "run_replay");
        Assert.Equal(SessionAppendOutcome.Appended, await store.AppendIfAbsentAsync(sessionId, sourced, default));
        Assert.True(await store.AppendAsync(sessionId, [new("ai", "durable searchable phrase")], default));

        Assert.NotNull(await store.RewindAsync(sessionId, 0, default));
        Assert.Equal(SessionAppendOutcome.AlreadyPresent, await store.AppendIfAbsentAsync(sessionId, sourced, default));
        Assert.Empty(await store.SearchMessagesAsync("durable searchable", 10, default));
        Assert.Single(await store.SearchMessagesAsync("preserved source", 10, default));

        Assert.True(await store.DeleteAsync(sessionId, default));
        Assert.Empty(await store.SearchMessagesAsync("preserved source", 10, default));
        await using var connection = new SqliteConnection($"Data Source={Paths.AgentHarnessDatabase};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT count(*) FROM business_session_messages WHERE session_id=$id) + (SELECT count(*) FROM business_session_source_receipts WHERE session_id=$id);";
        command.Parameters.AddWithValue("$id", sessionId);
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Summary_pages_filter_title_and_content_with_literal_wildcards()
    {
        var store = new SessionStore(Paths);
        var matching = (await store.CreateAsync(default))!;
        Assert.True(await store.AppendAsync(matching, [new("user", "body has %_ literal")], default));
        var other = (await store.CreateAsync(default))!;
        Assert.True(await store.AppendAsync(other, [new("user", "ordinary content")], default));

        var page = await store.ListPageAsync(null, 10, "%_", default);

        Assert.Equal(matching, Assert.Single(page.Sessions).Id);
        Assert.Null(page.NextCursor);
        var hits = await store.SearchMessagesAsync("body has", 10, default);
        Assert.Equal(matching, Assert.Single(hits).SessionId);
        Assert.Equal(0, hits[0].MessageIndex);
        Assert.Equal("user", hits[0].Role);
    }

    [Fact]
    public async Task Summary_cursor_roundtrips_long_unicode_query_within_cursor_bound()
    {
        var store = new SessionStore(Paths);
        var query = string.Concat(Enumerable.Repeat("你好", 256));
        var ids = new List<string>();
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var id = (await store.CreateAsync(default))!;
            ids.Add(id);
            Assert.True(await store.AppendAsync(id, [new("user", $"prefix {query} suffix")], default));
        }

        var first = await store.ListPageAsync(null, 1, query, default);
        Assert.NotNull(first.NextCursor);
        Assert.True(first.NextCursor.Length <= 2048);
        var second = await store.ListPageAsync(first.NextCursor, 1, query, default);

        Assert.Null(second.NextCursor);
        Assert.Equal(2, first.Sessions.Count + second.Sessions.Count);
        Assert.Equal(ids.ToHashSet(), first.Sessions.Concat(second.Sessions).Select(row => row.Id).ToHashSet());
    }

    [Fact]
    public async Task Summary_query_matches_vietnamese_title_using_invariant_unicode_fold()
    {
        var store = new SessionStore(Paths);
        var matching = (await store.CreateAsync(default))!;
        Assert.True(await store.AppendAsync(matching, [new("user", "ordinary unrelated message")], default));
        await using (var connection = new SqliteConnection($"Data Source={Paths.AgentHarnessDatabase};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE business_sessions SET title='Xin Chào Việt Nam' WHERE session_id=$id;";
            command.Parameters.AddWithValue("$id", matching);
            await command.ExecuteNonQueryAsync();
        }

        var page = await store.ListPageAsync(null, 10, "VIỆT nam", default);

        Assert.Equal(matching, Assert.Single(page.Sessions).Id);
    }

    [Fact]
    public async Task A_valid_message_that_expands_past_four_mib_can_still_be_read_as_one_bounded_item()
    {
        var store = new SessionStore(Paths);
        var sessionId = (await store.CreateAsync(default))!;
        var content = new string('\0', 1024 * 1024);
        Assert.True(await store.AppendAsync(sessionId, [new("user", content)], default));

        var compatibility = await store.GetAsync(sessionId, default);
        var page = await store.GetMessagePageAsync(sessionId, null, 200, default);

        Assert.Equal(1, compatibility!.TotalMessageCount);
        Assert.Single(page!.Messages);
        Assert.Equal(content, page.Messages[0].Content);
        Assert.Null(page.NextCursor);
    }
}
