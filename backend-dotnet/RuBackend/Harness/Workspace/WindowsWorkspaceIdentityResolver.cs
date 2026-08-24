using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Protocol.Harness;

namespace RuBackend.Harness.Workspace;

public sealed class WindowsWorkspaceIdentityResolver
{
    public async Task<WorkspaceIdentity> ResolveAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var canonicalRoot = GetCanonicalRoot(workspacePath);
        var volumeSerial = await GetVolumeSerialAsync(canonicalRoot, cancellationToken).ConfigureAwait(false);
        var rootFileId = await GetRootFileIdAsync(canonicalRoot, cancellationToken).ConfigureAwait(false);
        var displayHash = ComputeDisplayIdentityHash(canonicalRoot, volumeSerial, rootFileId);
        var workspaceId = ComputeWorkspaceId(canonicalRoot, volumeSerial, rootFileId);
        return new WorkspaceIdentity(workspaceId, volumeSerial, rootFileId, displayHash);
    }

    // v2 lease mapping: bind the already-trusted exact identity and canonical root into the Broker lease
    // without reinterpreting repository markers or current directory.
    public static BrokerRunLeaseRequest CreateBrokerLeaseRequest(
        WorkspaceIdentity trustedIdentity,
        string canonicalRoot,
        string runId,
        string sessionId,
        string preset,
        long snapshotVersion,
        string snapshotHash,
        DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(trustedIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(preset);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotHash);

        // Do not reinterpret markers or cwd: use the provided canonicalRoot exactly, only normalize separators via GetFullPath
        // without calling GetCanonicalRoot (which would re-resolve final path) – we assume caller already resolved via phase-1 final-path.
        // Validate rootedness BEFORE GetFullPath: GetFullPath would silently anchor a relative
        // input to the current directory, which is exactly the reinterpretation we forbid.
        if (!Path.IsPathRooted(canonicalRoot))
        {
            throw new ArgumentException("Canonical root must be rooted.", nameof(canonicalRoot));
        }

        var normalizedRoot = Path.GetFullPath(canonicalRoot);

        // Preserve trusted identity fields exactly; do not recompute from path.
        var request = new BrokerRunLeaseRequest(
            RunId: runId,
            SessionId: sessionId,
            WorkspaceId: trustedIdentity.WorkspaceId.Value,
            CanonicalRoot: normalizedRoot,
            VolumeSerialNumber: trustedIdentity.VolumeSerialNumber,
            RootFileId: trustedIdentity.RootFileId,
            DisplayIdentityHash: trustedIdentity.DisplayIdentityHash,
            Preset: preset,
            SnapshotVersion: snapshotVersion,
            SnapshotHash: snapshotHash,
            ExpiresAt: expiresAt);
        request.Validate();
        return request;
    }

    public static RuAgent.Protocol.Harness.BrokerRunLease CreateBrokerLease(
        WorkspaceIdentity trustedIdentity,
        string canonicalRoot,
        string runId,
        string sessionId,
        string preset,
        long snapshotVersion,
        string snapshotHash,
        DateTimeOffset expiresAt,
        IReadOnlyList<string> advertisedCapabilities)
    {
        var request = CreateBrokerLeaseRequest(
            trustedIdentity,
            canonicalRoot,
            runId,
            sessionId,
            preset,
            snapshotVersion,
            snapshotHash,
            expiresAt);
        return new RuAgent.Protocol.Harness.BrokerRunLease(
            request.RunId,
            request.SessionId,
            request.WorkspaceId,
            request.CanonicalRoot,
            request.VolumeSerialNumber,
            request.RootFileId,
            request.DisplayIdentityHash,
            request.Preset,
            request.SnapshotVersion,
            request.SnapshotHash,
            request.ExpiresAt,
            advertisedCapabilities);
    }

    public BrokerRunLeaseRequest CreateLeaseRequestForTrustedWorkspace(
        WorkspaceIdentity trustedIdentity,
        string canonicalRoot,
        string runId,
        string sessionId,
        AgentPermissionPreset preset,
        long snapshotVersion,
        string snapshotHash,
        DateTimeOffset expiresAt)
    {
        var presetWire = preset == AgentPermissionPreset.FullAccess ? "full_access" : "standard";
        return CreateBrokerLeaseRequest(
            trustedIdentity,
            canonicalRoot,
            runId,
            sessionId,
            presetWire,
            snapshotVersion,
            snapshotHash,
            expiresAt);
    }

    public static string GetCanonicalRoot(string workspacePath)
    {
        var full = Path.GetFullPath(workspacePath);
        // Normalize separators and trim trailing separator except root
        if (full.Length > 3 && full.EndsWith(Path.DirectorySeparatorChar))
        {
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        // Resolve final path via GetFinalPathNameByHandle if possible (avoid symlink/junction intermediate)
        try
        {
            if (Directory.Exists(full) || File.Exists(full))
            {
                var final = GetFinalPath(full);
                if (!string.IsNullOrWhiteSpace(final))
                {
                    full = final;
                }
            }
        }
        catch
        {
            // Fallback to GetFullPath result
        }

        // Ensure rooted and not empty
        if (!Path.IsPathRooted(full))
        {
            throw new ArgumentException("Workspace path must be rooted.", nameof(workspacePath));
        }

        // Repository markers grant nothing; we do not use .git etc to influence identity.
        return full;
    }

    public static string ComputeDisplayIdentityHash(string canonicalRoot, string volumeSerial, string rootFileId)
    {
        var input = $"{canonicalRoot.ToLowerInvariant()}|{volumeSerial}|{rootFileId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static WorkspaceId ComputeWorkspaceId(string canonicalRoot, string volumeSerial, string rootFileId)
    {
        var input = $"{canonicalRoot.ToLowerInvariant()}|{volumeSerial}|{rootFileId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var hex = Convert.ToHexString(hash).ToLowerInvariant()[..32];
        return new WorkspaceId($"ws_{hex}");
    }

    private static async Task<string> GetVolumeSerialAsync(string canonicalRoot, CancellationToken ct)
    {
        await Task.Yield();
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return FallbackVolumeSerial(canonicalRoot);
        }

        try
        {
            var root = Path.GetPathRoot(canonicalRoot);
            if (string.IsNullOrWhiteSpace(root))
            {
                return FallbackVolumeSerial(canonicalRoot);
            }

            var serial = GetVolumeSerialNative(root);
            if (serial is not null)
            {
                return serial;
            }
        }
        catch
        {
        }

        return FallbackVolumeSerial(canonicalRoot);
    }

    private static async Task<string> GetRootFileIdAsync(string canonicalRoot, CancellationToken ct)
    {
        await Task.Yield();
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return FallbackFileId(canonicalRoot);
        }

        try
        {
            var fileId = GetFileIdNative(canonicalRoot);
            if (fileId is not null)
            {
                return fileId;
            }
        }
        catch
        {
        }

        return FallbackFileId(canonicalRoot);
    }

    private static string FallbackVolumeSerial(string canonicalRoot)
    {
        var drive = Path.GetPathRoot(canonicalRoot) ?? canonicalRoot;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(drive.ToLowerInvariant()));
        return $"VOL-{Convert.ToHexString(hash)[..8]}";
    }

    private static string FallbackFileId(string canonicalRoot)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRoot.ToLowerInvariant()));
        return $"fid-{Convert.ToHexString(hash)[..16].ToLowerInvariant()}";
    }

    private static string? GetVolumeSerialNative(string rootPath)
    {
        var volumeName = new StringBuilder(261);
        var fileSystemName = new StringBuilder(261);
        if (GetVolumeInformation(rootPath, volumeName, volumeName.Capacity, out var serial, out _, out _, fileSystemName, fileSystemName.Capacity))
        {
            return serial.ToString("X8");
        }

        return null;
    }

    private static string? GetFileIdNative(string path)
    {
        const uint GENERIC_READ = 0x80000000;
        const uint FILE_SHARE_READ = 0x00000001;
        const uint FILE_SHARE_WRITE = 0x00000002;
        const uint FILE_SHARE_DELETE = 0x00000004;
        const uint OPEN_EXISTING = 3;
        const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

        using var handle = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        if (GetFileInformationByHandle(handle, out var info))
        {
            // Combine volume serial + file index to make unique
            var volume = info.dwVolumeSerialNumber.ToString("X8");
            var fileIndex = $"{info.nFileIndexHigh:X8}{info.nFileIndexLow:X8}";
            return $"{volume}-{fileIndex}";
        }

        return null;
    }

    private static string? GetFinalPath(string path)
    {
        const uint GENERIC_READ = 0x80000000;
        const uint FILE_SHARE_READ = 0x00000001;
        const uint FILE_SHARE_WRITE = 0x00000002;
        const uint FILE_SHARE_DELETE = 0x00000004;
        const uint OPEN_EXISTING = 3;
        const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        const int FILE_NAME_NORMALIZED = 0x0;

        using var handle = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        var sb = new StringBuilder(1024);
        var result = GetFinalPathNameByHandle(handle, sb, sb.Capacity, FILE_NAME_NORMALIZED);
        if (result == 0 || result > sb.Capacity)
        {
            return null;
        }

        var final = sb.ToString();
        // Strip \\?\ prefix if present
        if (final.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            final = final.Substring(4);
        }

        return Path.GetFullPath(final);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool GetVolumeInformation(string lpRootPathName, StringBuilder lpVolumeNameBuffer, int nVolumeNameSize, out uint lpVolumeSerialNumber, out uint lpMaximumComponentLength, out uint lpFileSystemFlags, StringBuilder lpFileSystemNameBuffer, int nFileSystemNameSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle hFile, StringBuilder lpszFilePath, int cchFilePath, int dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }
}
