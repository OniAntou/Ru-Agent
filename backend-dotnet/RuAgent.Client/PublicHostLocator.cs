using System.Diagnostics;
using System.Text;
using RuAgent.Protocol.Harness;

namespace RuAgent.Client;

public sealed class PublicHostLocator
{
    private const long MaximumDescriptorBytes = 1 * 1024 * 1024;
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

        try
        {
            if (new FileInfo(_descriptorPath).Length > MaximumDescriptorBytes)
            {
                error = "too_large";
                return false;
            }
        }
        catch (IOException ex)
        {
            error = ex.Message;
            return false;
        }

        string json;
        try { json = ReadBoundedText(_descriptorPath, checked((int)MaximumDescriptorBytes)); }
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

    private static string ReadBoundedText(string path, int maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        using var output = new MemoryStream(Math.Min(maximumBytes, 81920));
        var buffer = new byte[Math.Min(maximumBytes, 81920)];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length > maximumBytes - read)
                throw new InvalidDataException("descriptor_too_large");
            output.Write(buffer, 0, read);
        }
        using var text = new StreamReader(new MemoryStream(output.ToArray(), writable: false), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return text.ReadToEnd();
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
