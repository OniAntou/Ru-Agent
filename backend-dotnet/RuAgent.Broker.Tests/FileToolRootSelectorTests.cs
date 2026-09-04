using RuAgent.Broker.Tools.Files;
using RuAgent.Broker.Tools.Paths;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class FileToolRootSelectorTests : IDisposable
{
    private readonly string _root;
    private readonly string _leaseRoot;
    private readonly string _granted;
    private readonly CanonicalPathResolver _leaseResolver;

    public FileToolRootSelectorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ru-rootselector-" + Guid.NewGuid().ToString("N"));
        _leaseRoot = Path.Combine(_root, "workspace");
        _granted = Path.Combine(_root, "shared");
        Directory.CreateDirectory(_leaseRoot);
        Directory.CreateDirectory(_granted);
        _leaseResolver = new CanonicalPathResolver(_leaseRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Inside_lease_root_selects_lease_root()
    {
        var selected = FileToolRootSelector.SelectRoot(
            _leaseResolver,
            _leaseResolver.CanonicalRoot,
            Array.Empty<string>(),
            Path.Combine(_leaseRoot, "notes", "a.txt"));

        Assert.Equal(_leaseResolver.CanonicalRoot, selected);
    }

    [Fact]
    public void Relative_path_selects_lease_root()
    {
        var selected = FileToolRootSelector.SelectRoot(
            _leaseResolver,
            _leaseResolver.CanonicalRoot,
            Array.Empty<string>(),
            Path.Combine("notes", "a.txt"));

        Assert.Equal(_leaseResolver.CanonicalRoot, selected);
    }

    [Fact]
    public void Outside_with_grant_selects_granted_directory()
    {
        var target = Path.Combine(_granted, "child", "x.txt");
        var selected = FileToolRootSelector.SelectRoot(
            _leaseResolver,
            _leaseResolver.CanonicalRoot,
            new[] { _granted },
            target);

        Assert.Equal(
            Path.GetFullPath(_granted).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            selected.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    [Fact]
    public void Outside_without_grant_throws_outside_workspace()
    {
        var error = Assert.Throws<UnauthorizedAccessException>(() => FileToolRootSelector.SelectRoot(
            _leaseResolver,
            _leaseResolver.CanonicalRoot,
            Array.Empty<string>(),
            Path.Combine(_granted, "x.txt")));

        Assert.Equal("outside_workspace", error.Message);
    }

    [Fact]
    public void Sibling_prefix_does_not_match_grant()
    {
        var sibling = _granted + "-sibling";
        Directory.CreateDirectory(sibling);
        Assert.Throws<UnauthorizedAccessException>(() => FileToolRootSelector.SelectRoot(
            _leaseResolver,
            _leaseResolver.CanonicalRoot,
            new[] { _granted },
            Path.Combine(sibling, "x.txt")));
    }

    [Fact]
    public void Longest_grant_wins()
    {
        var nested = Path.Combine(_granted, "nested");
        Directory.CreateDirectory(nested);
        var selected = FileToolRootSelector.SelectRoot(
            _leaseResolver,
            _leaseResolver.CanonicalRoot,
            new[] { _granted, nested },
            Path.Combine(nested, "x.txt"));

        Assert.EndsWith("nested", selected.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }
}
