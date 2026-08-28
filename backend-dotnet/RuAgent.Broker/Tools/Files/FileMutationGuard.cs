namespace RuAgent.Broker.Tools.Files;

/// <summary>
/// Serializes adapter mutations and performs the final preimage check immediately
/// before the filesystem operation. Approval and preparation intentionally happen
/// earlier; this guard closes the in-process TOCTOU window at execution time.
/// </summary>
internal static class FileMutationGuard
{
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
            var current = FileHashService.GetCurrentHash(path);
            if (!string.Equals(current, expectedHash, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("workspace_changed");
            }

            AtomicFileReplacement.Replace(path, content, overwrite);
            var after = FileHashService.GetCurrentHash(path);
            if (!string.Equals(after, FileHashService.ComputeBytesHash(content), StringComparison.Ordinal))
            {
                throw new IOException("io_error");
            }
            return 0;
        });
    }

    public static void DeleteIfHashMatches(string path, string expectedHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHash);
        WithLock(() =>
        {
            var current = FileHashService.GetCurrentHash(path);
            if (!string.Equals(current, expectedHash, StringComparison.Ordinal))
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
}
