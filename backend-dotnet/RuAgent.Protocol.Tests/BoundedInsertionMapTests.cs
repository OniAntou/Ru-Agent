using Xunit;

namespace RuAgent.Protocol.Tests;

/// <summary>
/// Regression for the bounded row maps in the transcript: eviction must drop the
/// oldest inserted entry, never the entry that was just added. A Dictionary
/// reuses a freed slot, so <c>Remove(Keys.First())</c> commonly removed the
/// newest row as soon as a run produced more than the cap.
/// </summary>
public sealed class BoundedInsertionMapTests
{
    [Fact]
    public void Full_map_evicts_the_oldest_entry_and_keeps_the_newest()
    {
        var map = new BoundedInsertionMap<string, int>(64, StringComparer.Ordinal);
        for (var index = 1; index <= 70; index++)
        {
            map["a" + index] = index;
        }

        Assert.Equal(64, map.Count);
        Assert.False(map.TryGetValue("a1", out _));
        Assert.False(map.TryGetValue("a6", out _));
        Assert.True(map.TryGetValue("a7", out var seventh));
        Assert.Equal(7, seventh);
        Assert.True(map.TryGetValue("a69", out _));
        Assert.True(map.TryGetValue("a70", out var seventieth));
        Assert.Equal(70, seventieth);
        Assert.Equal("a70", map.Keys[^1]);
    }

    [Fact]
    public void Updating_an_existing_key_keeps_its_position()
    {
        var map = new BoundedInsertionMap<string, int>(3, StringComparer.Ordinal);
        map["first"] = 1;
        map["second"] = 2;
        map["first"] = 11;
        map["third"] = 3;

        Assert.Equal(3, map.Count);
        Assert.Equal(new[] { "first", "second", "third" }, map.Keys);
        Assert.Equal(11, map["first"]);

        // The fourth insertion evicts "first" only because it was inserted first.
        map["fourth"] = 4;
        Assert.DoesNotContain("first", map.Keys);
        Assert.Contains("fourth", map.Keys);
    }

    [Fact]
    public void Keys_is_a_snapshot_so_removal_during_enumeration_is_safe()
    {
        var map = new BoundedInsertionMap<string, int>(8, StringComparer.Ordinal);
        map["kept"] = 1;
        map["stale"] = 2;

        foreach (var key in map.Keys)
        {
            if (key == "stale")
            {
                map.Remove(key);
            }
        }

        Assert.Equal(new[] { "kept" }, map.Keys);
    }
}
