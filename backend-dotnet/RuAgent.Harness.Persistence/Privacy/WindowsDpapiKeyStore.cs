using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;

namespace RuAgent.Harness.Persistence.Privacy;

[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiKeyStore : IHarnessKeyStore
{
    private static readonly byte[] FileHeader = Encoding.ASCII.GetBytes("RUHK1");
    private readonly string _keyPath;
    private readonly string _databasePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WindowsDpapiKeyStore(string keyPath, string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _keyPath = Path.GetFullPath(keyPath);
        _databasePath = Path.GetFullPath(databasePath);
    }

    public static int FileHeaderLength => FileHeader.Length;

    public async ValueTask<byte[]> GetOrCreateKeyAsync(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows DPAPI is required.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_keyPath)
                ?? throw new HarnessStoreException("harness_key_path_invalid");
            Directory.CreateDirectory(directory);
            HardenDirectory(directory);
            if (!File.Exists(_keyPath))
            {
                // A missing key file beside a database that already holds protected rows is
                // not a fresh install, it is a lost key. Minting a new one here left every
                // existing ciphertext undecryptable while the store still reported itself
                // available, so the failure surfaced later as payload_authentication_failed
                // with nothing pointing at the cause. Refuse instead: the data cannot be
                // recovered, and silently starting over is the one outcome this must not do.
                if (await HasProtectedRowsAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new HarnessStoreException("harness_key_missing");
                }

                await CreateKeyFileAsync(directory, cancellationToken).ConfigureAwait(false);
            }

            HardenFile(_keyPath);
            if (File.Exists(_databasePath))
            {
                HardenFile(_databasePath);
            }

            return await LoadKeyAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// True when the database already holds rows encrypted under this key.
    /// </summary>
    /// <remarks>
    /// Read-only and tolerant of shape: a database that cannot be opened or has no payload
    /// table yet is treated as fresh, because refusing to start on a database that holds
    /// nothing to lose would trade one silent failure for a loud, unnecessary one.
    /// </remarks>
    private async Task<bool> HasProtectedRowsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_databasePath))
        {
            return false;
        }

        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            };
            await using var connection = new SqliteConnection(builder.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT EXISTS(
                    SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'agent_payloads'
                ) AND EXISTS(SELECT 1 FROM agent_payloads LIMIT 1);
                """;
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is long and not 0;
        }
        catch (SqliteException)
        {
            // Not a shape this key ever protected: nothing to be responsible for.
            return false;
        }
    }

    private async Task CreateKeyFileAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(_keyPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var protectedKey = ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser);
            var fileBytes = new byte[FileHeader.Length + protectedKey.Length];
            FileHeader.CopyTo(fileBytes, 0);
            protectedKey.CopyTo(fileBytes, FileHeader.Length);
            await File.WriteAllBytesAsync(temporary, fileBytes, cancellationToken)
                .ConfigureAwait(false);
            HardenFile(temporary);
            try
            {
                File.Move(temporary, _keyPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(_keyPath))
            {
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private async Task<byte[]> LoadKeyAsync(
        CancellationToken cancellationToken)
    {
        byte[] fileBytes;
        try
        {
            fileBytes = await ReadBoundedBytesAsync(_keyPath, 64 * 1024, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new HarnessStoreException("harness_key_read_failed", exception);
        }

        if (fileBytes.Length <= FileHeader.Length
            || fileBytes.Length > 64 * 1024
            || !fileBytes.AsSpan(0, FileHeader.Length).SequenceEqual(FileHeader))
        {
            throw new HarnessStoreException("harness_key_invalid");
        }

        try
        {
            var key = ProtectedData.Unprotect(
                fileBytes.AsSpan(FileHeader.Length).ToArray(),
                null,
                DataProtectionScope.CurrentUser);
            if (key.Length != 32)
            {
                CryptographicOperations.ZeroMemory(key);
                throw new HarnessStoreException("harness_key_invalid");
            }

            return key;
        }
        catch (CryptographicException exception)
        {
            throw new HarnessStoreException("harness_key_unprotect_failed", exception);
        }
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var output = new MemoryStream(Math.Min(maximumBytes, 81920));
        var buffer = new byte[Math.Min(maximumBytes, 81920)];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length > maximumBytes - read)
                throw new HarnessStoreException("harness_key_invalid");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return output.ToArray();
    }

    private static void HardenFile(string path)
    {
        var currentUser = RequireCurrentUser();
        var security = new FileSecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static void HardenDirectory(string path)
    {
        var currentUser = RequireCurrentUser();
        var security = new DirectorySecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static SecurityIdentifier RequireCurrentUser() =>
        WindowsIdentity.GetCurrent().User
        ?? throw new HarnessStoreException("harness_user_identity_unavailable");
}
