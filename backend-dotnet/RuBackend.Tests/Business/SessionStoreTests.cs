using System.Text.RegularExpressions;
using System.Text.Json;
using RuBackend.Business;
using RuBackend.Business.Sessions;
using Xunit;

namespace RuBackend.Tests.Business;

public sealed class SessionStoreTests
{
    [Fact]
    public async Task Create_writes_a_valid_default_session_document()
    {
        var (store, _) = CreateStore();

        var sessionId = await store.CreateAsync(CancellationToken.None);
        var session = await store.GetAsync(sessionId!, CancellationToken.None);

        Assert.Matches(new Regex("^[a-f0-9]{32}$"), sessionId!);
        Assert.NotNull(session);
        Assert.Equal(sessionId, session!.Id);
        Assert.Equal("Cuộc trò chuyện mới", session.Title);
        Assert.Equal(TimeSpan.Zero, session.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, session.UpdatedAt.Offset);
        Assert.Empty(session.Messages);
    }

    [Fact]
    public async Task Append_filters_invalid_messages_generates_title_and_retains_last_200()
    {
        var (store, _) = CreateStore();
        var sessionId = (await store.CreateAsync(CancellationToken.None))!;
        var messages = new List<SessionMessage>
        {
            new("invalid", "must be filtered"),
            new("user", "   "),
            new("user", "  " + new string('a', 60) + "  "),
            new("ai", "first answer"),
        };
        messages.AddRange(Enumerable.Range(0, 205)
            .Select(index => new SessionMessage(index % 2 == 0 ? "user" : "ai", $"message-{index}")));

        Assert.True(await store.AppendAsync(sessionId, messages, CancellationToken.None));
        var session = await store.GetAsync(sessionId, CancellationToken.None);

        Assert.NotNull(session);
        Assert.Equal(new string('a', 50), session!.Title);
        Assert.Equal(200, session.Messages.Count);
        Assert.DoesNotContain(session.Messages, message => message.Role == "invalid");
        Assert.Equal("message-5", session.Messages[0].Content);
        Assert.Equal("message-204", session.Messages[^1].Content);
    }

    [Fact]
    public async Task List_orders_sessions_by_updated_time_descending()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero));
        var (store, _) = CreateStore(clock);
        var first = (await store.CreateAsync(CancellationToken.None))!;
        clock.Advance(TimeSpan.FromMinutes(1));
        var second = (await store.CreateAsync(CancellationToken.None))!;
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.AppendAsync(first, [new SessionMessage("user", "newest")], CancellationToken.None);

        var sessions = await store.ListAsync(CancellationToken.None);

        Assert.Equal([first, second], sessions.Select(session => session.Id).ToArray());
    }

    [Fact]
    public async Task List_honors_cancellation_even_when_the_session_directory_is_missing()
    {
        var (store, _) = CreateStore();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.ListAsync(cancellation.Token));
    }

    [Fact]
    public async Task List_updates_indexed_summaries_after_append_and_delete()
    {
        var (store, _) = CreateStore();
        var sessionId = (await store.CreateAsync(CancellationToken.None))!;
        Assert.Single(await store.ListAsync(CancellationToken.None));

        Assert.True(await store.AppendAsync(
            sessionId,
            [new SessionMessage("user", "A searchable conversation")],
            CancellationToken.None));
        var updated = Assert.Single(await store.ListAsync(CancellationToken.None));

        Assert.Equal("A searchable conversation", updated.Title);
        Assert.Equal(1, updated.MessageCount);
        Assert.True(await store.DeleteAsync(sessionId, CancellationToken.None));
        Assert.Empty(await store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task List_imports_all_valid_sessions_and_selects_the_newest_500_by_update_time()
    {
        var (store, paths) = CreateStore();
        Directory.CreateDirectory(paths.SessionDirectory);
        for (var index = 0; index < 2005; index++)
        {
            var id = index.ToString("x32");
            await File.WriteAllTextAsync(
                Path.Combine(paths.SessionDirectory, id + ".json"),
                JsonSerializer.Serialize(new
                {
                    id,
                    title = $"session-{index}",
                    created_at = DateTimeOffset.UnixEpoch,
                    updated_at = DateTimeOffset.UnixEpoch.AddSeconds(index),
                    messages = Array.Empty<object>(),
                }));
        }

        var enumerated = Directory.GetFiles(paths.SessionDirectory, "*.json");
        var newestPath = enumerated[2000];
        var newestId = Path.GetFileNameWithoutExtension(newestPath);
        await File.WriteAllTextAsync(
            newestPath,
            JsonSerializer.Serialize(new
            {
                id = newestId,
                title = "newest by persisted update time",
                created_at = DateTimeOffset.UnixEpoch,
                updated_at = DateTimeOffset.UnixEpoch.AddYears(100),
                messages = Array.Empty<object>(),
            }));
        File.SetLastWriteTimeUtc(newestPath, DateTime.UnixEpoch);

        var listing = await store.ListWithMetadataAsync(CancellationToken.None);

        Assert.Equal(500, listing.Sessions.Count);
        Assert.Equal(newestId, listing.Sessions[0].Id);
        Assert.True(listing.IsTruncated);
    }

    [Fact]
    public async Task List_ignores_legacy_file_replacement_after_one_time_import()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero));
        var (store, paths) = CreateStore(clock);
        const string sessionId = "22222222222222222222222222222222";
        Directory.CreateDirectory(paths.SessionDirectory);
        var path = Path.Combine(paths.SessionDirectory, sessionId + ".json");
        await File.WriteAllTextAsync(path, SessionJson(sessionId, "Old title"));
        var originalInfo = new FileInfo(path);
        var originalWriteTime = originalInfo.LastWriteTimeUtc;
        var originalCreationTime = originalInfo.CreationTimeUtc;
        Assert.Equal("Old title", Assert.Single(await store.ListAsync(CancellationToken.None)).Title);

        await File.WriteAllTextAsync(path, SessionJson(sessionId, "New title"));
        File.SetLastWriteTimeUtc(path, originalWriteTime);
        File.SetCreationTimeUtc(path, originalCreationTime);
        var replacementInfo = new FileInfo(path);
        Assert.Equal(originalInfo.Length, replacementInfo.Length);
        Assert.Equal(originalWriteTime, replacementInfo.LastWriteTimeUtc);
        Assert.Equal(originalCreationTime, replacementInfo.CreationTimeUtc);
        clock.Advance(TimeSpan.FromSeconds(61));

        var refreshed = await store.ListAsync(CancellationToken.None);

        Assert.Equal("Old title", Assert.Single(refreshed).Title);
    }

    [Fact]
    public async Task List_queries_all_imported_metadata_without_a_json_scan_budget()
    {
        var (store, paths) = CreateStore();
        Directory.CreateDirectory(paths.SessionDirectory);
        var padding = new string('x', 13 * 1024 * 1024);
        foreach (var (id, second) in new[]
        {
            ("33333333333333333333333333333333", 2),
            ("44444444444444444444444444444444", 1),
            ("66666666666666666666666666666666", 0),
        })
        {
            var path = Path.Combine(paths.SessionDirectory, id + ".json");
            await File.WriteAllTextAsync(path, $$"""
                {"id":"{{id}}","title":"large","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:0{{second}}Z","messages":[],"padding":"{{padding}}"}
                """);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(second));
        }

        var first = await store.ListWithMetadataAsync(CancellationToken.None);
        var resumed = await store.ListWithMetadataAsync(CancellationToken.None);

        Assert.Equal(3, first.Sessions.Count);
        Assert.False(first.IsTruncated);
        Assert.Equal(3, resumed.Sessions.Count);
        Assert.False(resumed.IsTruncated);
    }

    [Fact]
    public async Task List_preserves_caller_cancellation_when_internal_work_is_bounded()
    {
        var (store, paths) = CreateStore();
        Directory.CreateDirectory(paths.SessionDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(paths.SessionDirectory, "55555555555555555555555555555555.json"),
            SessionJson("55555555555555555555555555555555", "cancel"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.ListWithMetadataAsync(cancellation.Token));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("..\\evil")]
    [InlineData("")]
    [InlineData("has space")]
    public async Task Invalid_session_ids_never_escape_the_session_directory(string sessionId)
    {
        var (store, _) = CreateStore();

        Assert.Null(await store.GetAsync(sessionId, CancellationToken.None));
        Assert.False(await store.DeleteAsync(sessionId, CancellationToken.None));
        Assert.False(await store.AppendAsync(
            sessionId,
            [new SessionMessage("user", "blocked")],
            CancellationToken.None));
    }

    [Fact]
    public async Task GetContextTurns_returns_only_the_last_eight_complete_pairs()
    {
        var (store, _) = CreateStore();
        var sessionId = (await store.CreateAsync(CancellationToken.None))!;
        var messages = Enumerable.Range(0, 10)
            .SelectMany(index => new[]
            {
                new SessionMessage("user", $"prompt-{index}"),
                new SessionMessage("ai", $"response-{index}"),
            })
            .Append(new SessionMessage("user", "incomplete"))
            .ToArray();
        await store.AppendAsync(sessionId, messages, CancellationToken.None);

        var turns = await store.GetContextTurnsAsync(sessionId, 8, CancellationToken.None);

        Assert.Equal(8, turns.Count);
        Assert.Equal("prompt-2", turns[0].Prompt);
        Assert.Equal("response-9", turns[^1].Response);
        Assert.DoesNotContain(turns, turn => turn.Prompt == "incomplete");
    }

    [Fact]
    public async Task Concurrent_appends_are_serialized_without_losing_messages()
    {
        var (store, _) = CreateStore();
        var sessionId = (await store.CreateAsync(CancellationToken.None))!;

        await Task.WhenAll(
            store.AppendAsync(sessionId, [new SessionMessage("user", "one")], CancellationToken.None),
            store.AppendAsync(sessionId, [new SessionMessage("ai", "two")], CancellationToken.None));

        var session = await store.GetAsync(sessionId, CancellationToken.None);
        Assert.Equal(2, session!.Messages.Count);
        Assert.Contains(session.Messages, message => message.Content == "one");
        Assert.Contains(session.Messages, message => message.Content == "two");
    }

    [Fact]
    public async Task Delete_returns_false_for_missing_and_true_after_removal()
    {
        var (store, _) = CreateStore();
        var sessionId = (await store.CreateAsync(CancellationToken.None))!;

        Assert.True(await store.DeleteAsync(sessionId, CancellationToken.None));
        Assert.False(await store.DeleteAsync(sessionId, CancellationToken.None));
        Assert.Null(await store.GetAsync(sessionId, CancellationToken.None));
    }

    [Fact]
    public async Task List_skips_a_torn_session_file_instead_of_failing()
    {
        // Previously ListAsync held the exclusive gate while loading every
        // file, so one torn write failed the whole sidebar poll. Writes are
        // atomic (temp + move) and reads no longer take the gate; a file that
        // cannot be parsed is skipped and picked up on the next poll.
        var (store, paths) = CreateStore();
        var good = (await store.CreateAsync(CancellationToken.None))!;
        Directory.CreateDirectory(paths.SessionDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(paths.SessionDirectory, "22222222222222222222222222222222.json"),
            "{ torn json,");

        var listed = await store.ListAsync(CancellationToken.None);

        var single = Assert.Single(listed);
        Assert.Equal(good, single.Id);
    }

    [Fact]
    public async Task A_sidebar_listing_never_costs_a_chat_turn()
    {
        // ListAsync deliberately runs without the store gate on a three second
        // poll, and a write replaces the session file with File.Move(overwrite).
        // On Windows the replace fails while any handle is open, so the poll and
        // a send used to collide: the append lost and reported a bare false, and
        // the user's turn was dropped with nothing to show for it. Reads and the
        // replace are serialized per file operation now, so every append lands.
        var (store, _) = CreateStore();
        var sessionIds = new List<string>();
        for (var index = 0; index < 12; index++)
        {
            sessionIds.Add((await store.CreateAsync(CancellationToken.None))!);
        }

        const int rounds = 5;
        const int perRound = 6;
        for (var round = 0; round < rounds; round++)
        {
            var listing = store.ListAsync(CancellationToken.None);
            var sends = sessionIds
                .Select((sessionId, index) => store.AppendAsync(
                    sessionId,
                    [new SessionMessage("user", $"turn-{round}-{index}")],
                    CancellationToken.None))
                .Take(perRound)
                .ToArray();

            var outcomes = await Task.WhenAll(sends);
            await listing;

            Assert.All(outcomes, appended => Assert.True(
                appended,
                "a turn was dropped because the sidebar was listing at the same time"));
        }

        for (var index = 0; index < perRound; index++)
        {
            var session = await store.GetAsync(sessionIds[index], CancellationToken.None);
            Assert.Equal(
                rounds,
                session!.Messages.Count(message => message.Content.StartsWith("turn-", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public async Task Legacy_json_without_source_metadata_still_loads()
    {
        var (store, paths) = CreateStore();
        const string sessionId = "11111111111111111111111111111111";
        Directory.CreateDirectory(paths.SessionDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(paths.SessionDirectory, $"{sessionId}.json"),
            """
            {
              "id": "11111111111111111111111111111111",
              "title": "Legacy",
              "created_at": "2026-08-24T00:00:00+00:00",
              "updated_at": "2026-08-24T00:00:00+00:00",
              "messages": [
                { "role": "user", "content": "old prompt" },
                { "role": "ai", "content": "old reply" }
              ]
            }
            """);

        var loaded = await store.GetAsync(sessionId, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.All(loaded!.Messages, message =>
        {
            Assert.Null(message.SourceMessageId);
            Assert.Null(message.SourceRunId);
        });
    }

    [Fact]
    public async Task AppendIfAbsent_is_idempotent_by_stable_source_message_id()
    {
        var (store, _) = CreateStore();
        var sessionId = (await store.CreateAsync(CancellationToken.None))!;
        var message = new SessionMessage(
            "user",
            "agent prompt",
            SourceMessageId: "evt_11111111111111111111111111111111",
            SourceRunId: "run_11111111111111111111111111111111");

        var appended = await store.AppendIfAbsentAsync(
            sessionId,
            message,
            CancellationToken.None);
        var replayed = await store.AppendIfAbsentAsync(
            sessionId,
            message,
            CancellationToken.None);
        var loaded = await store.GetAsync(sessionId, CancellationToken.None);

        Assert.Equal(SessionAppendOutcome.Appended, appended);
        Assert.Equal(SessionAppendOutcome.AlreadyPresent, replayed);
        Assert.Single(loaded!.Messages);
        Assert.Equal(message, loaded.Messages[0]);
    }

    [Fact]
    public async Task AppendIfAbsent_rejects_conflicting_content_for_the_same_source_id()
    {
        var (store, _) = CreateStore();
        var sessionId = (await store.CreateAsync(CancellationToken.None))!;
        var original = new SessionMessage(
            "ai",
            "first",
            "evt_22222222222222222222222222222222",
            "run_22222222222222222222222222222222");
        var conflict = original with { Content = "different" };
        await store.AppendIfAbsentAsync(sessionId, original, CancellationToken.None);

        var result = await store.AppendIfAbsentAsync(
            sessionId,
            conflict,
            CancellationToken.None);

        Assert.Equal(SessionAppendOutcome.InvalidMessage, result);
        Assert.Single((await store.GetAsync(sessionId, CancellationToken.None))!.Messages);
    }

    [Fact]
    public async Task AppendIfAbsent_rejects_oversized_message_content()
    {
        var (store, _) = CreateStore();
        var sessionId = (await store.CreateAsync(CancellationToken.None))!;
        var message = new SessionMessage(
            "user",
            new string('x', 1024 * 1024 + 1),
            "evt_oversized_message",
            "run_oversized_message");

        var result = await store.AppendIfAbsentAsync(sessionId, message, CancellationToken.None);

        Assert.Equal(SessionAppendOutcome.InvalidMessage, result);
        Assert.Empty((await store.GetAsync(sessionId, CancellationToken.None))!.Messages);
    }

    [Fact]
    public async Task Get_falls_back_to_default_for_an_oversized_persisted_title()
    {
        var (store, paths) = CreateStore();
        const string sessionId = "22222222222222222222222222222222";
        Directory.CreateDirectory(paths.SessionDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(paths.SessionDirectory, $"{sessionId}.json"),
            $$"""
            {
              "id": "{{sessionId}}",
              "title": "{{new string('x', 51)}}",
              "created_at": "2026-08-24T00:00:00+00:00",
              "updated_at": "2026-08-24T00:00:00+00:00",
              "messages": []
            }
            """);

        var loaded = await store.GetAsync(sessionId, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("Cuộc trò chuyện mới", loaded!.Title);
    }

    [Fact]
    public async Task Append_rejects_a_merged_session_document_that_exceeds_the_file_cap()
    {
        var (store, _) = CreateStore();
        var sessionId = (await store.CreateAsync(CancellationToken.None))!;
        var messages = Enumerable.Range(0, 17)
            .Select(index => new SessionMessage("user", new string((char)('a' + index % 26), 1024 * 1024)))
            .ToArray();

        Assert.False(await store.AppendAsync(sessionId, messages, CancellationToken.None));
        Assert.Empty((await store.GetAsync(sessionId, CancellationToken.None))!.Messages);
    }

    [Fact]
    public async Task Ordinary_append_cannot_overwrite_the_idempotency_ledger()
    {
        var (store, _) = CreateStore();
        var sessionId = (await store.CreateAsync(CancellationToken.None))!;
        var original = new SessionMessage(
            "ai",
            "first",
            "evt_ledger_owner",
            "run_ledger_owner");
        await store.AppendIfAbsentAsync(sessionId, original, CancellationToken.None);

        var appended = await store.AppendAsync(
            sessionId,
            [original with { Content = "conflicting ordinary append" }],
            CancellationToken.None);
        var replayed = await store.AppendIfAbsentAsync(
            sessionId,
            original,
            CancellationToken.None);
        var loaded = await store.GetAsync(sessionId, CancellationToken.None);

        Assert.False(appended);
        Assert.Equal(SessionAppendOutcome.AlreadyPresent, replayed);
        Assert.Equal([original], loaded!.Messages);
    }

    [Fact]
    public async Task Stable_source_dedupe_survives_message_retention_trimming()
    {
        var (store, _) = CreateStore();
        var sessionId = (await store.CreateAsync(CancellationToken.None))!;
        var original = new SessionMessage(
            "user",
            "old agent prompt",
            "evt_33333333333333333333333333333333",
            "run_33333333333333333333333333333333");
        await store.AppendIfAbsentAsync(sessionId, original, CancellationToken.None);
        await store.AppendAsync(
            sessionId,
            Enumerable.Range(0, 201)
                .Select(index => new SessionMessage("user", $"ordinary-{index}"))
                .ToArray(),
            CancellationToken.None);

        var replayed = await store.AppendIfAbsentAsync(
            sessionId,
            original,
            CancellationToken.None);
        var conflict = await store.AppendIfAbsentAsync(
            sessionId,
            original with { Content = "changed" },
            CancellationToken.None);
        var loaded = await store.GetAsync(sessionId, CancellationToken.None);

        Assert.Equal(SessionAppendOutcome.AlreadyPresent, replayed);
        Assert.Equal(SessionAppendOutcome.InvalidMessage, conflict);
        Assert.Equal(200, loaded!.Messages.Count);
        Assert.DoesNotContain(loaded.Messages, message => message == original);
    }

    [Fact]
    public async Task Append_stamps_agent_activity_only_for_ai_messages()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));
        var (store, _) = CreateStore(clock);
        var sessionId = (await store.CreateAsync(CancellationToken.None))!;

        await store.AppendAsync(sessionId, [new SessionMessage("user", "hello")], CancellationToken.None);
        var afterUser = await store.GetAsync(sessionId, CancellationToken.None);
        Assert.NotNull(afterUser);
        Assert.Null(afterUser!.LastAgentActivityAt);

        clock.Advance(TimeSpan.FromMinutes(5));
        await store.AppendAsync(sessionId, [new SessionMessage("ai", "hi")], CancellationToken.None);
        var afterAi = await store.GetAsync(sessionId, CancellationToken.None);
        Assert.NotNull(afterAi);
        Assert.Equal(clock.GetUtcNow(), afterAi!.LastAgentActivityAt);

        var summaries = await store.ListAsync(CancellationToken.None);
        Assert.Equal(afterAi.LastAgentActivityAt, Assert.Single(summaries).LastAgentActivityAt);
    }

    [Fact]
    public async Task Legacy_json_without_agent_activity_loads_with_null_stamp()
    {
        var (store, paths) = CreateStore();
        const string sessionId = "22222222222222222222222222222222";
        Directory.CreateDirectory(paths.SessionDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(paths.SessionDirectory, $"{sessionId}.json"),
            """
            {
              "id": "22222222222222222222222222222222",
              "title": "Legacy",
              "created_at": "2026-08-24T00:00:00+00:00",
              "updated_at": "2026-08-24T00:00:00+00:00",
              "messages": [
                { "role": "user", "content": "old prompt" },
                { "role": "ai", "content": "old reply" }
              ]
            }
            """);

        var loaded = await store.GetAsync(sessionId, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Null(loaded!.LastAgentActivityAt);
    }

    private static (SessionStore Store, DataPaths Paths) CreateStore(TimeProvider? clock = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-session-tests", Guid.NewGuid().ToString("N"));
        var paths = new DataPaths(Path.Combine(root, "settings.json"),
            Path.Combine(root, "sessions"),
            Path.Combine(root, "activity.jsonl"));
        return (new SessionStore(paths, clock), paths);
    }

    private static string SessionJson(string id, string title) =>
        JsonSerializer.Serialize(new
        {
            id,
            title,
            created_at = DateTimeOffset.UnixEpoch,
            updated_at = DateTimeOffset.UnixEpoch,
            messages = Array.Empty<object>(),
        });

    private sealed class MutableTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset _current = current;

        public override DateTimeOffset GetUtcNow() => _current;

        public void Advance(TimeSpan duration) => _current += duration;
    }
}
