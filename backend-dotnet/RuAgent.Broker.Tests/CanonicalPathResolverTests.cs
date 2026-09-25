using RuAgent.Broker.Tools.Paths;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class CanonicalPathResolverTests : IDisposable
{
    private readonly string _root;
    private readonly CanonicalPathResolver _resolver;

    public CanonicalPathResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ru-path-resolver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "a.txt"), "alpha");
        _resolver = new CanonicalPathResolver(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Relative_path_anchors_inside_workspace()
    {
        var resolved = _resolver.Resolve(@"src\a.txt");

        Assert.Equal(PathClassification.InsideWorkspace, resolved.Classification);
        Assert.False(resolved.IsAbsoluteInput);
        Assert.StartsWith(
            _resolver.CanonicalRoot,
            resolved.CanonicalPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.Matches("^[0-9a-f]{64}$", resolved.CanonicalIntentHash);
    }

    [Fact]
    public void Dot_path_resolves_to_workspace_root_itself()
    {
        var resolved = _resolver.Resolve(".");

        Assert.Equal(PathClassification.InsideWorkspace, resolved.Classification);
        Assert.Equal(
            _resolver.CanonicalRoot.TrimEnd(Path.DirectorySeparatorChar),
            resolved.CanonicalPath.TrimEnd(Path.DirectorySeparatorChar),
            ignoreCase: true,
            ignoreWhiteSpaceDifferences: false);
    }

    [Fact]
    public void Explicit_absolute_inside_stays_inside()
    {
        var absolute = Path.Combine(_root, "src", "a.txt");
        var resolved = _resolver.Resolve(absolute);

        Assert.Equal(PathClassification.InsideWorkspace, resolved.Classification);
        Assert.True(resolved.IsAbsoluteInput);
    }

    [Fact]
    public void Explicit_absolute_outside_is_outside_not_rejected()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ru-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            var resolved = _resolver.Resolve(outside);

            Assert.Equal(PathClassification.OutsideWorkspace, resolved.Classification);
            Assert.NotEqual(PathClassification.Protected, resolved.Classification);
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void Protected_target_is_typed_protected_not_rejected()
    {
        var gitDir = Path.Combine(_root, ".git");
        Directory.CreateDirectory(gitDir);
        try
        {
            var resolved = _resolver.Resolve(".git");

            Assert.Equal(PathClassification.Protected, resolved.Classification);
        }
        finally
        {
            Directory.Delete(gitDir, true);
        }
    }

    [Fact]
    public void Missing_create_target_under_workspace_is_inside()
    {
        var missing = Path.Combine(_root, "newdir", "created.md");
        var resolved = _resolver.Resolve(missing);

        Assert.False(File.Exists(resolved.CanonicalPath));
        Assert.Equal(PathClassification.InsideWorkspace, resolved.Classification);
    }

    [Fact]
    public void Parent_traversal_cannot_escape_without_absolute_input()
    {
        var resolved = _resolver.Resolve(@"..\..\outside.txt");

        Assert.Equal(PathClassification.OutsideWorkspace, resolved.Classification);
    }

    [Fact]
    public void Junction_escape_is_classified_outside_or_protected()
    {
        var escapeTarget = Path.Combine(Path.GetTempPath(), "ru-junction-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(escapeTarget);
        File.WriteAllText(Path.Combine(escapeTarget, "secret.txt"), "outside");
        var junction = Path.Combine(_root, "link");
        var created = false;
        try
        {
            created = TryCreateJunction(junction, escapeTarget);
            if (!created)
            {
                return;
            }

            var resolved = _resolver.Resolve(@"link\secret.txt");

            Assert.NotEqual(PathClassification.InsideWorkspace, resolved.Classification);
            Assert.True(
                resolved.IsOutsideWorkspace || resolved.IsProtected,
                "junction escape must not classify inside workspace");
        }
        finally
        {
            if (Directory.Exists(junction))
            {
                Directory.Delete(junction, recursive: false);
            }

            Directory.Delete(escapeTarget, true);
        }
    }

    [Fact]
    public void Reresolution_after_link_replacement_changes_canonical_intent()
    {
        var realDir = Path.Combine(_root, "real");
        Directory.CreateDirectory(realDir);
        File.WriteAllText(Path.Combine(realDir, "file.txt"), "content");
        var link = Path.Combine(_root, "alias");
        var otherDir = Path.Combine(_root, "other");
        Directory.CreateDirectory(otherDir);

        var created = TryCreateJunction(link, realDir);
        if (!created)
        {
            return;
        }

        try
        {
            var before = _resolver.Resolve(@"alias\file.txt");
            Assert.Equal(PathClassification.InsideWorkspace, before.Classification);

            Directory.Delete(link, recursive: false);
            created = TryCreateJunction(link, Path.Combine(Path.GetTempPath(), "ru-alias-escape-" + Guid.NewGuid().ToString("N")));
            if (!created)
            {
                return;
            }

            var after = _resolver.Resolve(@"alias\file.txt");
            Assert.True(CanonicalPathResolver.HasChanged(before, after));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link, recursive: false);
            }
        }
    }

    [Fact]
    public void Same_path_resolves_deterministically()
    {
        var first = _resolver.Resolve(@"src\a.txt");
        var second = _resolver.Resolve(@"src/a.txt");

        Assert.Equal(first.CanonicalPath, second.CanonicalPath);
        Assert.Equal(first.CanonicalIntentHash, second.CanonicalIntentHash);
        Assert.Equal(first.Classification, second.Classification);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_or_blank_input_throws(string input)
    {
        Assert.ThrowsAny<ArgumentException>(() => _resolver.Resolve(input));
    }

    [Theory]
    [InlineData("file.txt:stream")]
    [InlineData("file.txt::$DATA")]
    [InlineData("dir/file.txt:evil.exe")]
    [InlineData("C:\\workspace\\file.txt:stream")]
    [InlineData("\\\\?\\C:\\workspace\\file.txt:stream")]
    public void Alternate_data_stream_input_throws(string input)
    {
        var ex = Assert.Throws<ArgumentException>(() => _resolver.Resolve(input));
        Assert.Contains("Alternate data streams", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Broken_link_is_classified_by_its_target_not_its_location()
    {
        var missingTarget = Path.Combine(Path.GetTempPath(), "ru-broken-target-" + Guid.NewGuid().ToString("N"));
        var link = Path.Combine(_root, "broken");
        var created = TryCreateJunction(link, missingTarget);
        if (!created)
        {
            return;
        }

        try
        {
            var resolved = _resolver.Resolve(@"broken\child.txt");

            Assert.NotEqual(PathClassification.InsideWorkspace, resolved.Classification);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(missingTarget, "child.txt")),
                resolved.CanonicalPath,
                ignoreCase: true,
                ignoreWhiteSpaceDifferences: false);
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link, recursive: false);
            }
        }
    }

    [Fact]
    public void Broken_link_pointing_inside_the_workspace_stays_inside()
    {
        var insideTarget = Path.Combine(_root, "missingdir");
        var link = Path.Combine(_root, "broken-inside");
        var created = TryCreateJunction(link, insideTarget);
        if (!created)
        {
            return;
        }

        try
        {
            var resolved = _resolver.Resolve(@"broken-inside\child.txt");

            Assert.Equal(PathClassification.InsideWorkspace, resolved.Classification);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(insideTarget, "child.txt")),
                resolved.CanonicalPath,
                ignoreCase: true,
                ignoreWhiteSpaceDifferences: false);
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link, recursive: false);
            }
        }
    }

    [Fact]
    public void Broken_file_link_is_classified_by_its_target_not_its_location()
    {
        var missingTarget = Path.Combine(Path.GetTempPath(), "ru-broken-file-target-" + Guid.NewGuid().ToString("N"));
        var link = Path.Combine(_root, "broken-file.txt");
        var created = TryCreateFileLink(link, missingTarget);
        if (!created)
        {
            return;
        }

        try
        {
            var resolved = _resolver.Resolve(@"broken-file.txt");

            Assert.NotEqual(PathClassification.InsideWorkspace, resolved.Classification);
            Assert.Equal(
                Path.GetFullPath(missingTarget),
                resolved.CanonicalPath,
                ignoreCase: true,
                ignoreWhiteSpaceDifferences: false);
        }
        finally
        {
            if (File.Exists(link) || Directory.Exists(link))
            {
                File.Delete(link);
            }
        }
    }

    [Fact]
    public void Circular_links_are_rejected()
    {
        var cycleA = Path.Combine(_root, "cycle-a");
        var cycleB = Path.Combine(_root, "cycle-b");
        var createdA = TryCreateJunction(cycleA, cycleB);
        var createdB = TryCreateJunction(cycleB, cycleA);
        if (!createdA || !createdB)
        {
            TryDeleteLink(cycleA);
            TryDeleteLink(cycleB);
            return;
        }

        try
        {
            Assert.ThrowsAny<ArgumentException>(() => _resolver.Resolve(@"cycle-a\file.txt"));
        }
        finally
        {
            TryDeleteLink(cycleA);
            TryDeleteLink(cycleB);
        }
    }

    private static void TryDeleteLink(string link)
    {
        try
        {
            if (Directory.Exists(link) || File.Exists(link))
            {
                Directory.Delete(link, recursive: false);
            }
        }
        catch (IOException)
        {
        }
    }

    private static bool TryCreateJunction(string junctionPath, string targetPath)
    {
        try
        {
            if (Directory.Exists(junctionPath))
            {
                return true;
            }

            var info = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = System.Diagnostics.Process.Start(info);
            process?.WaitForExit(5_000);
            return Directory.Exists(junctionPath);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryCreateFileLink(string linkPath, string targetPath)
    {
        try
        {
            if (File.Exists(linkPath))
            {
                return true;
            }

            var info = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink \"{linkPath}\" \"{targetPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = System.Diagnostics.Process.Start(info);
            process?.WaitForExit(5_000);
            return File.Exists(linkPath);
        }
        catch
        {
            return false;
        }
    }
}
