using System.Text;

namespace RuApp.Services;

/// <summary>What the editor loaded: the text and the file stamp it was read at.</summary>
public sealed record InstructionFileSnapshot(string Path, string Text, bool Exists, DateTime? LastWriteUtc, long? Length);

public enum InstructionSaveOutcome
{
    Saved,
    ChangedOnDisk,
    TooLarge,
    Unsafe,
    Failed,
}

/// <summary>
/// Reads and writes the user's own instruction files, which the Host loads into the
/// agent's prompt as guidance (never as permission): <c>%USERPROFILE%\.ru\AGENTS.md</c>
/// for every project, and <c>&lt;workspace&gt;\.ru\AGENTS.md</c> for one workspace. The
/// project's shared root <c>AGENTS.md</c> is never written here.
/// </summary>
/// <remarks>
/// Limits mirror <c>RuInstructionLoader</c> in RuBackend, which the GUI cannot
/// reference: a file above <see cref="MaxBytes"/> is skipped as oversize, so the
/// editor refuses to save one.
/// </remarks>
public static class InstructionFileStore
{
    /// <summary>RuInstructionLoader.MaxInstructionBytes: the per-file limit the Host enforces.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>RuInstructionLoader.MaxCombinedInstructionBytes: all instruction sources together.</summary>
    public const int MaxCombinedBytes = 256 * 1024;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string GlobalPath(string? userProfile = null) =>
        System.IO.Path.Combine(userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ru", "AGENTS.md");

    public static string? WorkspacePath(string? workspace) =>
        string.IsNullOrWhiteSpace(workspace) ? null : System.IO.Path.Combine(workspace, ".ru", "AGENTS.md");

    /// <summary>The other workspace sources the Host reads, shown read-only so the user sees every input.</summary>
    public static IReadOnlyList<(string Name, bool Exists)> OtherWorkspaceSources(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace)) return Array.Empty<(string, bool)>();
        return new[] { "AGENTS.md", "CONTEXT.md", System.IO.Path.Combine(".ru", "CONTEXT.md") }
            .Select(relative => (relative.Replace('\\', '/'), File.Exists(System.IO.Path.Combine(workspace, relative))))
            .ToArray();
    }

    /// <summary>
    /// The editor's text box hands back bare carriage returns; the file keeps plain
    /// newlines so every reader sees the same lines.
    /// </summary>
    public static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    public static int ByteCount(string text) => Utf8NoBom.GetByteCount(NormalizeNewlines(text));

    public static InstructionFileSnapshot Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return new InstructionFileSnapshot(path, string.Empty, false, null, null);
        var text = File.ReadAllText(path, Encoding.UTF8);
        return new InstructionFileSnapshot(path, text, true, info.LastWriteTimeUtc, info.Length);
    }

    /// <summary>
    /// Saves atomically (temporary file in the same folder, then a replace), as UTF-8
    /// without a byte-order mark. Refuses when the file changed since it was read, unless
    /// <paramref name="overwrite"/>; refuses to write through a link, because the Host
    /// skips an instruction file whose final path leaves its root.
    /// </summary>
    public static InstructionSaveOutcome Save(InstructionFileSnapshot loaded, string text, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        var normalized = NormalizeNewlines(text);
        if (Utf8NoBom.GetByteCount(normalized) > MaxBytes) return InstructionSaveOutcome.TooLarge;
        try
        {
            var info = new FileInfo(loaded.Path);
            var directory = info.Directory!;
            if (IsLink(info) || (directory.Exists && IsLink(directory))) return InstructionSaveOutcome.Unsafe;
            if (!overwrite && (info.Exists != loaded.Exists
                || (info.Exists && (info.LastWriteTimeUtc != loaded.LastWriteUtc || info.Length != loaded.Length))))
                return InstructionSaveOutcome.ChangedOnDisk;

            directory.Create();
            var temporary = System.IO.Path.Combine(directory.FullName, $".{info.Name}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, normalized, Utf8NoBom);
                File.Move(temporary, info.FullName, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }

            return InstructionSaveOutcome.Saved;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[Instructions] Save failed: {error.GetType().Name}");
            return InstructionSaveOutcome.Failed;
        }
    }

    private static bool IsLink(FileSystemInfo info) =>
        info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0;
}
