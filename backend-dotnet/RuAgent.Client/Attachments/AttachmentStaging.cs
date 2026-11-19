using System.Security.Cryptography;

namespace RuAgent.Client.Attachments;

/// <summary>A named failure while copying an attachment into the workspace.</summary>
public sealed class AttachmentStagingException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>An attachment copied into the workspace, by its workspace-relative path.</summary>
public sealed record StagedAttachment(string RelativePath, long ByteSize);

/// <summary>
/// Copies a file the user attached from outside the workspace into
/// <c>&lt;workspace&gt;\.ru\attachments</c>, so the model opens it with the file tools like any
/// workspace file (ADR 0053). Attaching never needs a file type the client understands: the
/// model gets a path and decides how to read it.
/// </summary>
/// <remarks>
/// This is the user's own action on their own workspace, done by the client; it grants the
/// agent nothing new. The folder carries its own <c>.gitignore</c> (<c>*</c>) so a copy never
/// reaches the user's repository, a copy is bounded by <see cref="MaxBytes"/>, and copies older
/// than <see cref="RetentionDays"/> days are pruned when another file is staged.
/// </remarks>
public static class AttachmentStaging
{
    /// <summary>The staging folder, relative to the workspace, with forward slashes.</summary>
    public const string FolderRelativePath = ".ru/attachments";

    /// <summary>Largest file copied into the workspace.</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Text up to this size is inlined in the prompt; anything larger, or not text, is sent
    /// by path. Both clients use this threshold.
    /// </summary>
    public const long InlineTextThresholdBytes = 8 * 1024;

    /// <summary>Staged copies older than this are removed the next time a file is staged.</summary>
    public const int RetentionDays = 14;

    public const string TooLargeCode = "attachment_too_large";
    public const string FailedCode = "attachment_staging_failed";

    private const int MaxNameLength = 120;

    /// <summary>Whether <paramref name="relativePath"/> names a file inside the staging folder.</summary>
    public static bool IsStagedPath(string? relativePath) =>
        relativePath is not null
        && relativePath.Replace('\\', '/').StartsWith(FolderRelativePath + "/", StringComparison.OrdinalIgnoreCase);

    public static async Task<StagedAttachment> StageAsync(string sourcePath, string workspaceRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        try
        {
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            return await StageAsync(source, Path.GetFileName(sourcePath), workspaceRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new AttachmentStagingException(FailedCode, $"{Path.GetFileName(sourcePath)} could not be read: {error.Message}", error);
        }
    }

    public static async Task<StagedAttachment> StageAsync(Stream source, string fileName, string workspaceRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var name = SafeFileName(fileName);
        var folder = Path.Combine(Path.GetFullPath(workspaceRoot), ".ru", "attachments");
        string partial;
        try
        {
            Directory.CreateDirectory(folder);
            EnsureIgnored(folder);
            Prune(folder);
            partial = Path.Combine(folder, $".{Guid.NewGuid():N}.partial");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new AttachmentStagingException(FailedCode, $"the workspace folder .ru/attachments could not be prepared: {error.Message}", error);
        }

        try
        {
            byte[] hash;
            long length;
            await using (var target = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                length = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    length += read;
                    if (length > MaxBytes)
                    {
                        throw new AttachmentStagingException(
                            TooLargeCode,
                            $"{name} is larger than {MaxBytes / (1024 * 1024)} MB; copy it into the workspace yourself, or attach a smaller file");
                    }

                    digest.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                hash = digest.GetHashAndReset();
            }

            var finalName = ChooseName(folder, name, length, hash);
            var finalPath = Path.Combine(folder, finalName);
            if (File.Exists(finalPath))
            {
                // The same bytes were staged before under this name: reuse that copy.
                File.Delete(partial);
                File.SetLastWriteTimeUtc(finalPath, DateTime.UtcNow);
            }
            else
            {
                File.Move(partial, finalPath);
            }

            return new StagedAttachment($"{FolderRelativePath}/{finalName}", length);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            TryDeleteFile(partial);
            throw new AttachmentStagingException(FailedCode, $"{name} could not be copied into the workspace: {error.Message}", error);
        }
        catch
        {
            TryDeleteFile(partial);
            throw;
        }
    }

    /// <summary>Removes a staged copy the user detached before sending. Best effort.</summary>
    public static void TryRemove(string relativePath, string workspaceRoot)
    {
        if (!IsStagedPath(relativePath) || string.IsNullOrWhiteSpace(workspaceRoot)) return;
        var folder = Path.Combine(Path.GetFullPath(workspaceRoot), ".ru", "attachments");
        var full = Path.GetFullPath(Path.Combine(Path.GetFullPath(workspaceRoot), relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!string.Equals(Path.GetDirectoryName(full), folder, StringComparison.OrdinalIgnoreCase)) return;
        TryDeleteFile(full);
    }

    /// <summary>
    /// A file name that is safe in the staging folder: no directory part, no characters
    /// Windows refuses, no trailing dots or spaces, a bounded length that keeps the extension.
    /// </summary>
    internal static string SafeFileName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty);
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray();
        name = new string(chars).Trim().TrimEnd('.', ' ');
        if (name.Length == 0 || name.StartsWith('.'))
        {
            name = "attachment" + name;
        }

        // The Broker protects these names anywhere (harness keys, databases, runtime
        // descriptors); a copy that kept one would be unreadable to the model.
        if (name.StartsWith("agent_harness", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("harness_key", StringComparison.OrdinalIgnoreCase)
            || name.Equals("runtime_descriptor.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("broker.json", StringComparison.OrdinalIgnoreCase))
        {
            name = "attached-" + name;
        }

        if (name.Length > MaxNameLength)
        {
            var extension = Path.GetExtension(name);
            if (extension.Length > 16) extension = string.Empty;
            name = name[..(MaxNameLength - extension.Length)].TrimEnd('.', ' ') + extension;
        }

        return name;
    }

    /// <summary>
    /// The name a copy gets: the original name when free or when it already holds the same
    /// bytes, otherwise "name (2).ext", "name (3).ext" and so on.
    /// </summary>
    private static string ChooseName(string folder, string name, long length, byte[] hash)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var index = 1; index <= 100; index++)
        {
            var candidate = index == 1 ? name : $"{stem} ({index}){extension}";
            var path = Path.Combine(folder, candidate);
            if (!File.Exists(path) || SameContent(path, length, hash))
            {
                return candidate;
            }
        }

        return $"{stem} ({Guid.NewGuid():N}){extension}";
    }

    private static bool SameContent(string path, long length, byte[] hash)
    {
        try
        {
            if (new FileInfo(path).Length != length) return false;
            using var stream = File.OpenRead(path);
            return SHA256.HashData(stream).AsSpan().SequenceEqual(hash);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void EnsureIgnored(string folder)
    {
        var ignore = Path.Combine(folder, ".gitignore");
        if (!File.Exists(ignore))
        {
            File.WriteAllText(ignore, "# Files attached in Ru chats. Not part of the project.\n*\n");
        }
    }

    private static void Prune(string folder)
    {
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
        {
            if (file.Name == ".gitignore") continue;
            if (file.LastWriteTimeUtc < cutoff) TryDeleteFile(file.FullName);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover copy is ignored by git and pruned later.
        }
    }
}
