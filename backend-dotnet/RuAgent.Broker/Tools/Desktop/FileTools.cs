using System.Text;
using System.Text.Json.Nodes;

namespace RuAgent.Broker.Tools.Desktop;

public sealed class FileTools
{
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

        var bytes = File.ReadAllBytes(path);
        var truncated = bytes.Length > maxBytes;
        var content = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, maxBytes));
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
}
