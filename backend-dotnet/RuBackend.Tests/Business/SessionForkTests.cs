using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RuAgent.Client;
using RuBackend.Business.Sessions;
using RuBackend.Business;
using Xunit;

namespace RuBackend.Tests.Business;

public sealed class SessionForkTests
{
    [Theory]
    [InlineData(24)]
    [InlineData(220)]
    public async Task Paged_source_forks_the_absolute_prefix_including_older_messages(int keep)
    {
        using var data = TemporaryBusinessData.Create(); var store = new SessionStore(data.Paths);
        var id = (await store.CreateAsync(default))!;
        var messages = Enumerable.Range(0, 220).Select(i => new SessionMessage(i % 2 == 0 ? "user" : "ai", "row " + i)).ToArray();
        Assert.True(await store.AppendAsync(id, messages, default));
        var source = (await store.GetAsync(id, default))!;
        Assert.Equal(20, source.MessageOffset); Assert.Equal(220, source.TotalMessageCount);
        Assert.Equal("invalid_fork_boundary", (await store.ForkAsync(id, 0, SessionStore.HistoryRevision(source), default)).ErrorCode);
        var result = await store.ForkAsync(id, keep, SessionStore.HistoryRevision(source), default);
        Assert.Null(result.ErrorCode);
        var child = Assert.IsType<SessionDocument>(result.Document);
        Assert.Equal(keep, child.TotalMessageCount);
        Assert.Equal(messages.Take(keep).TakeLast(200), child.Messages);
        var reopened = new SessionStore(data.Paths);
        Assert.Equal(child.Messages, (await reopened.GetAsync(child.Id, default))!.Messages);
        Assert.Equal(220, (await reopened.GetAsync(id, default))!.TotalMessageCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Branch_is_durable_and_keeps_original_and_only_selected_prefix(int keep)
    {
        using var data = TemporaryBusinessData.Create();
        var store = new SessionStore(data.Paths);
        var id = (await store.CreateForClientAsync("gui", default))!;
        Assert.True(await store.AppendAsync(id, [new("user", "first"), new("ai", "answer"), new("user", "next"), new("ai", "second")], default));
        var original = (await store.GetAsync(id, default))!;
        var result = await store.ForkAsync(id, keep, SessionStore.HistoryRevision(original), default);
        Assert.Null(result.ErrorCode);
        var child = Assert.IsType<SessionDocument>(result.Document);
        Assert.NotEqual(id, child.Id); Assert.Equal(id, child.ParentSessionId); Assert.Equal("cli", child.ClientSource);
        Assert.Null(child.LastAgentActivityAt);
        Assert.Equal(original.Messages.Take(keep), child.Messages);
        var reopened = new SessionStore(data.Paths);
        Assert.Equal(child.Messages, (await reopened.GetAsync(child.Id, default))!.Messages);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(await reopened.GetAsync(id, default)));
        Assert.True(await reopened.AppendAsync(child.Id, [new("user", "different approach")], default));
        Assert.Equal(4, (await reopened.GetAsync(id, default))!.Messages.Count);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(99)]
    public async Task Invalid_or_partial_turn_boundary_creates_nothing(int keep)
    {
        using var data = TemporaryBusinessData.Create(); var store = new SessionStore(data.Paths);
        var id = (await store.CreateAsync(default))!;
        await store.AppendAsync(id, [new("user", "a"), new("ai", "b"), new("user", "c"), new("ai", "d")], default);
        var source = (await store.GetAsync(id, default))!;
        Assert.Equal("invalid_fork_boundary", (await store.ForkAsync(id, keep, SessionStore.HistoryRevision(source), default)).ErrorCode);
        Assert.Single(await store.ListAsync(default));
    }

    [Fact]
    public async Task Stale_revision_refuses_without_creating_or_truncating()
    {
        using var data = TemporaryBusinessData.Create(); var store = new SessionStore(data.Paths);
        var id = (await store.CreateAsync(default))!;
        await store.AppendAsync(id, [new("user", "a"), new("ai", "b")], default);
        var revision = SessionStore.HistoryRevision((await store.GetAsync(id, default))!);
        await store.AppendAsync(id, [new("user", "later")], default);
        Assert.Equal("history_stale", (await store.ForkAsync(id, 0, revision, default)).ErrorCode);
        Assert.Single(await store.ListAsync(default)); Assert.Equal(3, (await store.GetAsync(id, default))!.Messages.Count);
    }

    [Fact]
    public async Task Branch_copies_only_retained_source_ledger_without_adopting_old_runs()
    {
        using var data = TemporaryBusinessData.Create(); var store = new SessionStore(data.Paths);
        var id = (await store.CreateAsync(default))!;
        var first = new SessionMessage("user", "first", "msg_first", "run_original");
        var answer = new SessionMessage("ai", "answer", "msg_answer", "run_original");
        var later = new SessionMessage("user", "later", "msg_later", "run_later");
        Assert.Equal(SessionAppendOutcome.Appended, await store.AppendIfAbsentAsync(id, first, default));
        Assert.Equal(SessionAppendOutcome.Appended, await store.AppendIfAbsentAsync(id, answer, default));
        Assert.Equal(SessionAppendOutcome.Appended, await store.AppendIfAbsentAsync(id, later, default));
        var source = (await store.GetAsync(id, default))!;
        var child = (await store.ForkAsync(id, 2, SessionStore.HistoryRevision(source), default)).Document!;
        Assert.Equal(new[] { "msg_answer", "msg_first" }, child.SourceMessageLedger!.Keys.Order());
        Assert.Equal("msg_answer", child.ForkedFromMessageId);
        var reopened = new SessionStore(data.Paths);
        Assert.Equal(SessionAppendOutcome.AlreadyPresent, await reopened.AppendIfAbsentAsync(child.Id, first, default));
        Assert.Equal(SessionAppendOutcome.Appended, await reopened.AppendIfAbsentAsync(child.Id, later, default));
        Assert.Equal(3, (await reopened.GetAsync(id, default))!.Messages.Count);
    }

    [Fact]
    public async Task Cancelled_fork_creates_nothing()
    {
        using var data = TemporaryBusinessData.Create(); var store = new SessionStore(data.Paths);
        var id = (await store.CreateAsync(default))!;
        var source = (await store.GetAsync(id, default))!;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ForkAsync(id, 0, SessionStore.HistoryRevision(source), cancellation.Token));
        Assert.Single(await store.ListAsync(default));
    }

    [Fact]
    public async Task Host_and_client_use_revision_and_return_named_stale_error()
    {
        using var factory = new BackendWebApplicationFactory(); using var http = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ISessionStore>();
        var id = (await store.CreateAsync(default))!;
        await store.AppendAsync(id, [new("user", "original"), new("ai", "answer")], default);
        var host = new PublicHostSession(http.BaseAddress!, "test-secret", null!);
        var client = new SessionApiClient(host, http);
        var source = await client.GetSessionAsync(id);
        var revision = source.GetProperty("session").GetProperty("history_revision").GetString()!;
        var fork = await client.ForkAsync(id, 0, revision);
        Assert.NotEqual(id, fork.GetProperty("session").GetProperty("id").GetString());
        Assert.Empty(fork.GetProperty("session").GetProperty("messages").EnumerateArray());
        await store.AppendAsync(id, [new("user", "changed")], default);
        var error = await Assert.ThrowsAsync<AgentApiException>(() => client.ForkAsync(id, 0, revision));
        Assert.Equal("history_stale", error.ErrorCode);
        Assert.Equal(2, (await store.ListAsync(default)).Count);
    }
    private sealed class TemporaryBusinessData : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "ru-fork-tests-" + Guid.NewGuid().ToString("N"));
        public DataPaths Paths { get; }
        private TemporaryBusinessData()
        {
            Directory.CreateDirectory(root);
            Paths = new DataPaths(Path.Combine(root, "settings.json"), Path.Combine(root, "sessions"), Path.Combine(root, "activity.jsonl"));
        }
        public static TemporaryBusinessData Create() => new();
        public void Dispose() { SqliteTestPools.ClearHarnessDatabase(Paths.AgentHarnessDatabase); Directory.Delete(root, true); }
    }

}
