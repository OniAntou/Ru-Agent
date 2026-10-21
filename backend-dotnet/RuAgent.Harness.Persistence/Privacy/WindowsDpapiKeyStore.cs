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

    /// <summary>
    /// DPAPI additional entropy, bound to this purpose rather than null. DPAPI
    /// CurrentUser already scopes to the user on this machine; the entropy keeps
    /// the key file useless to every other consumer of that user token that does
    /// not share this exact purpose string. It is deliberately NOT bound to the
    /// database path: <c>RU_DATA_ROOT</c> legitimately moves between test runs
    /// and setups, and a path-bound entropy would silently brick every payload
    /// on such a move -- the exact failure class this store refuses to create.
    /// </summary>
    public static readonly byte[] DpapiEntropy =
        SHA256.HashData(Encoding.UTF8.GetBytes("Ru.Harness.Payloads.v2\n"));

    private readonly string _keyPath;
    private readonly string _databasePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // The key is read once and reused while the key file is unchanged. Every fetch used
    // to rewrite three ACLs and call DPAPI (~16 ms), and hot paths such as the session
    // list fetch it once per workspace. A missing or rewritten file still takes the
    // full path, so a lost key keeps failing closed as harness_key_missing.
    private byte[]? _cachedKey;
    private DateTime _cachedKeyWriteTimeUtc;
    private long _cachedKeyLength;
    private bool _isDatabaseHardened;

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
            var keyFile = new FileInfo(_keyPath);
            if (_cachedKey is not null
                && keyFile.Exists
                && keyFile.LastWriteTimeUtc == _cachedKeyWriteTimeUtc
                && keyFile.Length == _cachedKeyLength)
            {
                // A database created after the key was cached still gets its ACL once.
                if (!_isDatabaseHardened && File.Exists(_databasePath))
                {
                    HardenFile(_databasePath);
                    _isDatabaseHardened = true;
                }

                // Callers zero the key after use, so each one gets its own copy.
                return (byte[])_cachedKey.Clone();
            }

            ForgetCachedKey();
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
            _isDatabaseHardened = File.Exists(_databasePath);
            if (_isDatabaseHardened)
            {
                HardenFile(_databasePath);
            }

            var key = await LoadKeyAsync(cancellationToken).ConfigureAwait(false);
            // Read the identity after loading: a legacy key is rewritten in place.
            keyFile.Refresh();
            if (keyFile.Exists)
            {
                _cachedKey = (byte[])key.Clone();
                _cachedKeyWriteTimeUtc = keyFile.LastWriteTimeUtc;
                _cachedKeyLength = keyFile.Length;
            }

            return key;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ForgetCachedKey()
    {
        if (_cachedKey is not null)
        {
            CryptographicOperations.ZeroMemory(_cachedKey);
            _cachedKey = null;
        }
    }

    /// <summary>
    /// True when the database already holds rows encrypted under this key.
    /// </summary>
    /// <remarks>
    /// "Cannot tell" is never "nothing to lose": a database locked by a racing
    /// host is exactly the shape that used to mint a replacement key over live
    /// protected rows, so an unopenable or unreadable database now fails the
    /// store closed instead of reporting it fresh. Only a database that opens
    /// cleanly and genuinely has no payload rows yet (or no payload table)
    /// counts as fresh.
    /// </remarks>
    private async Task<bool> HasProtectedRowsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_databasePath))
        {
            return false;
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            // A locked database must surface as BUSY quickly: the probe is a
            // bounded existence check, not a readiness wait. The one-second
            // timeout keeps the fail-closed path responsive for the host that
            // retries after the racing owner lets go.
            DefaultTimeout = 1,
        };
        await using var connection = new SqliteConnection(builder.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var tableCommand = connection.CreateCommand();
            tableCommand.CommandText = """
                SELECT EXISTS(
                    SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'agent_payloads'
                );
                """;
            var hasTable = await tableCommand.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            if (hasTable is 0L)
            {
                return false;
            }

            if (hasTable is not 1L)
            {
                throw new HarnessStoreException("harness_key_probe_unavailable");
            }

            await using var rowCommand = connection.CreateCommand();
            rowCommand.CommandText = "SELECT EXISTS(SELECT 1 FROM agent_payloads LIMIT 1);";
            var result = await rowCommand.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            return result is long and not 0;
        }
        catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6)
        {
            // SQLITE_BUSY / SQLITE_LOCKED: another host owns this database right
            // now, which is exactly the race that used to mint a replacement key
            // over live protected rows. Fail closed and let the caller retry.
            throw new HarnessStoreException("harness_key_probe_unavailable", error);
        }
        catch (SqliteException)
        {
            // Unreadable for any other reason (not a database, malformed): no
            // protected payload could be read from that file either, so it stays
            // the documented "nothing to lose" case.
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
            var protectedKey = ProtectedData.Protect(key, DpapiEntropy, DataProtectionScope.CurrentUser);
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

        // Current format first; a key file written before the purpose entropy
        // existed still unprotects with null entropy. The legacy read migrates
        // the file in place so the fallback never has to fire again.
        try
        {
            return UnprotectAndValidate(fileBytes.AsSpan(FileHeader.Length).ToArray(), DpapiEntropy);
        }
        catch (CryptographicException)
        {
        }

        byte[] legacyKey;
        try
        {
            legacyKey = UnprotectAndValidate(fileBytes.AsSpan(FileHeader.Length).ToArray(), null);
        }
        catch (CryptographicException exception)
        {
            throw new HarnessStoreException("harness_key_unprotect_failed", exception);
        }

        try
        {
            await RewriteKeyFileAsync(legacyKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The legacy key still works; the migration can happen on a later
            // run when the file is not being read concurrently.
        }

        return legacyKey;
    }

    private static byte[] UnprotectAndValidate(byte[] protectedKey, byte[]? entropy)
    {
        var key = ProtectedData.Unprotect(protectedKey, entropy, DataProtectionScope.CurrentUser);
        if (key.Length != 32)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new HarnessStoreException("harness_key_invalid");
        }

        return key;
    }

    /// <summary>
    /// Re-protects an existing key under the purpose entropy and replaces the
    /// key file atomically. Callers treat failure as non-fatal: the legacy
    /// protection remains readable until a later migration.
    /// </summary>
    private async Task RewriteKeyFileAsync(byte[] key, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_keyPath)
            ?? throw new HarnessStoreException("harness_key_path_invalid");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(_keyPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var protectedKey = ProtectedData.Protect(key, DpapiEntropy, DataProtectionScope.CurrentUser);
            var fileBytes = new byte[FileHeader.Length + protectedKey.Length];
            FileHeader.CopyTo(fileBytes, 0);
            protectedKey.CopyTo(fileBytes, FileHeader.Length);
            await File.WriteAllBytesAsync(temporary, fileBytes, cancellationToken)
                .ConfigureAwait(false);
            HardenFile(temporary);
            File.Move(temporary, _keyPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
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
