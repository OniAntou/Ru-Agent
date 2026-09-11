using System.Text.RegularExpressions;
using RuBackend.Business;
using RuBackend.Business.Sessions;
using Xunit;

namespace RuBackend.Tests.Business;

public sealed class SessionBranchingTests
{
    [Fact]
    public async Task ForkAsync_CreatesBranchSession_WithSlicedMessages_AndSetsParent()
    {
        var (store, _) = CreateStore();
        var originalId = (await store.CreateAsync(CancellationToken.None))!;

        await store.AppendIfAbsentAsync(originalId, new("user", "prompt 1", "msg-1", "run-1"), CancellationToken.None);
        await store.AppendIfAbsentAsync(originalId, new("ai", "response 1", "msg-2", "run-1"), CancellationToken.None);
        await store.AppendIfAbsentAsync(originalId, new("user", "prompt 2", "msg-3", "run-2"), CancellationToken.None);
        await store.AppendIfAbsentAsync(originalId, new("ai", "response 2", "msg-4", "run-2"), CancellationToken.None);

        // Fork up to message index 1 (prompt 1 and response 1)
        var forked = await store.ForkAsync(originalId, 1, "Nhánh thử nghiệm", CancellationToken.None);

        Assert.NotNull(forked);
        Assert.NotEqual(originalId, forked!.Id);
        Assert.Equal("Nhánh thử nghiệm", forked.Title);
        Assert.Equal(originalId, forked.ParentSessionId);
        Assert.Equal(2, forked.Messages.Count);
        Assert.Equal("prompt 1", forked.Messages[0].Content);
        Assert.Equal("response 1", forked.Messages[1].Content);
        Assert.Equal("msg-2", forked.ForkedFromMessageId);

        // Original session remains intact with 4 messages
        var original = await store.GetAsync(originalId, CancellationToken.None);
        Assert.NotNull(original);
        Assert.Equal(4, original!.Messages.Count);

        // List includes both sessions with correct branch metadata
        var list = await store.ListAsync(CancellationToken.None);
        var branchSummary = list.FirstOrDefault(s => s.Id == forked.Id);
        Assert.NotNull(branchSummary);
        Assert.Equal(originalId, branchSummary!.ParentSessionId);
        Assert.Equal("msg-2", branchSummary.ForkedFromMessageId);
    }

    [Fact]
    public async Task RewindAsync_TrimsMessages_InPlace()
    {
        var (store, _) = CreateStore();
        var sessionId = (await store.CreateAsync(CancellationToken.None))!;

        var messages = new List<SessionMessage>
        {
            new("user", "prompt 1"),
            new("ai", "response 1"),
            new("user", "prompt 2"),
            new("ai", "response 2"),
        };
        await store.AppendAsync(sessionId, messages, CancellationToken.None);

        // Rewind to message index 1
        var rewound = await store.RewindAsync(sessionId, 1, CancellationToken.None);

        Assert.NotNull(rewound);
        Assert.Equal(sessionId, rewound!.Id);
        Assert.Equal(2, rewound.Messages.Count);
        Assert.Equal("prompt 1", rewound.Messages[0].Content);
        Assert.Equal("response 1", rewound.Messages[1].Content);

        var loaded = await store.GetAsync(sessionId, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Messages.Count);
    }

    [Fact]
    public async Task ForkAsync_InvalidSession_ReturnsNull()
    {
        var (store, _) = CreateStore();
        var result = await store.ForkAsync("non-existent", 0, null, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task RewindAsync_InvalidSession_ReturnsNull()
    {
        var (store, _) = CreateStore();
        var result = await store.RewindAsync("non-existent", 0, CancellationToken.None);
        Assert.Null(result);
    }

    private static (SessionStore Store, DataPaths Paths) CreateStore()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-session-branch-tests", Guid.NewGuid().ToString("N"));
        var paths = new DataPaths(Path.Combine(root, "settings.json"),
            Path.Combine(root, "sessions"),
            Path.Combine(root, "activity.jsonl"));
        return (new SessionStore(paths), paths);
    }
}
