using RuBackend.Business;
using RuBackend.Business.Sessions;
using Xunit;

namespace RuBackend.Tests.Business;

public sealed class SessionBranchingTests
{
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
