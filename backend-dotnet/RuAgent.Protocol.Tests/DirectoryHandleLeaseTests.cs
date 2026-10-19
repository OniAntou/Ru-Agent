using Xunit;

namespace RuAgent.Protocol.Tests;

public sealed class DirectoryHandleLeaseTests
{
    [Fact]
    public void Held_canonical_directory_and_ancestors_cannot_be_renamed_until_release()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-directory-lease-" + Guid.NewGuid().ToString("N"));
        var parent = Path.Combine(root, "parent");
        var child = Path.Combine(parent, "child");
        Directory.CreateDirectory(child);
        try
        {
            using (var lease = DirectoryHandleLease.Open(child, requireCanonicalPath: true))
            {
                Assert.ThrowsAny<IOException>(() => Directory.Move(child, Path.Combine(parent, "renamed")));
                Assert.ThrowsAny<IOException>(() => Directory.Move(parent, Path.Combine(root, "renamed-parent")));
                Assert.True(Directory.Exists(child));
            }
            Directory.Move(parent, Path.Combine(root, "released"));
            Assert.True(Directory.Exists(Path.Combine(root, "released", "child")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
