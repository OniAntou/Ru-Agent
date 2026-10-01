using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class FeedDensityStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-density-" + Guid.NewGuid().ToString("N"));

    public FeedDensityStoreTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    [Fact]
    public void Missing_file_means_roomy()
    {
        Assert.False(new FeedDensityStore(Path.Combine(_root, "feed-density.json")).GetCompact());
    }

    [Fact]
    public void Roundtrip_persists_compact()
    {
        var path = Path.Combine(_root, "feed-density.json");
        new FeedDensityStore(path).SetCompact(true);

        Assert.True(new FeedDensityStore(path).GetCompact());
    }

    [Fact]
    public void Corrupt_file_means_roomy()
    {
        var path = Path.Combine(_root, "feed-density.json");
        File.WriteAllText(path, "{not json");

        Assert.False(new FeedDensityStore(path).GetCompact());
    }

    [Fact]
    public void Density_instance_toggles_and_notifies()
    {
        var path = Path.Combine(_root, "feed-density.json");
        var density = new FeedDensity(new FeedDensityStore(path));
        bool notified = false;
        density.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(FeedDensity.IsCompact))
            {
                notified = true;
            }
        };

        Assert.False(density.IsCompact);
        density.IsCompact = true;

        Assert.True(notified);
        Assert.True(new FeedDensityStore(path).GetCompact());
    }
}
