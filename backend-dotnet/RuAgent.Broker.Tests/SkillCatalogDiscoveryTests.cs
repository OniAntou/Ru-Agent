using RuAgent.Broker.Skills;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class SkillCatalogDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-skill-discovery-" + Guid.NewGuid().ToString("N"));
    private readonly string _library;

    public SkillCatalogDiscoveryTests()
    {
        Directory.CreateDirectory(_root);
        _library = Directory.CreateDirectory(Path.Combine(_root, "library")).FullName;
    }

    [Fact]
    public void Grouped_packages_are_discovered_loaded_and_keep_their_resource_base()
    {
        var path = Package("team/release", "Release workflow");
        var catalog = Catalog();
        var item = Assert.Single(catalog.Discover().Items);
        Assert.Equal("release", item.Id);
        Assert.Equal(path, item.SourcePath);
        Assert.Equal(Path.GetDirectoryName(path), item.ResourceBase);
        Assert.Equal("Release workflow", catalog.LoadBody("release", item.Revision)!.Body);
    }

    [Fact]
    public void Package_is_a_leaf_and_hidden_or_dependency_directories_are_not_scanned()
    {
        Package("release", "Outer");
        Package("release/nested", "Not a separate package");
        Package(".hidden/secret", "Hidden");
        Package("node_modules/dependency", "Dependency");
        Assert.Equal("release", Assert.Single(Catalog().Discover().Items).Id);
    }

    [Fact]
    public void Warm_catalog_observes_nested_addition_edit_and_removal()
    {
        Package("team/first", "First");
        var catalog = Catalog();
        Assert.Single(catalog.Discover().Items);
        var second = Package("team/second", "Second");
        Assert.Equal(2, catalog.Discover().Items.Count);
        File.WriteAllText(second, "---\nname: Updated\ndescription: Changed nested metadata\n---\nSecond body.");
        Assert.Equal("Updated", catalog.Discover().Items.Single(x => x.Id == "second").Name);
        File.Delete(second);
        Assert.Single(catalog.Discover().Items);
    }

    [Fact]
    public void Grouped_duplicate_ids_use_deterministic_path_order_and_root_precedence()
    {
        Package("z-team/shared", "Later");
        Package("a-team/shared", "First");
        var other = Directory.CreateDirectory(Path.Combine(_root, "other")).FullName;
        var otherPackage = Directory.CreateDirectory(Path.Combine(other, "shared")).FullName;
        File.WriteAllText(Path.Combine(otherPackage, "SKILL.md"), "---\nname: Other\ndescription: Other\n---\nOther");
        var catalog = Catalog([_library, other]);
        Assert.Equal("First", catalog.LoadBody("shared")!.Body);
        var result = catalog.Discover();
        Assert.Single(result.Items);
        Assert.Contains(result.Diagnostics, x => x.Code == "duplicate");
    }

    [Fact]
    public void Direct_package_precedes_grouped_package_with_the_same_id()
    {
        Package("a-team/shared", "Grouped");
        var directPath = Package("shared", "Direct");

        var catalog = Catalog();

        Assert.Equal("Direct", catalog.LoadBody("shared")!.Body);
        Assert.Equal(directPath, Assert.Single(catalog.Discover().Items).SourcePath);
    }

    [Fact]
    public void Reparse_group_directory_is_not_traversed()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        var package = Directory.CreateDirectory(Path.Combine(outside, "secret-skill"));
        File.WriteAllText(Path.Combine(package.FullName, "SKILL.md"),
            "---\nname: Secret\ndescription: Outside package\n---\nsecret body");
        var link = Path.Combine(_library, "team-link");
        try
        {
            try { Directory.CreateSymbolicLink(link, outside); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var result = Catalog().Discover();

            Assert.Empty(result.Items);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "reparse_point");
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link, recursive: false);
        }
    }

    [Fact]
    public void Depth_bound_is_visible_and_does_not_hide_shallower_packages()
    {
        Package("a/b/c/d/e/f/g/too-deep", "Too deep");
        Package("shallow", "Visible");
        var result = Catalog().Discover();
        Assert.Equal("shallow", Assert.Single(result.Items).Id);
        Assert.True(result.Truncated);
        Assert.Contains(result.Diagnostics, x => x.Code == "depth_limit");
    }

    private SkillCatalog Catalog(IReadOnlyList<string>? roots = null) =>
        new(_root, Path.Combine(_root, "preferences.json"), roots ?? [_library]);

    private string Package(string relativePath, string body)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_library, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var file = Path.Combine(directory.FullName, "SKILL.md");
        File.WriteAllText(file, $"---\nname: {directory.Name}\ndescription: Workflow for {directory.Name}\n---\n{body}");
        return file;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
