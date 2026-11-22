namespace RuAgent.Broker.Tools.Browser;

/// <summary>The automation protocol family a discovered browser speaks.</summary>
public enum BrowserFamily
{
    /// <summary>Chrome DevTools Protocol: Edge, Chrome, Brave, Vivaldi, Opera, Chromium, Arc, â€¦</summary>
    Chromium,

    /// <summary>WebDriver BiDi: Firefox 129 and newer expose it directly.</summary>
    Firefox,
}

/// <summary>One installed browser executable and the protocol family it belongs to.</summary>
public sealed record BrowserTarget(string ExecutablePath, BrowserFamily Family)
{
    public string FileName => Path.GetFileName(ExecutablePath);
}

/// <summary>
/// Finds the browser the built-in tool should drive (ADR 0055, amended 2026-11-22). An explicit
/// executable wins, then <c>RU_BROWSER_EXE</c>, then known install paths. The Chromium family is
/// preferred because it is the CDP path; Firefox is chosen when no Chromium browser is installed
/// or when the override names it. Nothing is inferred from a process currently running.
/// </summary>
public static class BrowserExecutableDiscovery
{
    /// <summary>Environment override accepted by every engine selection path.</summary>
    public const string ExecutableOverrideVariable = "RU_BROWSER_EXE";

    private const string FirefoxFileName = "firefox.exe";

    /// <summary>The executable to use, or null when no supported browser is installed.</summary>
    public static BrowserTarget? Resolve(string? preferredExecutable = null)
    {
        foreach (var candidate in new[] { preferredExecutable, Environment.GetEnvironmentVariable(ExecutableOverrideVariable) })
        {
            if (Installed(candidate) is { } overridden) return overridden;
        }

        foreach (var candidate in Candidates())
        {
            if (Installed(candidate.ExecutablePath) is { } discovered) return discovered;
        }

        return null;
    }

    /// <summary>All installed candidates, Chromium family first, without duplicates.</summary>
    public static IReadOnlyList<BrowserTarget> Installed()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<BrowserTarget>();
        foreach (var candidate in Candidates())
        {
            if (Installed(candidate.ExecutablePath) is { } target && seen.Add(target.ExecutablePath)) result.Add(target);
        }

        return result;
    }

    /// <summary>The Chromium executable to use, or null when none is installed.</summary>
    public static string? FindChromiumExecutable(string? preferredExecutable = null) =>
        ResolveFamily(preferredExecutable, BrowserFamily.Chromium);

    /// <summary>The Firefox executable to use, or null when Firefox is not installed.</summary>
    public static string? FindFirefoxExecutable(string? preferredExecutable = null) =>
        ResolveFamily(preferredExecutable, BrowserFamily.Firefox);

    /// <summary>Classifies one executable path by file name; unknown names are treated as Chromium.</summary>
    public static BrowserFamily Classify(string executablePath) =>
        string.Equals(Path.GetFileName(executablePath), FirefoxFileName, StringComparison.OrdinalIgnoreCase)
            ? BrowserFamily.Firefox
            : BrowserFamily.Chromium;

    private static string? ResolveFamily(string? preferredExecutable, BrowserFamily family)
    {
        foreach (var candidate in new[] { preferredExecutable, Environment.GetEnvironmentVariable(ExecutableOverrideVariable) })
        {
            if (Installed(candidate) is { } overridden && overridden.Family == family) return overridden.ExecutablePath;
        }

        foreach (var candidate in Candidates())
        {
            if (candidate.Family == family && Installed(candidate.ExecutablePath) is { } discovered) return discovered.ExecutablePath;
        }

        return null;
    }

    private static BrowserTarget? Installed(string? executablePath) =>
        string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath)
            ? null
            : new BrowserTarget(Path.GetFullPath(executablePath), Classify(executablePath));

    private static IEnumerable<BrowserTarget> Candidates()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // Most common first inside each family; the Chromium family precedes Firefox because its
        // CDP path is the one the tool has the most coverage for.
        yield return Chromium(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe");
        yield return Chromium(programFiles, "Microsoft", "Edge", "Application", "msedge.exe");
        yield return Chromium(localAppData, "Microsoft", "Edge", "Application", "msedge.exe");
        yield return Chromium(programFiles, "Google", "Chrome", "Application", "chrome.exe");
        yield return Chromium(programFilesX86, "Google", "Chrome", "Application", "chrome.exe");
        yield return Chromium(localAppData, "Google", "Chrome", "Application", "chrome.exe");
        yield return Chromium(programFiles, "BraveSoftware", "Brave-Browser", "Application", "brave.exe");
        yield return Chromium(localAppData, "BraveSoftware", "Brave-Browser", "Application", "brave.exe");
        yield return Chromium(programFiles, "Vivaldi", "Application", "vivaldi.exe");
        yield return Chromium(localAppData, "Vivaldi", "Application", "vivaldi.exe");
        yield return Chromium(localAppData, "Programs", "Opera", "opera.exe");
        yield return Chromium(programFiles, "Opera", "opera.exe");
        yield return Chromium(programFilesX86, "Opera", "opera.exe");
        yield return Chromium(localAppData, "Chromium", "Application", "chrome.exe");
        yield return Chromium(programFiles, "Chromium", "Application", "chrome.exe");
        yield return Chromium(localAppData, "Programs", "Arc", "Arc.exe");
        yield return Firefox(programFiles, "Mozilla Firefox", "firefox.exe");
        yield return Firefox(programFilesX86, "Mozilla Firefox", "firefox.exe");
        yield return Firefox(localAppData, "Mozilla Firefox", "firefox.exe");
    }

    private static BrowserTarget Chromium(string root, params string[] parts) =>
        new(Path.Combine([root, .. parts]), BrowserFamily.Chromium);

    private static BrowserTarget Firefox(string root, params string[] parts) =>
        new(Path.Combine([root, .. parts]), BrowserFamily.Firefox);
}
