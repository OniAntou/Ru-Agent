using RuAgent.Broker.Tools.Paths;

namespace RuAgent.Broker.Tools.Files;

/// <summary>
/// Serializes adapter mutations in this process and performs the final preimage
/// check while holding the target open with a writer-denying share mode. The held
/// handle closes the window between the preimage hash and the filesystem
/// operation for in-place writers, and the target's file identity is re-verified
/// against the held handle immediately before the swap, so an editor's atomic
/// save (rename-over) in that window still fails as a bounded
/// <c>workspace_changed</c> with zero write. A rename-over that lands between the
/// identity re-check and the swap itself is a residual window that path-based
/// replacement APIs cannot close.
/// </summary>
internal static class FileMutationGuard
{
    private const int BufferSize = 4096;
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
            if (FileHashService.IsMissing(expectedHash))
            {
                ReplaceWhenMissing(path, content);
                return 0;
            }

            using var held = OpenForMutation(path);
            if (!string.Equals(FileHashService.ComputeStreamHash(held), expectedHash, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("workspace_changed");
            }

            RequireSameFileIdentity(held, path);
            AtomicFileReplacement.Replace(path, content, overwrite);
            VerifyContent(path, FileHashService.ComputeBytesHash(content));
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

            using var held = OpenForMutation(path);
            if (!string.Equals(FileHashService.ComputeStreamHash(held), expectedHash, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("workspace_changed");
            }

            RequireSameFileIdentity(held, path);
            File.Delete(path);
            if (File.Exists(path) || Directory.Exists(path))
            {
                throw new IOException("io_error");
            }
            return 0;
        });
    }

    private static FileStream OpenForMutation(string path)
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
            return HardLinkGuard.OpenRead(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                BufferSize,
                FileOptions.SequentialScan);
        }
        catch (IOException error) when (IsConcurrentChange(error))
        {
            throw new UnauthorizedAccessException("workspace_changed", error);
        }
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
