using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RuBackend.Business;
using RuBackend.Business.Sessions;
using Xunit;

namespace RuBackend.Tests.Business;

public sealed class SessionImportCancellationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ru-import-cancel-").FullName;
    private readonly SessionStore _store;
    private readonly DataPaths _paths;
    private readonly ILoggerFactory _loggers = LoggerFactory.Create(_ => { });

    public SessionImportCancellationTests()
    {
        _paths = new DataPaths(Path.Combine(_root, "settings.json"),
            Path.Combine(_root, "sessions"), Path.Combine(_root, "activity.jsonl"));
        _store = new SessionStore(_paths);
    }

    [Fact]
    public async Task Cancellation_after_create_removes_the_empty_session_and_propagates()
    {
        using var cancellation = new CancellationTokenSource();
        var intercepted = new InterceptedStore(_store, cancellation, cancelAfterCreate: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImportAsync(intercepted, cancellation.Token));

        Assert.Empty(await _store.ListAsync(CancellationToken.None));
        Assert.Equal(1, intercepted.DeleteCalls);
    }

    [Fact]
    public async Task Append_failure_removes_the_empty_session()
    {
        using var cancellation = new CancellationTokenSource();
        var intercepted = new InterceptedStore(_store, cancellation, failAppend: true);

        var result = await ImportAsync(intercepted, cancellation.Token);

        Assert.Contains("error", JsonSerializer.Serialize(((IValueHttpResult)result).Value));
        Assert.Empty(await _store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_after_append_commit_preserves_the_imported_messages()
    {
        using var cancellation = new CancellationTokenSource();
        var intercepted = new InterceptedStore(_store, cancellation, cancelAfterAppend: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImportAsync(intercepted, cancellation.Token));

        var session = Assert.Single(await _store.ListAsync(CancellationToken.None));
        Assert.Equal(1, session.MessageCount);
        Assert.Equal(0, intercepted.DeleteCalls);
    }

    [Fact]
    public async Task Cleanup_failure_does_not_replace_the_original_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var intercepted = new InterceptedStore(_store, cancellation,
            cancelAfterCreate: true, failDelete: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImportAsync(intercepted, cancellation.Token));

        Assert.Equal(1, intercepted.DeleteCalls);
    }

    [Fact]
    public async Task Empty_cleanup_preserves_messages_committed_by_another_store()
    {
        var id = (await _store.CreateAsync(CancellationToken.None))!;
        Assert.Empty((await _store.GetAsync(id, CancellationToken.None))!.Messages);
        var otherStore = new SessionStore(_paths);
        Assert.True(await otherStore.AppendAsync(id, [new SessionMessage("user", "Committed message")],
            CancellationToken.None));

        Assert.True(await ((ISessionStore)_store).CleanupEmptyAsync(id, CancellationToken.None));

        var message = Assert.Single((await _store.GetAsync(id, CancellationToken.None))!.Messages);
        Assert.Equal("Committed message", message.Content);
    }

    private async Task<IResult> ImportAsync(ISessionStore sessions, CancellationToken cancellationToken)
    {
        var transcript = JsonSerializer.Serialize(new
        {
            type = "user", sessionId = "synthetic-import",
            message = new { role = "user", content = new[] { new { type = "text", text = "Imported prompt" } } },
        }) + "\n";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { harness = "claude-code", content = transcript });
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        var method = typeof(SessionInterchangeEndpoints).GetMethod("ImportSessionAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return await (Task<IResult>)method.Invoke(null, [context, sessions, _loggers, cancellationToken])!;
    }

    public void Dispose()
    {
        _loggers.Dispose();
        SqliteTestPools.ClearHarnessDatabase(_paths.AgentHarnessDatabase);
        Directory.Delete(_root, recursive: true);
    }

    private sealed class InterceptedStore(SessionStore actual, CancellationTokenSource cancellation,
        bool cancelAfterCreate = false, bool cancelAfterAppend = false,
        bool failAppend = false, bool failDelete = false) : ISessionStore
    {
        public int DeleteCalls { get; private set; }
        public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken ct) => actual.ListAsync(ct);
        public Task<SessionDocument?> GetAsync(string id, CancellationToken ct) => actual.GetAsync(id, ct);
        public Task<string?> CreateAsync(CancellationToken ct) => CreateForClientAsync(null, ct);
        public async Task<string?> CreateForClientAsync(string? source, CancellationToken ct)
        {
            var id = await actual.CreateForClientAsync(source, ct);
            if (cancelAfterCreate) cancellation.Cancel();
            return id;
        }
        public Task<bool> DeleteAsync(string id, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            DeleteCalls++;
            if (failDelete) throw new IOException("synthetic cleanup failure");
            return actual.DeleteAsync(id, ct);
        }
        public async Task<bool> CleanupEmptyAsync(string id, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var document = await actual.GetAsync(id, ct);
            if (document?.Messages.Count == 0)
            {
                DeleteCalls++;
                if (failDelete) throw new IOException("synthetic cleanup failure");
            }
            return await ((ISessionStore)actual).CleanupEmptyAsync(id, ct);
        }
        public async Task<bool> AppendAsync(string id, IReadOnlyList<SessionMessage> messages, CancellationToken ct)
        {
            if (failAppend) throw new IOException("synthetic append failure");
            var appended = await actual.AppendAsync(id, messages, ct);
            if (cancelAfterAppend) cancellation.Cancel();
            return appended;
        }
        public Task<SessionAppendOutcome> AppendIfAbsentAsync(string id, SessionMessage message, CancellationToken ct) =>
            actual.AppendIfAbsentAsync(id, message, ct);
        public Task<IReadOnlyList<ConversationTurn>> GetContextTurnsAsync(string id, int limit, CancellationToken ct) =>
            actual.GetContextTurnsAsync(id, limit, ct);
    }
}
