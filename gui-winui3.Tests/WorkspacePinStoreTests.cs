using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class WorkspacePinStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "RuAppTests",
        $"ws-pins-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Pin_adds_canonical_path_once_and_unpin_removes_it()
    {
        var store = new WorkspacePinStore(Path.Combine(_root, "workspace-pinned.json"));

        Assert.True(store.Pin(@"D:\Work\LLM\"));
        Assert.False(store.Pin(@"d:\work\llm"));
        Assert.Equal([@"D:\Work\LLM"], store.PinnedPaths);

        Assert.True(store.Unpin(@"D:\WORK\LLM"));
        Assert.Empty(store.PinnedPaths);
    }

    [Fact]
    public void Pins_survive_reload_and_corrupt_file_starts_empty()
    {
        var path = Path.Combine(_root, "workspace-pinned.json");
        var store = new WorkspacePinStore(path);
        store.Pin(@"D:\A");
        store.Pin(@"D:\B");

        Assert.Equal([@"D:\A", @"D:\B"], new WorkspacePinStore(path).PinnedPaths);

        File.WriteAllText(path, "{oops");
        Assert.Empty(new WorkspacePinStore(path).PinnedPaths);
    }

    [Fact]
    public void Session_map_set_lookup_and_remove()
    {
        var store = new SessionWorkspaceMapStore(Path.Combine(_root, "session-workspaces.json"));

        Assert.Null(store.Lookup("s1"));
        store.Set("s1", @"D:\Work\LLM");
        Assert.Equal(@"D:\Work\LLM", store.Lookup("s1"));

        store.Remove("s1");
        Assert.Null(store.Lookup("s1"));
    }
}
