using System.Text;
using System.Text.Json.Nodes;

namespace RuAgent.Broker.Tools.Desktop;

public sealed class FileEditException : Exception
{
    public FileEditException(string code) : base(code) => Code = code;

    public FileEditException(string code, JsonObject details) : base(code)
    {
        Code = code;
        Details = details;
    }

    public string Code { get; }

    public JsonObject? Details { get; }
}

public sealed class FileTools
{
    private const long MaxEditBytes = 4 * 1024 * 1024;

    private readonly WorkspacePathGuard _workspace;

    public FileTools(WorkspacePathGuard workspace) => _workspace = workspace;

    public JsonObject List(string relativePath)
    {
        var directory = _workspace.Resolve(relativePath);
        if (!Directory.Exists(directory))
        {
            throw new IOException("not_a_directory");
        }

        var entries = new JsonArray();
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Take(512))
        {
            var info = new FileInfo(entry);
            entries.Add(new JsonObject
            {
                ["path"] = _workspace.ToRelative(entry),
                ["kind"] = Directory.Exists(entry) ? "directory" : "file",
                ["size"] = Directory.Exists(entry) ? 0 : info.Length,
            });
        }

        return new JsonObject { ["entries"] = entries };
    }

    public JsonObject Read(string relativePath, int maxBytes = 256 * 1024)
    {
        var path = _workspace.Resolve(relativePath);
        if (maxBytes is < 1 or > 4 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        var observedLength = new FileInfo(path).Length;
        var read = ReadAtMost(path, maxBytes);
        var truncated = observedLength > maxBytes || read.Truncated;
        var content = Encoding.UTF8.GetString(read.Bytes);
        return new JsonObject
        {
            ["path"] = _workspace.ToRelative(path),
            ["content"] = content,
            ["truncated"] = truncated,
        };
    }

    public JsonObject Write(string relativePath, string content, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length > 256 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(content));
        }

        var path = _workspace.Resolve(relativePath, allowMissing: true);
        if (File.Exists(path) && !overwrite)
        {
            throw new IOException("file_exists");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return new JsonObject { ["path"] = _workspace.ToRelative(path), ["bytes"] = Encoding.UTF8.GetByteCount(content) };
    }

    public JsonObject Edit(string relativePath, string? oldString, string? newString, int expectedOccurrences = 1)
    {
        if (string.IsNullOrEmpty(oldString))
        {
            throw new FileEditException("old_string_empty");
        }

        var path = _workspace.Resolve(relativePath, allowMissing: true);
        if (!File.Exists(path))
        {
            throw new FileEditException("file_not_found");
        }

        if (new FileInfo(path).Length > MaxEditBytes)
        {
            throw new FileEditException("file_too_large");
        }

        var read = ReadAtMost(path, checked((int)MaxEditBytes));
        if (read.Truncated)
        {
            throw new FileEditException("file_too_large");
        }

        var text = Encoding.UTF8.GetString(read.Bytes);
        var matches = CountOccurrences(text, oldString);
        if (matches != expectedOccurrences)
        {
            throw new FileEditException("edit_not_unique", new JsonObject { ["matches"] = matches });
        }

        var updated = text.Replace(oldString, newString ?? string.Empty, StringComparison.Ordinal);
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(updated));
        return new JsonObject { ["path"] = _workspace.ToRelative(path), ["replacements"] = matches };
    }

    private static BoundedRead ReadAtMost(string path, int maxBytes)
    {
        var buffer = new byte[checked(maxBytes + 1)];
        var total = 0;
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        while (total < buffer.Length)
        {
            var count = stream.Read(buffer, total, buffer.Length - total);
            if (count == 0)
            {
                break;
            }

            total += count;
        }

        var truncated = total > maxBytes;
        Array.Resize(ref buffer, Math.Min(total, maxBytes));
        return new BoundedRead(buffer, truncated);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private readonly record struct BoundedRead(byte[] Bytes, bool Truncated);
}
