namespace RuApp.Services;

/// <summary>Presentation only: hides installer-owned payload, never user files by extension.</summary>
internal sealed class InstalledFileTreeFilter
{
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private int _remainingChecks = 2048;

    private InstalledFileTreeFilter(string root, IEnumerable<string> inventory)
    {
        foreach (string entry in inventory.Append("ru-files.lst").Append("ru-install.json")
            .Append("unins000.exe").Append("unins000.dat"))
        {
            if (string.IsNullOrWhiteSpace(entry) || Path.IsPathRooted(entry) || entry.Contains(':')) continue;
            string path;
            try { path = Path.GetFullPath(Path.Combine(root, entry)); }
            catch (ArgumentException) { continue; }
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            _files.Add(path);
            for (string? parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            {
                _directories.Add(parent);
                if (string.Equals(parent, root, StringComparison.OrdinalIgnoreCase)) break;
            }
        }
    }

    public static InstalledFileTreeFilter? ForDirectory(string directory)
    {
        // Expanded rows still need the installation's inventory, not a new set
        // based on whatever happens to be present in the application directory.
        for (var candidate = new DirectoryInfo(directory); candidate is not null; candidate = candidate.Parent)
        {
            string inventory = Path.Combine(candidate.FullName, "ru-files.lst");
            if (!File.Exists(Path.Combine(candidate.FullName, "ru-install.json"))) continue;
            try
            {
                if (!File.Exists(inventory) || new FileInfo(inventory).Length > 4 * 1024 * 1024) return null;
                return new InstalledFileTreeFilter(Path.TrimEndingDirectorySeparator(candidate.FullName), File.ReadAllLines(inventory));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Without a readable inventory, keep files visible rather than
                // guessing which ones belong to the installer.
                return null;
            }
        }
        return null;
    }

    public bool Hides(string path, bool directory)
    {
        if (!directory) return _files.Contains(path);
        if (!_directories.Contains(path)) return false;
        try
        {
            foreach (string child in Directory.EnumerateFileSystemEntries(path))
            {
                // A bounded inspection may leave an application folder visible;
                // it must never hide a subtree containing unknown user content.
                if (--_remainingChecks < 0) return false;
                var attributes = File.GetAttributes(child);
                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (FileTreeLister.IsSkipped(Path.GetFileName(child), isDirectory)) continue;
                if ((attributes & FileAttributes.ReparsePoint) != 0 || !Hides(child, isDirectory)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
