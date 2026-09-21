using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
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

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task A_lost_key_beside_protected_rows_is_refused_instead_of_replaced()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "ru-dpapi-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var keyPath = Path.Combine(root, "agent_harness.key");
        var databasePath = Path.Combine(root, "agent_harness.db");
        try
        {
            var store = new WindowsDpapiKeyStore(keyPath, databasePath);
            var original = (await store.GetOrCreateKeyAsync(CancellationToken.None)).ToArray();
            await SeedPayloadRowAsync(databasePath);
            File.Delete(keyPath);

            // Minting a replacement here would leave the seeded row permanently undecryptable
            // while the store still reported itself healthy.
            var error = await Assert.ThrowsAsync<HarnessStoreException>(
                async () => await store.GetOrCreateKeyAsync(CancellationToken.None));
            Assert.Equal("harness_key_missing", error.Code);
            Assert.False(File.Exists(keyPath), "a refused store must not write a replacement key");
            Assert.NotEmpty(original);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task A_database_without_protected_rows_still_gets_a_key()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "ru-dpapi-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var keyPath = Path.Combine(root, "agent_harness.key");
        var databasePath = Path.Combine(root, "agent_harness.db");
        try
        {
            // The guard must not turn "first run after the database file exists but is empty"
            // into a refusal: there is nothing to lose, so the key is still created.
            await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE agent_payloads (payload_id TEXT PRIMARY KEY);";
                await command.ExecuteNonQueryAsync();
            }

            var store = new WindowsDpapiKeyStore(keyPath, databasePath);
            var key = (await store.GetOrCreateKeyAsync(CancellationToken.None)).ToArray();

            Assert.Equal(32, key.Length);
            Assert.True(File.Exists(keyPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task SeedPayloadRowAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE agent_payloads (payload_id TEXT PRIMARY KEY);
            INSERT INTO agent_payloads(payload_id) VALUES ('payload-1');
            """;
        await command.ExecuteNonQueryAsync();
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
