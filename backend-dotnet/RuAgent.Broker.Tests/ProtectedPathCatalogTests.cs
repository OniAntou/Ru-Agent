using RuAgent.Broker.Tools.Paths;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class ProtectedPathCatalogTests
{
    [Fact]
    public void Catalog_version_is_locked_at_1()
    {
        Assert.Equal("1", ProtectedPathCatalog.Version);
    }

    [Theory]
    [InlineData(@"C:\ws\repo\.git")]
    [InlineData(@"C:\ws\repo\.git\config")]
    [InlineData(@"C:\ws\repo\.git\objects\ab\cd1234")]
    [InlineData(@"C:\ws\repo\.env")]
    [InlineData(@"C:\ws\repo\.env.local")]
    [InlineData(@"C:\ws\repo\sub\.env.production")]
    public void Git_and_env_paths_are_protected(string path)
    {
        Assert.True(ProtectedPathCatalog.IsProtected(path));
    }

    [Theory]
    [InlineData(@"C:\Users\u\AppData\Local\Ru\agent_harness.db")]
    [InlineData(@"D:\data\Ru\agent_harness.db-wal")]
    [InlineData(@"C:\Users\u\AppData\Local\Ru\harness_key.dat")]
    [InlineData(@"C:\Users\u\AppData\Local\Ru\runtime_descriptor.json")]
    [InlineData(@"D:\tmp\sessions\broker.json")]
    public void Harness_database_key_and_descriptor_locations_are_protected(string path)
    {
        Assert.True(ProtectedPathCatalog.IsProtected(path));
    }

    [Fact]
    public void Env_file_named_without_prefix_is_not_protected()
    {
        Assert.False(ProtectedPathCatalog.IsProtected(@"C:\ws\repo\environment.txt"));
        Assert.False(ProtectedPathCatalog.IsProtected(@"C:\ws\repo\src\main.cpp"));
        Assert.False(ProtectedPathCatalog.IsProtected(@"C:\ws\repo\README.md"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_paths_are_not_classified_protected(string? path)
    {
        Assert.False(ProtectedPathCatalog.IsProtected(path!));
    }

    [Fact]
    public void Workspace_root_itself_is_not_protected_by_catalog_rules()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-protected-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.False(ProtectedPathCatalog.IsProtected(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
