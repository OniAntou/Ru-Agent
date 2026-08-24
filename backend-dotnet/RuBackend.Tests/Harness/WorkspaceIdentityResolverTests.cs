using System.Security.Cryptography;
using System.Text;
using RuAgent.Harness.Core;
using RuBackend.Harness.Workspace;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class WorkspaceIdentityResolverTests
{
    [Fact]
    public async Task Resolve_returns_canonical_final_root()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "ru-workspace-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var resolver = new WindowsWorkspaceIdentityResolver();
            var identity1 = await resolver.ResolveAsync(tempRoot);
            var identity2 = await resolver.ResolveAsync(tempRoot);
            // Canonical root should be same and stable
            var canonical = WindowsWorkspaceIdentityResolver.GetCanonicalRoot(tempRoot);
            Assert.Equal(Path.GetFullPath(tempRoot).TrimEnd(Path.DirectorySeparatorChar), canonical.TrimEnd(Path.DirectorySeparatorChar));
            Assert.Equal(identity1.DisplayIdentityHash, identity2.DisplayIdentityHash);
            Assert.Equal(identity1.VolumeSerialNumber, identity2.VolumeSerialNumber);
            Assert.Equal(identity1.RootFileId, identity2.RootFileId);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task Resolve_includes_volume_serial_and_root_file_id()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "ru-workspace-serial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var resolver = new WindowsWorkspaceIdentityResolver();
            var identity = await resolver.ResolveAsync(tempRoot);
            Assert.False(string.IsNullOrWhiteSpace(identity.VolumeSerialNumber));
            Assert.False(string.IsNullOrWhiteSpace(identity.RootFileId));
            Assert.True(identity.VolumeSerialNumber.Length <= 64);
            Assert.True(identity.RootFileId.Length <= 128);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task Display_identity_hash_is_stable()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "ru-workspace-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var resolver = new WindowsWorkspaceIdentityResolver();
            var a = await resolver.ResolveAsync(tempRoot);
            var b = await resolver.ResolveAsync(tempRoot);
            Assert.Equal(a.DisplayIdentityHash, b.DisplayIdentityHash);
            Assert.Matches("^[0-9a-f]{64}$", a.DisplayIdentityHash);
            // Different path should give different hash (unless same volume/file but different path)
            var other = Path.Combine(Path.GetTempPath(), "ru-workspace-hash-other-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(other);
            try
            {
                var c = await resolver.ResolveAsync(other);
                Assert.NotEqual(a.DisplayIdentityHash, c.DisplayIdentityHash);
            }
            finally
            {
                Directory.Delete(other, true);
            }
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task Moved_directory_requires_trust_again()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-workspace-move-" + Guid.NewGuid().ToString("N"));
        var sub = Path.Combine(root, "workspace");
        Directory.CreateDirectory(sub);
        try
        {
            var resolver = new WindowsWorkspaceIdentityResolver();
            var before = await resolver.ResolveAsync(sub);
            var moved = Path.Combine(Path.GetTempPath(), "ru-workspace-moved-" + Guid.NewGuid().ToString("N"));
            Directory.Move(sub, moved);
            try
            {
                var after = await resolver.ResolveAsync(moved);
                // Moved directory should have different canonical root, thus different display hash and workspace id
                Assert.NotEqual(before.DisplayIdentityHash, after.DisplayIdentityHash);
                Assert.NotEqual(before.WorkspaceId.Value, after.WorkspaceId.Value);
            }
            finally
            {
                if (Directory.Exists(moved))
                {
                    Directory.Delete(moved, true);
                }
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task Repository_markers_grant_nothing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "ru-workspace-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(Path.Combine(tempRoot, ".git"));
        File.WriteAllText(Path.Combine(tempRoot, ".gitignore"), "node_modules");
        try
        {
            var resolver = new WindowsWorkspaceIdentityResolver();
            var withMarkers = await resolver.ResolveAsync(tempRoot);
            // Remove markers and resolve again - identity should be same because markers grant nothing
            Directory.Delete(Path.Combine(tempRoot, ".git"), true);
            File.Delete(Path.Combine(tempRoot, ".gitignore"));
            var withoutMarkers = await resolver.ResolveAsync(tempRoot);
            Assert.Equal(withMarkers.DisplayIdentityHash, withoutMarkers.DisplayIdentityHash);
            Assert.Equal(withMarkers.WorkspaceId.Value, withoutMarkers.WorkspaceId.Value);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task Lease_mapping_preserves_trusted_identity_without_reinterpreting_markers_or_cwd()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "ru-lease-map-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var resolver = new WindowsWorkspaceIdentityResolver();
            var identity = await resolver.ResolveAsync(tempRoot);
            var canonicalRoot = WindowsWorkspaceIdentityResolver.GetCanonicalRoot(tempRoot);
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);

            var request = WindowsWorkspaceIdentityResolver.CreateBrokerLeaseRequest(
                identity,
                canonicalRoot,
                "run_" + new string('a', 32),
                new Guid("00000000000000000000000000000001").ToString("N"),
                "standard",
                snapshotVersion: 1,
                snapshotHash: new string('0', 64),
                expiresAt);

            Assert.Equal(identity.WorkspaceId.Value, request.WorkspaceId);
            Assert.Equal(identity.VolumeSerialNumber, request.VolumeSerialNumber);
            Assert.Equal(identity.RootFileId, request.RootFileId);
            Assert.Equal(identity.DisplayIdentityHash, request.DisplayIdentityHash);
            Assert.Equal(
                canonicalRoot.TrimEnd(Path.DirectorySeparatorChar),
                request.CanonicalRoot.TrimEnd(Path.DirectorySeparatorChar));

            // Repository markers appearing after trust must not change the mapped lease.
            Directory.CreateDirectory(Path.Combine(tempRoot, ".git"));
            File.WriteAllText(Path.Combine(tempRoot, "Cargo.toml"), "[package]");
            var remapped = WindowsWorkspaceIdentityResolver.CreateBrokerLeaseRequest(
                identity,
                canonicalRoot,
                "run_" + new string('a', 32),
                new Guid("00000000000000000000000000000001").ToString("N"),
                "standard",
                snapshotVersion: 1,
                snapshotHash: new string('0', 64),
                expiresAt);
            Assert.Equal(request.VolumeSerialNumber, remapped.VolumeSerialNumber);
            Assert.Equal(request.RootFileId, remapped.RootFileId);
            Assert.Equal(request.DisplayIdentityHash, remapped.DisplayIdentityHash);
            Assert.Equal(request.WorkspaceId, remapped.WorkspaceId);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task Moved_identity_produces_different_lease_binding()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-lease-move-" + Guid.NewGuid().ToString("N"));
        var sub = Path.Combine(root, "ws");
        Directory.CreateDirectory(sub);
        try
        {
            var resolver = new WindowsWorkspaceIdentityResolver();
            var before = await resolver.ResolveAsync(sub);
            var moved = Path.Combine(Path.GetTempPath(), "ru-lease-moved-" + Guid.NewGuid().ToString("N"));
            Directory.Move(sub, moved);
            try
            {
                var after = await resolver.ResolveAsync(moved);
                var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
                var session = Guid.NewGuid().ToString("N");

                var beforeRequest = WindowsWorkspaceIdentityResolver.CreateBrokerLeaseRequest(
                    before,
                    WindowsWorkspaceIdentityResolver.GetCanonicalRoot(sub),
                    "run_" + new string('b', 32),
                    session,
                    "standard",
                    1,
                    new string('1', 64),
                    expiresAt);
                var afterRequest = WindowsWorkspaceIdentityResolver.CreateBrokerLeaseRequest(
                    after,
                    WindowsWorkspaceIdentityResolver.GetCanonicalRoot(moved),
                    "run_" + new string('b', 32),
                    session,
                    "standard",
                    1,
                    new string('1', 64),
                    expiresAt);

                Assert.NotEqual(beforeRequest.WorkspaceId, afterRequest.WorkspaceId);
                Assert.NotEqual(beforeRequest.DisplayIdentityHash, afterRequest.DisplayIdentityHash);
            }
            finally
            {
                Directory.Delete(moved, true);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void Relative_canonical_root_is_rejected_for_lease_mapping()
    {
        var identity = new RuAgent.Harness.Core.Workspace.WorkspaceIdentity(
            new WorkspaceId("ws_" + new string('c', 32)),
            "vol",
            "fileid",
            new string('d', 64));

        Assert.ThrowsAny<ArgumentException>(() =>
            WindowsWorkspaceIdentityResolver.CreateBrokerLeaseRequest(
                identity,
                "relative/path",
                "run_" + new string('e', 32),
                Guid.NewGuid().ToString("N"),
                "standard",
                1,
                new string('0', 64),
                DateTimeOffset.UtcNow.AddMinutes(5)));
    }
}
