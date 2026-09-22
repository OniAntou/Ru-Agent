using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using RuAgent.Protocol.Harness;

namespace RuAgent.Hosting;

public sealed class RuntimeDescriptorWriter
{
    private const int MaximumDescriptorBytes = 1 * 1024 * 1024;
    private readonly string _descriptorPath;

    public RuntimeDescriptorWriter(string descriptorPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptorPath);
        _descriptorPath = Path.GetFullPath(descriptorPath);
    }

    public string DescriptorPath => _descriptorPath;

    public async Task WriteAsync(RuntimeDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ValidateDescriptor(descriptor);

        var directory = Path.GetDirectoryName(_descriptorPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(descriptor, RuntimeDescriptor.JsonOptions);
        // Ensure no forbidden fields leak
        if (json.Contains("worker_token", StringComparison.OrdinalIgnoreCase) ||
            json.Contains("broker", StringComparison.OrdinalIgnoreCase) ||
            json.Contains("executable", StringComparison.OrdinalIgnoreCase) ||
            json.Contains("provider", StringComparison.OrdinalIgnoreCase) ||
            json.Contains("OPENAI", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Descriptor serialization leaked forbidden field.");
        }

        var tempPath = _descriptorPath + ".tmp-" + Guid.NewGuid().ToString("N");
        await OwnerOnlyFile.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);

        try
        {
            // Atomic replace: move temp over target
            // Ensure atomic via File.Move with overwrite (NET 6+)
            File.Move(tempPath, _descriptorPath, overwrite: true);

            if (OperatingSystem.IsWindows())
            {
                OwnerOnlyFile.RestrictToCurrentUser(_descriptorPath);
            }
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    public bool TryReadValid(out RuntimeDescriptor? descriptor, out string? error)
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
            var json = ReadBoundedText(_descriptorPath);
            descriptor = RuntimeDescriptorValidator.ParseStrict(json);
            // Additional bind check: PID/start-time
            if (!IsProcessBound(descriptor))
            {
                error = "stale_pid";
                descriptor = null;
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            descriptor = null;
            return false;
        }
    }

    public void Cleanup()
    {
        try
        {
            if (File.Exists(_descriptorPath))
                File.Delete(_descriptorPath);
        }
        catch { }
    }

    public bool IsStale()
    {
        if (!File.Exists(_descriptorPath)) return true;
        try
        {
            var json = ReadBoundedText(_descriptorPath);
            var descriptor = RuntimeDescriptorValidator.ParseStrict(json);
            return !IsProcessBound(descriptor);
        }
        catch
        {
            return true;
        }
    }

    private static void ValidateDescriptor(RuntimeDescriptor descriptor)
    {
        if (descriptor.ProtocolVersion != RuntimeDescriptor.CurrentProtocolVersion)
            throw new ArgumentException("unsupported_protocol_version");
        if (string.IsNullOrWhiteSpace(descriptor.BaseUrl) || !Uri.TryCreate(descriptor.BaseUrl, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme != Uri.UriSchemeHttp)
            throw new ArgumentException("base_url must be loopback http");
        if (uri.Port is < 1024 or > 65535) throw new ArgumentException("base_url port invalid");
        if (string.IsNullOrWhiteSpace(descriptor.BearerToken)) throw new ArgumentException("bearer_token required");
        if (descriptor.ProcessId <= 0) throw new ArgumentException("pid invalid");
        if (string.IsNullOrWhiteSpace(descriptor.ProcessStartTime) || !DateTimeOffset.TryParse(descriptor.ProcessStartTime, out _))
            throw new ArgumentException("process_start_time invalid");
        if (string.IsNullOrWhiteSpace(descriptor.InstanceNonce) || descriptor.InstanceNonce.Length != 32)
            throw new ArgumentException("instance_nonce invalid");
        if (string.IsNullOrWhiteSpace(descriptor.CreatedAt) || !DateTimeOffset.TryParse(descriptor.CreatedAt, out _))
            throw new ArgumentException("created_at invalid");
    }

    private static string ReadBoundedText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        using var output = new MemoryStream(Math.Min(MaximumDescriptorBytes, 81920));
        var buffer = new byte[Math.Min(MaximumDescriptorBytes, 81920)];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length > MaximumDescriptorBytes - read)
                throw new InvalidDataException("descriptor_too_large");
            output.Write(buffer, 0, read);
        }
        using var text = new StreamReader(
            new MemoryStream(output.ToArray(), writable: false),
            System.Text.Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return text.ReadToEnd();
    }

    private static bool IsProcessBound(RuntimeDescriptor descriptor)
    {
        try
        {
            using var process = Process.GetProcessById(descriptor.ProcessId);
            // Compare start time with tolerance: descriptor's start time is ISO string; process.StartTime is local.
            if (!DateTimeOffset.TryParse(descriptor.ProcessStartTime, out var descriptorStart))
                return false;
            var processStart = process.StartTime.ToUniversalTime();
            var descStartUtc = descriptorStart.ToUniversalTime();
            // Allow 2 seconds drift due to serialization precision
            var diff = (processStart - descStartUtc).Duration();
            return diff < TimeSpan.FromSeconds(2);
        }
        catch (ArgumentException)
        {
            return false; // PID not found
        }
        catch
        {
            return false;
        }
    }

    private static void RestrictToCurrentUser(string path) =>
        OwnerOnlyFile.RestrictToCurrentUser(path);
}

public static class RuntimeDescriptorValidator
{
    public static RuntimeDescriptor ParseStrict(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("invalid_descriptor");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new ArgumentException("invalid_json: " + ex.Message); }
        using var documentLease = doc;

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("descriptor must be object");

        var fields = root.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        if (!fields.SetEquals(RuntimeDescriptor.AllowedFields))
        {
            var extra = fields.Except(RuntimeDescriptor.AllowedFields).FirstOrDefault();
            var missing = RuntimeDescriptor.AllowedFields.Except(fields).FirstOrDefault();
            if (extra is not null) throw new ArgumentException($"unknown_field:{extra}");
            if (missing is not null) throw new ArgumentException($"missing_field:{missing}");
        }

        // Extract and validate each
        if (!root.TryGetProperty("protocol_version", out var vProp) || vProp.ValueKind != JsonValueKind.Number || !vProp.TryGetInt32(out var version))
            throw new ArgumentException("protocol_version invalid");
        if (version != RuntimeDescriptor.CurrentProtocolVersion)
            throw new ArgumentException("unsupported_protocol_version");

        if (!root.TryGetProperty("base_url", out var urlProp) || urlProp.ValueKind != JsonValueKind.String)
            throw new ArgumentException("base_url invalid");
        var baseUrl = urlProp.GetString()!;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || !uri.IsLoopback || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("base_url must be loopback http");
        if (uri.Port is < 1024 or > 65535)
            throw new ArgumentException("base_url port invalid");

        if (!root.TryGetProperty("bearer_token", out var bearerProp) || bearerProp.ValueKind != JsonValueKind.String)
            throw new ArgumentException("bearer_token invalid");
        var bearer = bearerProp.GetString()!;
        if (string.IsNullOrWhiteSpace(bearer) || bearer.Length > 4096)
            throw new ArgumentException("bearer_token invalid");

        if (!root.TryGetProperty("pid", out var pidProp) || pidProp.ValueKind != JsonValueKind.Number || !pidProp.TryGetInt32(out var pid) || pid <= 0)
            throw new ArgumentException("pid invalid");

        if (!root.TryGetProperty("process_start_time", out var startProp) || startProp.ValueKind != JsonValueKind.String)
            throw new ArgumentException("process_start_time invalid");
        var startTime = startProp.GetString()!;
        if (!DateTimeOffset.TryParse(startTime, out _))
            throw new ArgumentException("process_start_time invalid");

        if (!root.TryGetProperty("instance_nonce", out var nonceProp) || nonceProp.ValueKind != JsonValueKind.String)
            throw new ArgumentException("instance_nonce invalid");
        var nonce = nonceProp.GetString()!;
        if (string.IsNullOrWhiteSpace(nonce) || nonce.Length != 32)
            throw new ArgumentException("instance_nonce invalid");

        if (!root.TryGetProperty("created_at", out var createdProp) || createdProp.ValueKind != JsonValueKind.String)
            throw new ArgumentException("created_at invalid");
        var created = createdProp.GetString()!;
        if (!DateTimeOffset.TryParse(created, out _))
            throw new ArgumentException("created_at invalid");

        return new RuntimeDescriptor(version, baseUrl, bearer, pid, startTime, nonce, created);
    }
}
