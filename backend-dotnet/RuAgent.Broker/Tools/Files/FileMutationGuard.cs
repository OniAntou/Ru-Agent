using RuAgent.Broker.Tools.Paths;

namespace RuAgent.Broker.Tools.Files;

/// <summary>
/// Validates and mutates the same held file while denying competing writes and
/// rename-over. Existing content is flushed through that handle; missing files
/// use staged no-overwrite creation. Existing-file updates are not crash-atomic.
/// </summary>
internal static class FileMutationGuard
{
    private const int BufferSize = 4096;
    internal const int MaxRollbackBytes = 32 * 1024 * 1024;
    private const int SharingViolationHResult = unchecked((int)0x80070020);
    private static readonly object Gate = new();

    public static T WithLock<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (Gate)
        {
            return action();
        }
    }

    public static void ReplaceIfHashMatches(
        string path,
        byte[] content,
        string expectedHash,
        bool overwrite = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHash);
        WithLock(() =>
        {
            using var parent = Protocol.DirectoryHandleLease.Open(Path.GetDirectoryName(Path.GetFullPath(path))!,
                requireCanonicalPath: true, createMissing: FileHashService.IsMissing(expectedHash));
            if (FileHashService.IsMissing(expectedHash))
            {
                ReplaceWhenMissing(path, content);
                return 0;
            }

            using var held = OpenForMutation(path);
            if (held.Length > MaxRollbackBytes) throw new IOException("file_too_large");
            if (!string.Equals(FileHashService.ComputeStreamHash(held), expectedHash, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("workspace_changed");
            }

            RequireSameFileIdentity(held, path);
            HardLinkGuard.RequireFinalPathMatches(held.SafeFileHandle, path);
            if (!overwrite) throw new IOException("file_exists");
            WriteValidatedFile(held, content);
            return 0;
        });
    }

    public static void DeleteIfHashMatches(string path, string expectedHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHash);
        WithLock(() =>
        {
            if (FileHashService.IsMissing(expectedHash))
            {
                if (Directory.Exists(path))
                {
                    throw new IOException("is_directory");
                }

                if (File.Exists(path))
                {
                    throw new UnauthorizedAccessException("workspace_changed");
                }

                return 0;
            }

            using var parent = Protocol.DirectoryHandleLease.Open(Path.GetDirectoryName(Path.GetFullPath(path))!,
                requireCanonicalPath: true);
            using var held = OpenForMutation(path, isDeleting: true);
            if (!string.Equals(FileHashService.ComputeStreamHash(held), expectedHash, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("workspace_changed");
            }

            RequireSameFileIdentity(held, path);
            HardLinkGuard.RequireFinalPathMatches(held.SafeFileHandle, path);
            FileDeletionHandle.Delete(held);
            return 0;
        });
    }

    private static FileStream OpenForMutation(string path, bool isDeleting = false)
    {
        if (Directory.Exists(path))
        {
            throw new IOException("is_directory");
        }

        if (!File.Exists(path))
        {
            throw new UnauthorizedAccessException("workspace_changed");
        }

        try
        {
            if (isDeleting) return FileDeletionHandle.Open(path);
            return HardLinkGuard.OpenRead(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read,
                BufferSize,
                FileOptions.SequentialScan | FileOptions.WriteThrough);
        }
        catch (IOException error) when (IsConcurrentChange(error))
        {
            throw new UnauthorizedAccessException("workspace_changed", error);
        }
    }

    private static void WriteValidatedFile(FileStream held, byte[] content)
    {
        held.Position = 0;
        var original = new byte[checked((int)held.Length)];
        held.ReadExactly(original);
        try
        {
            WriteAndFlush(held, content);
            held.Position = 0;
            if (FileHashService.ComputeStreamHash(held) != FileHashService.ComputeBytesHash(content))
                throw new IOException("io_error");
        }
        catch
        {
            WriteAndFlush(held, original);
            throw;
        }
    }

    private static void WriteAndFlush(FileStream held, byte[] content)
    {
        held.Position = 0;
        held.Write(content);
        held.SetLength(content.Length);
        held.Flush(flushToDisk: true);
    }

    private static void RequireSameFileIdentity(FileStream held, string path)
    {
        if (!File.Exists(path))
        {
            throw new UnauthorizedAccessException("workspace_changed");
        }

        try
        {
            using var probe = HardLinkGuard.OpenRead(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                BufferSize,
                FileOptions.None);
            if (!HardLinkGuard.HasSameFileIdentity(held.SafeFileHandle, probe.SafeFileHandle))
            {
                throw new UnauthorizedAccessException("workspace_changed");
            }
        }
        catch (IOException error) when (IsConcurrentChange(error))
        {
            throw new UnauthorizedAccessException("workspace_changed", error);
        }
    }

    private static void ReplaceWhenMissing(string path, byte[] content)
    {
        if (Directory.Exists(path))
        {
            throw new IOException("is_directory");
        }

        if (File.Exists(path))
        {
            throw new UnauthorizedAccessException("workspace_changed");
        }

        try
        {
            // Atomic create: Move without overwrite fails if a concurrent writer
            // claimed the name after the caller observed it missing.
            AtomicFileReplacement.Replace(path, content, overwrite: false);
        }
        catch (IOException error) when (File.Exists(path) || Directory.Exists(path))
        {
            throw new UnauthorizedAccessException("workspace_changed", error);
        }

        VerifyContent(path, FileHashService.ComputeBytesHash(content));
    }

    private static void VerifyContent(string path, string expectedHash)
    {
        try
        {
            using var stream = HardLinkGuard.OpenRead(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                BufferSize,
                FileOptions.SequentialScan);
            if (!string.Equals(FileHashService.ComputeStreamHash(stream), expectedHash, StringComparison.Ordinal))
            {
                throw new IOException("io_error");
            }
        }
        catch (IOException error) when (IsConcurrentChange(error))
        {
            throw new IOException("io_error", error);
        }
    }

    private static bool IsConcurrentChange(IOException error) =>
        error is FileNotFoundException or DirectoryNotFoundException
        || error.HResult == SharingViolationHResult;
}
