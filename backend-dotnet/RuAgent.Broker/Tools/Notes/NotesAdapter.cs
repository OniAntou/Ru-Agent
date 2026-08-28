using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Files;
using RuAgent.Broker.Tools.Paths;

namespace RuAgent.Broker.Tools.Notes;

public sealed class NotesAdapter
{
    private readonly WorkspacePathGuard _workspace;
    private readonly CanonicalPathResolver _resolver;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public NotesAdapter(WorkspacePathGuard workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _resolver = new CanonicalPathResolver(_workspace.Root);
    }

    public JsonObject Create(string relativePath, string title, string content, bool approved)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(content);
        if (!approved)
        {
            throw new UnauthorizedAccessException("notes_write_requires_approval");
        }

        if (string.IsNullOrWhiteSpace(title) || title.Length > 256 || content.Length > 64 * 1024)
        {
            throw new ArgumentException("note_invalid");
        }

        string text = $"# {title.Trim()}\n\n{content.Trim()}\n";
        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(text);
        }
        catch (EncoderFallbackException error)
        {
            throw new ArgumentException("invalid_utf8", error);
        }

        var path = FileMutationGuard.WithLock(() =>
        {
            // Preserve the legacy guard's input/symlink validation, then use the
            // canonical resolver so notes cannot target protected or escaped paths.
            _workspace.Resolve(relativePath, allowMissing: true);
            ResolvedPath resolved;
            try
            {
                resolved = _resolver.Resolve(relativePath);
            }
            catch (ArgumentException error)
            {
                throw new UnauthorizedAccessException("workspace_path_invalid", error);
            }

            if (resolved.IsOutsideWorkspace)
            {
                throw new UnauthorizedAccessException("workspace_path_outside_root");
            }

            if (resolved.IsProtected)
            {
                throw new UnauthorizedAccessException("protected_path");
            }

            var beforeHash = FileHashService.GetCurrentHash(resolved.CanonicalPath);
            FileMutationGuard.ReplaceIfHashMatches(resolved.CanonicalPath, bytes, beforeHash);
            return resolved.CanonicalPath;
        });

        return new JsonObject { ["path"] = _workspace.ToRelative(path), ["created"] = true };
    }
}
