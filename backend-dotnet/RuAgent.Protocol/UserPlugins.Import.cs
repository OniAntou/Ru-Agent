namespace RuAgent.Protocol;

/// <summary>
/// Importing a plugin draft (ADR 0044 phase 4): the user points at a folder, typically one the
/// <c>create-plugin</c> skill wrote inside a workspace, and Ru copies it into
/// <c>~/.ru/plugins/&lt;id&gt;</c>. Only the user does this, through the Host; the agent cannot
/// write the plugin roots itself. The copy is validated, bounded and atomic, and the imported
/// plugin stays off until the user enables it.
/// </summary>
public static partial class UserPlugins
{
    public const int MaxImportFiles = 256;
    public const long MaxImportBytes = 64L * 1024 * 1024;

    /// <summary>Folders and files left out of an imported copy: version control and installer metadata.</summary>
    private static readonly string[] ImportSkippedNames = [".git", ".ru-plugin-package.json", ".ru-skill-package.json"];

    public static UserPlugin Import(string sourceDirectory, string? localApplicationDataRoot = null, string? userProfileRoot = null)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Path.IsPathFullyQualified(sourceDirectory))
            throw new ArgumentException("plugin_import_path_invalid", nameof(sourceDirectory));
        var source = new DirectoryInfo(Path.GetFullPath(sourceDirectory));
        if (!source.Exists) throw new ArgumentException("plugin_import_path_invalid", nameof(sourceDirectory));
        if (IsLink(source)) throw new IOException("plugin_import_unsafe");

        var manifest = ReadManifest(source.FullName, out var problem)
            ?? throw new InvalidDataException("plugin_import_invalid: " + problem);
        var root = LocalRoot(userProfileRoot);
        var target = Path.Combine(root, manifest.Id);
        if (IsAtOrBelow(source.FullName, root) || IsAtOrBelow(source.FullName, ManagedRoot(localApplicationDataRoot)))
            throw new IOException("plugin_import_already_installed");
        if (System.IO.Directory.Exists(target) || System.IO.Directory.Exists(Path.Combine(ManagedRoot(localApplicationDataRoot), manifest.Id)))
            throw new IOException("plugin_exists");

        // Check everything before writing anything: a link or an oversized draft copies nothing.
        var files = new List<(FileInfo File, string Relative)>();
        long total = 0;
        foreach (var entry in source.EnumerateFileSystemInfos("*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     AttributesToSkip = 0,
                     IgnoreInaccessible = false,
                 }))
        {
            var relative = Path.GetRelativePath(source.FullName, entry.FullName);
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => ImportSkippedNames.Contains(part, StringComparer.OrdinalIgnoreCase)))
                continue;
            if (IsLink(entry)) throw new IOException("plugin_import_unsafe");
            if (entry is not FileInfo file) continue;
            total += file.Length;
            files.Add((file, relative));
            if (files.Count > MaxImportFiles || total > MaxImportBytes) throw new IOException("plugin_import_too_large");
        }

        System.IO.Directory.CreateDirectory(root);
        if (IsLink(new DirectoryInfo(root))) throw new IOException("plugin_root_is_link");
        var staging = Path.Combine(root, $".import-{Guid.NewGuid():N}");
        try
        {
            foreach (var (file, relative) in files)
            {
                var destination = Path.Combine(staging, relative);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                file.CopyTo(destination);
            }

            System.IO.Directory.CreateDirectory(staging);
            System.IO.Directory.Move(staging, target);
        }
        finally
        {
            if (System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, recursive: true);
        }

        // A plugin with this id may have been enabled and approved before it was removed. The
        // imported copy is new content (possibly agent-written), so it starts off and unapproved.
        SetEnabled(manifest.Id, false, localApplicationDataRoot);
        SetApproval(manifest.Id, null, localApplicationDataRoot);
        return Describe(manifest, target, UserPluginSource.Local, enabled: false);
    }

    private static bool IsAtOrBelow(string candidate, string root)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(candidate, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Skills that ship with Ru (<c>skills-builtin</c> next to the Host). They are searched after
/// every other root, plugins included, so any user, project or plugin skill with the same id
/// replaces one.
/// </summary>
public static class BuiltInSkills
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "skills-builtin");
}
