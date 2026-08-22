using System.Text;
using System.Text.Json.Nodes;

namespace RuAgent.Broker.Tools.Notes;

public sealed class NotesAdapter
{
    private readonly WorkspacePathGuard _workspace;

    public NotesAdapter(WorkspacePathGuard workspace) => _workspace = workspace;

    public JsonObject Create(string relativePath, string title, string content, bool approved)
    {
        if (!approved)
        {
            throw new UnauthorizedAccessException("notes_write_requires_approval");
        }

        if (string.IsNullOrWhiteSpace(title) || title.Length > 256 || content.Length > 64 * 1024)
        {
            throw new ArgumentException("note_invalid");
        }

        var path = _workspace.Resolve(relativePath, allowMissing: true);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"# {title.Trim()}\n\n{content.Trim()}\n", new UTF8Encoding(false));
        return new JsonObject { ["path"] = _workspace.ToRelative(path), ["status"] = "created" };
    }
}
