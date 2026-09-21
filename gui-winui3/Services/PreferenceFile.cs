namespace RuApp.Services;

/// <summary>
/// Reading a bounded JSON preference file from disk.
/// </summary>
/// <remarks>
/// Every preference store -- the workspace-session map, the theme choice, the group
/// collapse flags, the pins -- had its own byte-for-byte copy of this read. The bound is
/// the part worth keeping in one place: a preference file is written by the app itself, so
/// a file larger than the cap is not a preference, and the reader has to check the length
/// as it reads rather than after, so a runaway file cannot be buffered first and refused
/// afterwards.
///
/// This does not catch: every caller reads inside a guard that already treats an
/// unreadable file as absent, and swallowing here would hide which of the two refused it.
/// </remarks>
internal static class PreferenceFile
{
    /// <summary>The most a preference file may weigh. Anything larger reads as absent.</summary>
    internal const int MaximumBytes = 64 * 1024;

    /// <summary>
    /// Reads the file, or returns null when it is over the cap. A missing or unreadable
    /// file propagates, because the caller's guard handles both.
    /// </summary>
    internal static byte[]? ReadOrNull(string path, int maximumBytes = MaximumBytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var output = new MemoryStream(Math.Min(maximumBytes, 8192));
        var buffer = new byte[Math.Min(maximumBytes, 8192)];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length > maximumBytes - read)
            {
                return null;
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }
}
