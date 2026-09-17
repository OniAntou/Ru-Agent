using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class WorkspaceGroupPreferenceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "RuAppTests",
        $"ws-groups-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Roundtrips_expanded_flags()
    {
        var path = Path.Combine(_root, "workspace-groups.json");
        var store = new WorkspaceGroupPreferenceStore(path);
        store.SetExpanded("ws_a", false);

        var reloaded = new WorkspaceGroupPreferenceStore(path);

        Assert.False(reloaded.IsExpanded("ws_a", defaultExpanded: true));
        Assert.True(reloaded.IsExpanded("ws_b", defaultExpanded: true));
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults()
    {
        var path = Path.Combine(_root, "workspace-groups.json");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path, "{oops");

        var store = new WorkspaceGroupPreferenceStore(path);

        Assert.True(store.IsExpanded("ws_a", defaultExpanded: true));
    }
}
