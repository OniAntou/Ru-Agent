using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using RuBackend.Harness.Workspace;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class WorkspaceIdentityResolverSecurityTests
{
    [Fact]
    public async Task Resolve_uses_native_identity_when_directory_listing_is_denied_and_recreation_changes_identity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var parent = Path.Combine(Path.GetTempPath(), "ru-workspace-acl-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "workspace");
        var moved = Path.Combine(parent, "workspace-old");
        Directory.CreateDirectory(root);
        var originalSecurity = new DirectoryInfo(root).GetAccessControl(AccessControlSections.Access);
        DirectorySecurity? replacementSecurity = null;

        try
        {
            DenyDirectoryListing(root);
            var resolver = new WindowsWorkspaceIdentityResolver();
            var before = await resolver.ResolveAsync(root);

            Directory.Move(root, moved);
            Directory.CreateDirectory(root);
            replacementSecurity = new DirectoryInfo(root).GetAccessControl(AccessControlSections.Access);
            DenyDirectoryListing(root);

            var after = await resolver.ResolveAsync(root);

            Assert.NotEqual(before.RootFileId, after.RootFileId);
            Assert.NotEqual(before.WorkspaceId, after.WorkspaceId);
            Assert.NotEqual(before.DisplayIdentityHash, after.DisplayIdentityHash);
        }
        finally
        {
            RestoreAccessRules(root, replacementSecurity ?? originalSecurity);
            RestoreAccessRules(moved, originalSecurity);

            if (Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Resolve_fails_with_named_error_for_missing_windows_directory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var missing = Path.Combine(Path.GetTempPath(), "ru-workspace-missing-" + Guid.NewGuid().ToString("N"));
        var error = await Assert.ThrowsAsync<IOException>(
            () => new WindowsWorkspaceIdentityResolver().ResolveAsync(missing));

        Assert.Equal("workspace_identity_unverified", error.Message);
    }

    [Fact]
    public async Task Resolve_rejects_file_as_workspace_root()
    {
        var file = Path.Combine(Path.GetTempPath(), "ru-workspace-file-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(file, "workspace roots are directories");
        try
        {
            var error = await Assert.ThrowsAsync<IOException>(
                () => new WindowsWorkspaceIdentityResolver().ResolveAsync(file));

            Assert.Equal("workspace_identity_unverified", error.Message);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Resolve_honors_cancellation_before_native_lookup()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-workspace-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => new WindowsWorkspaceIdentityResolver().ResolveAsync(root, cancellation.Token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Resolve_returns_stable_identity_for_an_accessible_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-workspace-valid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var resolver = new WindowsWorkspaceIdentityResolver();
            var first = await resolver.ResolveAsync(root);
            var second = await resolver.ResolveAsync(root);

            Assert.Equal(first, second);
            Assert.False(string.IsNullOrWhiteSpace(first.RootFileId));
            if (OperatingSystem.IsWindows())
            {
                Assert.False(first.RootFileId.StartsWith("fid-", StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void DenyDirectoryListing(string path)
    {
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Current Windows user SID is unavailable.");
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.ListDirectory,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void RestoreAccessRules(string path, DirectorySecurity security)
    {
        if (OperatingSystem.IsWindows() && Directory.Exists(path))
        {
            var restoredSecurity = new DirectorySecurity();
            restoredSecurity.SetSecurityDescriptorSddlForm(
                security.GetSecurityDescriptorSddlForm(AccessControlSections.Access),
                AccessControlSections.Access);
            new DirectoryInfo(path).SetAccessControl(restoredSecurity);
        }
    }
}
