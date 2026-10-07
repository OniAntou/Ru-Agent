using RuAgent.Broker.Tools.Paths;

namespace RuAgent.Broker.Tools.Files;

/// <summary>
/// Serializes adapter mutations in this process and performs the final preimage
/// check while holding the target open with a writer-denying share mode. The held
/// handle closes the window between the preimage hash and the filesystem
/// operation across processes: a writer can no longer slip a change under the
/// replacement. A preimage that differs when the handle opens still fails as a
/// bounded <c>workspace_changed</c> with zero write.
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
