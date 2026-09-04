using RuAgent.Broker.Policy;
using RuAgent.Broker.Runtime;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class SessionDirectoryGrantRegistryTests
{
    [Fact]
    public void Grant_is_reusable_only_by_the_same_session_and_descendants()
    {
        using var fixture = DirectoryGrantFixture.Create();
        var grants = new SessionDirectoryGrantRegistry();

        grants.Add("session-a", CanonicalDirectoryIdentity.CreateExisting(fixture.Shared));

        Assert.True(grants.Contains("session-a", fixture.Shared));
        Assert.True(grants.Contains("session-a", Path.Combine(fixture.Shared, "child", "file.txt")));
        Assert.False(grants.Contains("session-b", Path.Combine(fixture.Shared, "child", "file.txt")));
        Assert.False(grants.Contains("session-a", Path.Combine(fixture.Sibling, "file.txt")));
    }

    [Fact]
    public void Revoking_one_agent_session_preserves_another_sessions_grants()
    {
        using var fixture = DirectoryGrantFixture.Create();
        var grants = new SessionDirectoryGrantRegistry();
        var identity = CanonicalDirectoryIdentity.CreateExisting(fixture.Shared);
        grants.Add("session-a", identity);
        grants.Add("session-b", identity);

        grants.RevokeSession("session-a");

        Assert.False(grants.Contains("session-a", fixture.Shared));
        Assert.True(grants.Contains("session-b", fixture.Shared));
    }

    [Fact]
    public void Invalid_session_or_nonexistent_directory_is_rejected()
    {
        using var fixture = DirectoryGrantFixture.Create();
        var grants = new SessionDirectoryGrantRegistry();

        Assert.Throws<ArgumentException>(() => grants.Add(" ", CanonicalDirectoryIdentity.CreateExisting(fixture.Shared)));
        Assert.Throws<DirectoryNotFoundException>(() =>
            CanonicalDirectoryIdentity.CreateExisting(Path.Combine(fixture.Root, "missing")));
    }

    private sealed class DirectoryGrantFixture : IDisposable
    {
        private DirectoryGrantFixture(string root, string shared, string sibling)
        {
            Root = root;
            Shared = shared;
            Sibling = sibling;
        }

        public string Root { get; }
        public string Shared { get; }
        public string Sibling { get; }

        public static DirectoryGrantFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "ru-session-grants-" + Guid.NewGuid().ToString("N"));
            var shared = Path.Combine(root, "shared");
            var sibling = Path.Combine(root, "shared-sibling");
            Directory.CreateDirectory(Path.Combine(shared, "child"));
            Directory.CreateDirectory(sibling);
            return new DirectoryGrantFixture(root, shared, sibling);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
