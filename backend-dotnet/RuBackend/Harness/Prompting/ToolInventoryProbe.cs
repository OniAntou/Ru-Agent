namespace RuBackend.Harness.Prompting;

/// <summary>
/// What shells and helper tools this machine actually has, resolved by PATH
/// probing with no execution. Re-resolved every run so a mid-session install
/// shows up on the next prompt, and every user gets their own truth instead
/// of the model guessing pwsh exists.
/// </summary>
public static class ToolInventoryProbe
{
    private static readonly string[] Shells = ["pwsh", "powershell", "cmd"];

    private static readonly string[] Tools = ["git", "rg", "node", "python", "dotnet"];

    private static readonly string[] WindowsExtensions = [".exe", ".cmd", ".bat"];

    private const int MaxSearchDirectories = 128;

    public static string Probe(IReadOnlyList<string>? searchDirectories = null)
    {
        var directories = (searchDirectories ?? PathDirectories()).Take(MaxSearchDirectories).ToArray();
        var shells = Shells.Where(name => ResolvePath(name, directories) is not null).ToArray();
        var tools = Tools.Where(name => ResolvePath(name, directories) is not null).ToArray();
        var missing = Shells.Concat(Tools).Except(shells, StringComparer.Ordinal).Except(tools, StringComparer.Ordinal).ToArray();
        return $"shells: {Join(shells)}; tools: {Join(tools)}; missing: {Join(missing)}";
    }

    /// <summary>
    /// Resolves one executable on PATH without running it. Used by the machine
    /// snapshot for file-version metadata; a missing or malformed entry answers null.
    /// </summary>
    internal static string? ResolvePath(string name, IReadOnlyList<string>? searchDirectories = null)
    {
        var directories = (searchDirectories ?? PathDirectories()).Take(MaxSearchDirectories).ToArray();
        foreach (var directory in directories)
        {
            try
            {
                var direct = Path.Combine(directory, name);
                if (File.Exists(direct))
                {
                    return direct;
                }

                foreach (var extension in WindowsExtensions)
                {
                    var candidate = Path.Combine(directory, name + extension);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch
            {
                // A malformed PATH entry skips instead of failing the prompt.
            }
        }

        return null;
    }

    private static string Join(string[] names) => names.Length == 0 ? "none" : string.Join(", ", names);

    private static IReadOnlyList<string> PathDirectories()
    {
        var raw = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        var directories = new List<string>();
        foreach (var entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entry.Length > 260)
            {
                continue;
            }

            directories.Add(entry);
        }

        return directories;
    }
}
