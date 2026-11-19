using System.Text;
using RuAgent.Cli.Images;

namespace RuAgent.Cli.Attachments;

/// <summary>
/// Loads one pending file attachment. Small UTF-8 text is inlined; any other file, of any
/// type or size, is attached by its workspace path, and a file from outside the workspace
/// is copied into <c>.ru/attachments</c> first (ADR 0053). The model decides how to read it.
/// </summary>
public static class CliTextFiles
{
    /// <summary>
    /// A text file larger than this is sent as a path, not inlined: the model reads what it
    /// needs with <c>read</c>, and the prompt keeps its room. The desktop app uses the same
    /// threshold.
    /// </summary>
    public const long ReferenceThresholdBytes = RuAgent.Client.Attachments.AttachmentStaging.InlineTextThresholdBytes;

    public static bool IsSupported(string name) =>
        CliImagePolicy.IsTextOrCodeFile(name);

    /// <summary>The workspace-relative path with forward slashes, or null outside the workspace.</summary>
    internal static string? WorkspaceRelativePath(string resolved, string workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace)) return null;
        try
        {
            var root = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(resolved);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
            return relative.Length > 0 ? relative : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public static async Task<(CliTextAttachment? Attachment, string? Error)> LoadAsync(
        string path,
        string workspace,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string resolved;
        try
        {
            resolved = CliImageFiles.ResolvePath(path, workspace);
        }
        catch (Exception ex)
        {
            return (null, $"invalid_attachment_path: {CliErrorText.ShortMessage(ex.Message)}");
        }

        var name = Path.GetFileName(resolved);
        if (name.Length == 0)
        {
            return (null, "invalid_attachment_path: the path has no file name");
        }

        if (!File.Exists(resolved))
        {
            return (null, Directory.Exists(resolved)
                ? $"attachment_is_folder: {name} is a folder; attach the files inside it"
                : $"attachment_not_found: {name}");
        }

        long length;
        try
        {
            length = new FileInfo(resolved).Length;
        }
        catch (Exception ex)
        {
            return (null, $"attachment_unreadable: {name} - {CliErrorText.ShortMessage(ex.Message)}");
        }

        // Small text is inlined; everything else reaches the model as a path it opens with
        // `read` (ADR 0053): any size, any format. A file outside the workspace is first
        // copied into .ru/attachments, where the file tools can open it.
        var relative = WorkspaceRelativePath(resolved, workspace);
        if (IsSupported(resolved) && length <= ReferenceThresholdBytes)
        {
            var inline = await TryLoadInlineAsync(resolved, name, length, cancellationToken).ConfigureAwait(false);
            if (inline is not null)
            {
                return (inline, null);
            }
        }

        if (relative is not null)
        {
            return (new CliTextAttachment(name, string.Empty, length, relative), null);
        }

        return await StageAsync(resolved, name, workspace, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Copies an outside file into the workspace and attaches the copy by path.</summary>
    internal static async Task<(CliTextAttachment? Attachment, string? Error)> StageAsync(
        string resolved,
        string name,
        string workspace,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return (null, $"attachment_outside_workspace: {name} is outside the workspace and there is no workspace to copy it into");
        }

        try
        {
            var staged = await RuAgent.Client.Attachments.AttachmentStaging
                .StageAsync(resolved, workspace, cancellationToken)
                .ConfigureAwait(false);
            return (new CliTextAttachment(name, string.Empty, staged.ByteSize, staged.RelativePath, IsStaged: true), null);
        }
        catch (RuAgent.Client.Attachments.AttachmentStagingException error)
        {
            return (null, $"{error.Code}: {CliErrorText.ShortMessage(error.Message)}");
        }
    }

    /// <summary>The file as inline text, or null when it is not UTF-8 text (it is then sent by path).</summary>
    private static async Task<CliTextAttachment?> TryLoadInlineAsync(
        string resolved,
        string name,
        long length,
        CancellationToken cancellationToken)
    {
        byte[]? bytes;
        try
        {
            await using var stream = File.OpenRead(resolved);
            bytes = await CliImagePolicy.ReadBoundedBytesAsync(stream, (int)ReferenceThresholdBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return bytes is null ? null : LoadBytes(name, bytes, length).Attachment;
    }

    internal static (CliTextAttachment? Attachment, string? Error) LoadBytes(string name, byte[] bytes, long sourceLength)
    {
        if (!IsSupported(name))
        {
            return (null, $"unsupported_attachment: {name} is not a supported text/code file");
        }

        // Strict UTF-8 (or a UTF-16 BOM), no NUL: a binary file renamed to .txt used to be
        // inlined as a page of U+FFFD.
        return RuAgent.Client.Attachments.TextAttachmentDecoder.TryDecode(bytes, out var text, out var code)
            ? (new CliTextAttachment(name, text, sourceLength), null)
            : (null, $"{code}: {name} is not UTF-8 text; save it as UTF-8 and attach it again");
    }
}
