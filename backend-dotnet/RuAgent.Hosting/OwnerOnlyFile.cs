using System.Security.AccessControl;
using System.Security.Principal;

namespace RuAgent.Hosting;

/// <summary>
/// Creation helper for files that carry a credential, so the secret is never
/// readable by another principal even briefly.
/// </summary>
/// <remarks>
/// Writing the file first and restricting it afterwards leaves a window in which
/// the token sits on disk with whatever the directory grants. On
/// <c>%LOCALAPPDATA%</c> that is SYSTEM and Administrators; when the root is a
/// shared path — <c>RU_LOG_ROOT</c> and <c>RU_DATA_ROOT</c> are both honoured —
/// it is every principal the directory admits, and those principals can then
/// speak to the authenticated loopback endpoint the token protects.
///
/// The ACL is therefore applied at creation time through a <see cref="FileStream"/>
/// built from an explicit <see cref="FileSecurity"/> with inheritance disabled,
/// which is the only way to guarantee the file never exists with inherited
/// rights. The call fails closed: a secret that cannot be protected is not
/// written, and the caller must not publish a descriptor it cannot protect —
/// the same contract <c>RuntimeDescriptorWriter</c> already documents.
/// </remarks>
internal static class OwnerOnlyFile
{
    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="path"/>, creating the
    /// file so that only the current user can read it.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The file was created without a restrictive ACL. The caller must treat the
    /// path as unusable and remove it.
    /// </exception>
    internal static async Task WriteAllTextAsync(
        string path,
        string contents,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!OperatingSystem.IsWindows())
        {
            // Windows is the supported host; elsewhere the platform's own default
            // permissions apply rather than a half-ported ACL attempt.
            await File.WriteAllTextAsync(path, contents, cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var stream = CreateOwnerOnly(path);
        await using (var writer = new StreamWriter(stream))
        {
            await writer.WriteAsync(contents.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        EnsureOwnerOnly(path);
    }

    /// <summary>
    /// Synchronous form, for callers that publish the descriptor before any await.
    /// </summary>
    internal static void WriteAllText(string path, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, contents);
            return;
        }

        using (var stream = CreateOwnerOnly(path))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(contents);
        }

        EnsureOwnerOnly(path);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static FileStream CreateOwnerOnly(string path)
    {
        var identity = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("owner_identity_unavailable");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(identity);
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            AccessControlType.Allow));

        return System.IO.FileSystemAclExtensions.Create(
            new FileInfo(path),
            FileMode.Create,
            FileSystemRights.FullControl,
            FileShare.None,
            4096,
            FileOptions.WriteThrough,
            security);
    }

    /// <summary>
    /// Applies the same owner-only restriction to a file created by another path.
    /// </summary>
    internal static void RestrictToCurrentUser(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fileInfo = new FileInfo(path);
        var security = fileInfo.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var identity = WindowsIdentity.GetCurrent().User;
        if (identity is not null)
        {
            security.SetAccessRule(new FileSystemAccessRule(
                identity,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        }

        fileInfo.SetAccessControl(security);
        EnsureOwnerOnly(path);
    }

    /// <summary>
    /// Verifies the file really carries a protected, owner-only DACL. Ownership
    /// and rule shape are checked rather than assumed, because a silently ignored
    /// ACL call is exactly the failure this helper exists to prevent.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void EnsureOwnerOnly(string path)
    {
        var security = new FileInfo(path).GetAccessControl();
        if (!security.AreAccessRulesProtected)
        {
            throw new InvalidOperationException("acl_inheritance_not_disabled");
        }

        var identity = WindowsIdentity.GetCurrent().User;
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));
        var owners = rules
            .OfType<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow)
            .ToArray();
        if (owners.Length == 0 || owners.Any(rule => !identity!.Equals(rule.IdentityReference)))
        {
            throw new InvalidOperationException("acl_not_owner_only");
        }
    }
}
