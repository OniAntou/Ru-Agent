using System.Text.RegularExpressions;
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

    private static (SessionStore Store, DataPaths Paths) CreateStore(TimeProvider? clock = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-session-tests", Guid.NewGuid().ToString("N"));
        var paths = new DataPaths(
            Path.Combine(root, "memory.db"),
            Path.Combine(root, "memory_texts.json"),
            Path.Combine(root, "settings.json"),
            Path.Combine(root, "history.json"),
            Path.Combine(root, "sessions"),
            Path.Combine(root, "activity.jsonl"));
        return (new SessionStore(paths, clock), paths);
    }

    private sealed class MutableTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset _current = current;

        public override DateTimeOffset GetUtcNow() => _current;

        public void Advance(TimeSpan duration) => _current += duration;
    }
}
