using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.Versioning;
using RuAgent.Harness.Persistence.Privacy;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiKeyStoreTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Key_is_stable_current_user_protected_and_files_have_no_broad_writer()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "ru-dpapi-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var keyPath = Path.Combine(root, "agent_harness.key");
        var databasePath = Path.Combine(root, "agent_harness.db");
        await File.WriteAllBytesAsync(databasePath, [1, 2, 3]);
        try
        {
            var firstStore = new WindowsDpapiKeyStore(keyPath, databasePath);
            var first = (await firstStore.GetOrCreateKeyAsync(CancellationToken.None)).ToArray();
            var second = (await new WindowsDpapiKeyStore(keyPath, databasePath)
                .GetOrCreateKeyAsync(CancellationToken.None)).ToArray();
            var protectedFile = await File.ReadAllBytesAsync(keyPath);
            var protectedBytes = protectedFile[WindowsDpapiKeyStore.FileHeaderLength..];

            Assert.Equal(32, first.Length);
            Assert.Equal(first, second);
            Assert.DoesNotContain(Convert.ToHexString(first), Convert.ToHexString(protectedFile));
            Assert.Equal(
                first,
                ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser));
            AssertNoBroadWriter(keyPath);
            AssertNoBroadWriter(databasePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertNoBroadWriter(string path)
    {
        var broadPrincipals = new HashSet<string>(StringComparer.Ordinal)
        {
            new SecurityIdentifier(WellKnownSidType.WorldSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null).Value,
        };
        var rules = new FileInfo(path).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>();
        var writeRights = FileSystemRights.Write
            | FileSystemRights.Modify
            | FileSystemRights.FullControl;

        Assert.DoesNotContain(rules, rule =>
            rule.AccessControlType == AccessControlType.Allow
            && rule.IdentityReference is SecurityIdentifier identity
            && broadPrincipals.Contains(identity.Value)
            && (rule.FileSystemRights & writeRights) != 0);
    }
}
