namespace RuAgent.Protocol;

/// <summary>
/// File-existence-only check for a Python interpreter on PATH: python.exe,
/// python3.exe, or the Windows py.exe launcher. Nothing is spawned, the PATH
/// scan is bounded, and the process-wide answer is cached after the first probe.
/// </summary>
public static class PythonAvailability
{
    public const int MaxSearchDirectories = 128;
    private const int MaxPathEntryLength = 260;
    private static readonly string[] ExecutableNames = ["python.exe", "python3.exe", "py.exe"];
    private static readonly Lazy<bool> Cached = new(() => Probe(PathDirectories()));

    public static bool IsAvailable => Cached.Value;

    public static bool Probe(IEnumerable<string> searchDirectories)
    {
        var searched = 0;
        foreach (var directory in searchDirectories)
        {
            if (searched >= MaxSearchDirectories)
            {
                break;
            }

            searched++;
            foreach (var name in ExecutableNames)
            {
                try
                {
                    if (File.Exists(Path.Combine(directory, name)))
                    {
                        return true;
                    }
                }
                catch
                {
                    // A malformed or unreadable PATH entry skips instead of failing the check.
                }
            }
        }

        return false;
    }

    private static IEnumerable<string> PathDirectories()
    {
        var raw = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        return raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry => entry.Length <= MaxPathEntryLength);
    }
}
