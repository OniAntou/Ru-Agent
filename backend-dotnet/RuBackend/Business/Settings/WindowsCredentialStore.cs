using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace RuBackend.Business.Settings;

public sealed record WindowsCredentialEntry(string UserName, string Secret);

public interface IWindowsCredentialApi
{
    WindowsCredentialEntry? Read(string targetName);

    void Write(string targetName, string userName, string secret);

    void Delete(string targetName);
}

public sealed class WindowsCredentialStore : ICredentialStore
{
    private const int MaximumSecretCharacters = 32 * 1024;
    private readonly IWindowsCredentialApi _native;

    public WindowsCredentialStore()
        : this(new WindowsCredentialApi())
    {
    }

    public WindowsCredentialStore(IWindowsCredentialApi native)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
    }

    public Task<string?> GetAsync(
        string serviceName,
        string accountName,
        CancellationToken cancellationToken)
    {
        ValidateNames(serviceName, accountName);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var direct = _native.Read(serviceName);
            if (direct is not null &&
                string.Equals(direct.UserName, accountName, StringComparison.Ordinal))
            {
                return Task.FromResult<string?>(direct.Secret);
            }

            var compound = _native.Read(CompoundName(serviceName, accountName));
            return Task.FromResult(
                compound is not null &&
                string.Equals(compound.UserName, accountName, StringComparison.Ordinal)
                    ? compound.Secret
                    : null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw new CredentialStoreException("Could not read the credential.");
        }
    }

    public Task SetAsync(
        string serviceName,
        string accountName,
        string secret,
        CancellationToken cancellationToken)
    {
        ValidateNames(serviceName, accountName);
        ArgumentNullException.ThrowIfNull(secret);
        // Reject empty and whitespace-only secrets, but store significant
        // secrets byte-exact: surrounding whitespace can be part of a pasted
        // key, so only the emptiness check trims, never the stored value.
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ArgumentException("Credential cannot be empty.", nameof(secret));
        }
        if (secret.Length > MaximumSecretCharacters)
        {
            throw new ArgumentException("Credential is too large.", nameof(secret));
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var existing = _native.Read(serviceName);
            if (existing is not null)
            {
                _native.Write(
                    CompoundName(serviceName, existing.UserName),
                    existing.UserName,
                    existing.Secret);
            }

            _native.Write(serviceName, accountName, secret);
            return Task.CompletedTask;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw new CredentialStoreException("Could not save the credential.");
        }
    }

    public Task DeleteAsync(
        string serviceName,
        string accountName,
        CancellationToken cancellationToken)
    {
        ValidateNames(serviceName, accountName);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            foreach (var target in new[] { serviceName, CompoundName(serviceName, accountName) })
            {
                var existing = _native.Read(target);
                if (existing is not null &&
                    string.Equals(existing.UserName, accountName, StringComparison.Ordinal))
                {
                    _native.Delete(target);
                }
            }

            return Task.CompletedTask;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw new CredentialStoreException("Could not remove the credential.");
        }
    }

    private static string CompoundName(string serviceName, string accountName)
    {
        return $"{accountName}@{serviceName}";
    }

    private static void ValidateNames(string serviceName, string accountName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
    }
}

public sealed class WindowsCredentialApi : IWindowsCredentialApi
{
    private const uint MaximumCredentialBlobBytes = 64 * 1024;
    private const uint GenericCredential = 1;
    private const uint PersistEnterprise = 3;
    private const int ErrorNotFound = 1168;

    public WindowsCredentialEntry? Read(string targetName)
    {
        EnsureWindows();
        if (!CredRead(targetName, GenericCredential, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return null;
            }

            throw new Win32Exception(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlobSize > MaximumCredentialBlobBytes
                || (credential.CredentialBlobSize > 0 && credential.CredentialBlob == IntPtr.Zero)
                || credential.CredentialBlobSize % 2 != 0)
            {
                throw new InvalidDataException("Windows credential data is invalid.");
            }

            var secretBytes = new byte[credential.CredentialBlobSize];
            if (secretBytes.Length > 0)
            {
                Marshal.Copy(credential.CredentialBlob, secretBytes, 0, secretBytes.Length);
            }

            var secret = Encoding.Unicode.GetString(secretBytes).TrimEnd('\0');
            return new WindowsCredentialEntry(credential.UserName ?? string.Empty, secret);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Write(string targetName, string userName, string secret)
    {
        EnsureWindows();
        var secretBytes = Encoding.Unicode.GetBytes(secret);
        var handle = GCHandle.Alloc(secretBytes, GCHandleType.Pinned);
        try
        {
            var credential = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = targetName,
                CredentialBlobSize = (uint)secretBytes.Length,
                CredentialBlob = handle.AddrOfPinnedObject(),
                Persist = PersistEnterprise,
                UserName = userName,
                Comment = "Stored using Ru ASP.NET Core",
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            handle.Free();
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    public void Delete(string targetName)
    {
        EnsureWindows();
        if (!CredDelete(targetName, GenericCredential, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(error);
            }
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Credential Manager is required.");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint reservedFlag,
        out IntPtr credentialPtr);

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("Advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
