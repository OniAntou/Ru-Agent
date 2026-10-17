using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class WorkspaceTrustMemoryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "RuAppTests",
        $"ws-trust-memory-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Remember_then_changed_identity_is_a_re_trust()
    {
        var store = new WorkspaceTrustMemoryStore(Path.Combine(_root, "workspace-trust-memory.json"));

        Assert.False(store.IsReTrust(@"D:\Work\LLM", "hash-a"));
        Assert.True(store.Remember(@"D:\Work\LLM\", "hash-a"));
        Assert.False(store.IsReTrust(@"D:\Work\LLM", "hash-a"));
        Assert.True(store.IsReTrust(@"d:\work\llm", "hash-b"));
        Assert.False(store.IsReTrust(@"D:\Other", "hash-b"));
    }

    [Fact]
    public void Missing_paths_and_hashes_are_never_a_re_trust()
    {
        var store = new WorkspaceTrustMemoryStore(Path.Combine(_root, "workspace-trust-memory.json"));

        Assert.False(store.IsReTrust(@"D:\Work\LLM", null));
        Assert.False(store.IsReTrust(@"D:\Work\LLM", "  "));
        Assert.False(store.IsReTrust("", "hash-a"));
        Assert.False(store.Remember(@"D:\Work\LLM", "  "));
        Assert.False(store.Remember("", "hash-a"));
    }

    [Fact]
    public void Memory_survives_reload_and_a_corrupt_file_reads_as_empty()
    {
        var path = Path.Combine(_root, "workspace-trust-memory.json");
        var store = new WorkspaceTrustMemoryStore(path);
        Assert.True(store.Remember(@"D:\A", "hash-a"));
        Assert.True(store.Remember(@"D:\B", "hash-b"));

        var reopened = new WorkspaceTrustMemoryStore(path);
        Assert.False(reopened.IsReTrust(@"D:\A", "hash-a"));
        Assert.True(reopened.IsReTrust(@"D:\A", "hash-changed"));
        Assert.False(reopened.IsReTrust(@"D:\B", "hash-b"));

        File.WriteAllText(path, "{oops");
        var corrupt = new WorkspaceTrustMemoryStore(path);
        Assert.False(corrupt.IsReTrust(@"D:\A", "hash-changed"));
        // A corrupt file reads as empty and the next approval rewrites it.
        Assert.True(corrupt.Remember(@"D:\A", "hash-a"));
        Assert.False(corrupt.IsReTrust(@"D:\A", "hash-a"));
    }

    [Fact]
    public void Remember_replaces_the_hash_for_the_same_path()
    {
        var store = new WorkspaceTrustMemoryStore(Path.Combine(_root, "workspace-trust-memory.json"));
        Assert.True(store.Remember(@"D:\A", "hash-1"));
        Assert.True(store.Remember(@"D:\A\", "hash-2"));

        Assert.False(store.IsReTrust(@"D:\A", "hash-2"));
        Assert.True(store.IsReTrust(@"D:\A", "hash-1"));
    }

    [Fact]
    public void Re_trust_copy_names_the_identity_change_and_stays_one_click()
    {
        var first = WorkspaceTrustPromptPolicy.For(isReTrust: false);
        var again = WorkspaceTrustPromptPolicy.For(isReTrust: true);

        Assert.Equal("Trust this workspace?", first.Title);
        Assert.Equal("Approve", first.PrimaryButtonText);
        Assert.Equal("Trust this workspace again?", again.Title);
        Assert.Equal("Trust again", again.PrimaryButtonText);
        Assert.Contains("identity changed", again.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("moved, recreated", again.Content, StringComparison.OrdinalIgnoreCase);
    }
}
