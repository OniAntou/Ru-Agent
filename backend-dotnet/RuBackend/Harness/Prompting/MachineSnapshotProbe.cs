using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace RuBackend.Harness.Prompting;

/// <summary>
/// One bounded, execution-free description of the machine the run is on: OS,
/// architecture, cores, memory visible to .NET, the runtime, and the shells and
/// helper tools already resolved on PATH. Versions come from file metadata, never
/// from running the programs, so this cannot become a second execution path.
/// </summary>
public static class MachineSnapshotProbe
{
    internal const int MaxSnapshotBytes = 4 * 1024;

    /// <summary>
    /// The snapshot text, or null when the machine cannot be described. The prompt
    /// provider caches one reading per run, so an install mid-run appears next run.
    /// </summary>
    public static string? Read() => ReadCore();

    /// <summary>Exposed for tests that need a deterministic rendering.</summary>
    internal static string Render(
        string osDescription,
        string architecture,
        int processorCount,
        long availableMemoryBytes,
        string runtime,
        IEnumerable<string> shells,
        IEnumerable<string> tools)
    {
        var builder = new StringBuilder(512);
        builder.AppendLine("machine:");
        builder.AppendLine($"  os: {osDescription.Trim()} ({architecture})");
        builder.AppendLine($"  cpu: {processorCount.ToString(CultureInfo.InvariantCulture)} logical processor(s)");
        if (availableMemoryBytes > 0)
        {
            var gib = Math.Round(availableMemoryBytes / (double)(1024 * 1024 * 1024), 1);
            builder.AppendLine($"  memory: {gib.ToString("0.0", CultureInfo.InvariantCulture)} GiB visible to .NET");
        }

        builder.AppendLine($"  runtime: {runtime}");
        var shellList = shells.Where(item => item.Length > 0).ToArray();
        if (shellList.Length > 0)
        {
            builder.AppendLine($"  shells: {string.Join(", ", shellList)}");
        }

        var toolList = tools.Where(item => item.Length > 0).ToArray();
        if (toolList.Length > 0)
        {
            builder.AppendLine($"  tools: {string.Join(", ", toolList)}");
        }

        return Fit(builder.ToString().TrimEnd());
    }

    /// <summary>
    /// Keeps one reference-sized note. File metadata is normally short, but a
    /// pathological version string must not exceed the reference bound.
    /// </summary>
    private static string Fit(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) <= MaxSnapshotBytes)
        {
            return text;
        }

        const string note = "\n…(machine snapshot truncated at its byte budget)";
        var budget = MaxSnapshotBytes - Encoding.UTF8.GetByteCount(note);
        int low = 0, high = text.Length;
        while (high - low > 1)
        {
            var middle = low + ((high - low) / 2);
            if (Encoding.UTF8.GetByteCount(text.AsSpan(0, middle)) > budget)
            {
                high = middle;
            }
            else
            {
                low = middle;
            }
        }

        if (low > 0 && char.IsHighSurrogate(text[low - 1]))
        {
            low--;
        }

        return text[..low] + note;
    }

    private static string? ReadCore()
    {
        try
        {
            return Render(
                RuntimeInformation.OSDescription,
                RuntimeInformation.OSArchitecture.ToString(),
                Environment.ProcessorCount,
                GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                $".NET {Environment.Version} ({RuntimeInformation.ProcessArchitecture})",
                new[] { "powershell", "pwsh", "cmd" }.Select(name => DescribeExecutable(name)).OfType<string>(),
                new[] { "git", "rg", "node", "python", "dotnet" }.Select(name => DescribeTool(name)).OfType<string>());
        }
        catch (Exception)
        {
            // Context only: an unreadable environment omits the snapshot.
            return null;
        }
    }

    private static string? DescribeTool(string name)
    {
        if (name.Equals("dotnet", StringComparison.Ordinal))
        {
            var dotnet = ToolInventoryProbe.ResolvePath(name);
            var sdk = dotnet is null ? null : DescribeDotnetSdk(dotnet);
            return sdk is null ? DescribeExecutable(name) : $"dotnet {sdk} (SDK)";
        }

        return DescribeExecutable(name);
    }

    private static string? DescribeExecutable(string name)
    {
        var path = ToolInventoryProbe.ResolvePath(name);
        if (path is null)
        {
            return null;
        }

        var version = FileVersionOf(path);
        return version is null ? name : $"{name} {version}";
    }

    private static string? FileVersionOf(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var version = string.IsNullOrWhiteSpace(info.ProductVersion) ? info.FileVersion : info.ProductVersion;
            if (string.IsNullOrWhiteSpace(version))
            {
                return null;
            }

            // ProductVersion may carry a commit or a descriptive suffix; the version
            // is the first whitespace-delimited token.
            var first = version.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return string.IsNullOrWhiteSpace(first) ? null : first.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? DescribeDotnetSdk(string dotnetExecutable)
    {
        var sdkRoot = Path.Combine(Path.GetDirectoryName(dotnetExecutable) ?? string.Empty, "sdk");
        if (!Directory.Exists(sdkRoot))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateDirectories(sdkRoot)
                .Select(Path.GetFileName)
                .Where(name => Version.TryParse(name, out _))
                .Select(name => name!)
                .OrderByDescending(name => Version.Parse(name))
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
