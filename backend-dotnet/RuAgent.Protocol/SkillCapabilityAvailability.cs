namespace RuAgent.Protocol;

/// <summary>Bounded, file-existence-only PATH probe for the small supported skill capability set.</summary>
public static class SkillCapabilityAvailability
{
    public const int MaxSearchDirectories = 128;
    private const int MaxPathEntryLength = 260;
    private static readonly IReadOnlyDictionary<string, string[]> Executables = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["python"] = ["python.exe", "python3.exe", "py.exe", "python", "python3"],
        ["node"] = ["node.exe", "node"],
        ["dotnet"] = ["dotnet.exe", "dotnet"],
        ["git"] = ["git.exe", "git"],
    };
    private static readonly IReadOnlyDictionary<string, Lazy<bool>> Cached = Executables.Keys.ToDictionary(
        key => key, key => new Lazy<bool>(() => Probe(key, PathDirectories()), LazyThreadSafetyMode.ExecutionAndPublication), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<string> Supported => Executables.Keys.ToArray();
    public static bool IsAvailable(string capability) => Cached.TryGetValue(capability, out var available) && available.Value;

    public static bool Probe(string capability, IEnumerable<string> searchDirectories)
    {
        ArgumentNullException.ThrowIfNull(searchDirectories);
        if (!Executables.TryGetValue(capability, out var names)) return false;
        var searched = 0;
        foreach (var directory in searchDirectories)
        {
            if (searched++ >= MaxSearchDirectories) break;
            if (string.IsNullOrWhiteSpace(directory) || directory.Length > MaxPathEntryLength) continue;
            foreach (var name in names)
            {
                try { if (File.Exists(Path.Combine(directory, name))) return true; }
                catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { }
            }
        }
        return false;
    }

    private static IEnumerable<string> PathDirectories()
    {
        var raw = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        var separator = OperatingSystem.IsWindows() ? ';' : Path.PathSeparator;
        return raw.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry => entry.Length <= MaxPathEntryLength);
    }
}
