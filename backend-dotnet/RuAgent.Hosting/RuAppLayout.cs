namespace RuAgent.Hosting;

/// <summary>
/// Locates the sibling executables and the working root, for both ways Ru is laid
/// out on disk.
/// </summary>
/// <remarks>
/// <para>
/// A <b>flat layout</b> is a distribution: every executable sits beside RuApp.exe
/// in one directory, and that directory is what the user was given. A <b>repo
/// layout</b> is a build tree: the GUI lives under
/// <c>gui-winui3/bin/Release/&lt;tfm&gt;/&lt;rid&gt;/publish</c> and each service under its
/// own project's publish directory, with the repository root above them.
/// </para>
/// <para>
/// Resolution order puts the flat layout first, because a distribution must not
/// depend on repository structure. The repo layout stays as the fallback so an
/// existing developer checkout keeps working unchanged, and each path can be
/// overridden by environment variable for tests and for a custom install.
/// </para>
/// </remarks>
public static class RuAppLayout
{
    /// <summary>The host executable name shared by both layouts.</summary>
    public const string HostExecutableName = "RuBackend.exe";

    /// <summary>The broker executable name shared by both layouts.</summary>
    public const string BrokerExecutableName = "RuAgent.Broker.exe";

    /// <summary>
    /// True when every executable of the runtime sits beside the running assembly.
    /// </summary>
    public static bool IsFlatLayout(string appBaseDirectory) =>
        !string.IsNullOrWhiteSpace(appBaseDirectory)
        && File.Exists(Path.Combine(appBaseDirectory, HostExecutableName))
        && File.Exists(Path.Combine(appBaseDirectory, BrokerExecutableName));

    /// <summary>
    /// Resolves the host executable: an explicit override, then a sibling of the
    /// running assembly, then the repository build output.
    /// </summary>
    public static string? ResolveHostExecutable(string? projectRoot, string appBaseDirectory)
    {
        var overridden = Environment.GetEnvironmentVariable("RU_AGENT_HOST_EXE");
        if (!string.IsNullOrWhiteSpace(overridden) && File.Exists(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        if (!string.IsNullOrWhiteSpace(appBaseDirectory))
        {
            var sibling = Path.Combine(appBaseDirectory, HostExecutableName);
            if (File.Exists(sibling))
            {
                return Path.GetFullPath(sibling);
            }
        }

        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            foreach (var candidate in RepoHostCandidates(projectRoot))
            {
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the broker executable: an explicit override, then a sibling, then the
    /// repository build output.
    /// </summary>
    public static string? ResolveBrokerExecutable(string? projectRoot, string appBaseDirectory)
    {
        var overridden = Environment.GetEnvironmentVariable("RU_AGENT_BROKER_EXE");
        if (!string.IsNullOrWhiteSpace(overridden) && File.Exists(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        if (!string.IsNullOrWhiteSpace(appBaseDirectory))
        {
            var sibling = Path.Combine(appBaseDirectory, BrokerExecutableName);
            if (File.Exists(sibling))
            {
                return Path.GetFullPath(sibling);
            }
        }

        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            foreach (var candidate in RepoBrokerCandidates(projectRoot))
            {
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Chooses the working root the runtime should adopt.
    /// </summary>
    /// <remarks>
    /// A flat install has no repository, so the application directory itself becomes
    /// the root. That matters beyond path resolution: the host records the root and
    /// derives its working directory from it, and returning null there makes the GUI
    /// refuse to start at all.
    /// </remarks>
    public static string ResolveWorkingRoot(string? projectRoot, string appBaseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(projectRoot) && Directory.Exists(projectRoot))
        {
            return Path.GetFullPath(projectRoot);
        }

        if (!string.IsNullOrWhiteSpace(appBaseDirectory) && Directory.Exists(appBaseDirectory))
        {
            return Path.GetFullPath(appBaseDirectory);
        }

        return Directory.GetCurrentDirectory();
    }

    private static IEnumerable<string> RepoHostCandidates(string projectRoot)
    {
        yield return Path.Combine(projectRoot, "backend-dotnet", "RuBackend", "bin", "Release", "net10.0", "win-x64", "publish", HostExecutableName);
        yield return Path.Combine(projectRoot, "backend-dotnet", "RuBackend", "bin", "Release", "net10.0", HostExecutableName);
    }

    private static IEnumerable<string> RepoBrokerCandidates(string projectRoot)
    {
        yield return Path.Combine(projectRoot, "backend-dotnet", "RuAgent.Broker", "bin", "Release", "net10.0", "win-x64", "publish", BrokerExecutableName);
        yield return Path.Combine(projectRoot, "backend-dotnet", "RuAgent.Broker", "bin", "Release", "net10.0", BrokerExecutableName);
    }
}
