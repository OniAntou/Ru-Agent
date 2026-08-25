using System.Text;

namespace RuAgent.Broker.Tools.Files;

public static class AtomicFileReplacement
{
    public static void Replace(string targetPath, byte[] newContent)
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

            File.Move(tempPath, targetPath, overwrite: true);
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

    public static void Replace(string targetPath, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetBytes(content);
        Replace(targetPath, bytes);
    }
}
