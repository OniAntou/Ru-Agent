using System.Text;

namespace RuAgent.Broker.Tools.Files;

public static class AtomicFileReplacement
{
    public static void Replace(string targetPath, byte[] newContent, bool overwrite = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(newContent);
        var directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("invalid_target_path");
        }

        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".ru-tmp-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(newContent, 0, newContent.Length);
                stream.Flush(true);
            }

            if (overwrite && File.Exists(targetPath))
            {
                // ReplaceFile (unlike MoveFileEx) succeeds while a caller holds
                // the target open with FILE_SHARE_DELETE, which is what lets the
                // mutation guard keep its writer-denying handle across the swap.
                File.Replace(tempPath, targetPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, targetPath, overwrite);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }
        }
    }

    public static void Replace(string targetPath, string content, bool overwrite = true)
    {
        ArgumentNullException.ThrowIfNull(content);
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetBytes(content);
        Replace(targetPath, bytes, overwrite);
    }
}
