namespace RuAgent.Broker.Tests.Support;

/// <summary>Cleanup for temporary directories that a just-terminated child process may still hold.</summary>
internal static class TemporaryDirectories
{
    /// <summary>
    /// Windows releases a killed process tree's handles on its own schedule, so deleting the
    /// child's working directory right after the run can fail with a sharing violation.
    /// Retries for up to five seconds, the same allowance the ConPTY fixtures use, then
    /// reports the last error.
    /// </summary>
    public static void DeleteEventually(string path)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 50 && Directory.Exists(path); attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                lastError = error;
                Thread.Sleep(100);
            }
        }

        if (Directory.Exists(path))
        {
            throw new IOException("temporary_directory_cleanup_failed", lastError);
        }
    }
}
