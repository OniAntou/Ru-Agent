using System.Diagnostics;
using RuAgent.Protocol.Harness;

namespace RuAgent.Client;

public sealed class PublicHostLocator
{
    private readonly string _descriptorPath;

    public PublicHostLocator(string? dataRoot = null, string? descriptorPath = null)
    {
        _descriptorPath = !string.IsNullOrWhiteSpace(descriptorPath)
            ? Path.GetFullPath(descriptorPath)
            : ResolveDescriptorPath(dataRoot);
    }

    public string DescriptorPath => _descriptorPath;

    public static string ResolveDescriptorPath(string? dataRoot)
    {
        var envDataRoot = string.IsNullOrWhiteSpace(dataRoot) ? Environment.GetEnvironmentVariable("RU_DATA_ROOT") : dataRoot;
        if (!string.IsNullOrWhiteSpace(envDataRoot))
        {
            var root = Path.GetFullPath(envDataRoot.Trim());
            return Path.Combine(root, "runtime_descriptor.json");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            localAppData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(localAppData))
            localAppData = Directory.GetCurrentDirectory();
        return Path.Combine(localAppData, "Ru", "runtime_descriptor.json");
    }

    public bool TryGetValidDescriptor(out RuntimeDescriptor? descriptor, out string? error)
    {
        descriptor = null;
        error = null;
        if (!File.Exists(_descriptorPath))
        {
            error = "not_found";
            return false;
        }

        string json;
        try { json = File.ReadAllText(_descriptorPath); }
        catch (Exception ex) { error = ex.Message; return false; }

        if (!RuntimeDescriptorValidator.TryParseStrict(json, out var parsed, out var parseError))
        {
            error = parseError;
            return false;
        }

        // Bind to PID/start-time: verify process liveness
        if (!IsProcessAlive(parsed!))
        {
            error = "stale_pid";
            return false;
        }

        descriptor = parsed;
        return true;
    }

    private static bool IsProcessAlive(RuntimeDescriptor descriptor)
    {
        try
        {
            using var process = Process.GetProcessById(descriptor.ProcessId);
            if (!DateTimeOffset.TryParse(descriptor.ProcessStartTime, out var descStart)) return false;
            var procStart = process.StartTime.ToUniversalTime();
            var descUtc = descStart.ToUniversalTime();
            var diff = (procStart - descUtc).Duration();
            return diff < TimeSpan.FromSeconds(2);
        }
        catch (ArgumentException) { return false; }
        catch { return false; }
    }
}
