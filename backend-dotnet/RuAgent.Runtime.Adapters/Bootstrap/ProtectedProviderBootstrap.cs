using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

#pragma warning disable CA1416

namespace RuAgent.Runtime.Adapters.Bootstrap;

public sealed record ProviderBootstrapData
{
    public ProviderBootstrapData(
        string backend,
        string protocol,
        Uri endpoint,
        string model,
        string credential,
        JsonObject? requestOptions = null,
        int? maximumOutputTokens = null)
    {
        Backend = Require(backend, 128, nameof(backend));
        Protocol = ProviderProtocols.Require(protocol);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(endpoint.UserInfo)
            || endpoint.ToString().Length > 2048)
        {
            throw new ArgumentException("Provider endpoint is invalid.", nameof(endpoint));
        }

        Model = Require(model, 256, nameof(model));
        Credential = Require(credential, 4096, nameof(credential), allowEmpty: true);
        Endpoint = endpoint;
        RequestOptions = ProviderRequestOptionsValidator.ValidateAndClone(protocol, requestOptions, nameof(requestOptions));
        MaximumOutputTokens = ValidateOutputCap(protocol, maximumOutputTokens);
    }

    public string Backend { get; }

    public string Protocol { get; }

    public Uri Endpoint { get; }

    public string Model { get; }

    public string Credential { get; }

    public JsonObject? RequestOptions { get; }

    public int? MaximumOutputTokens { get; }

    private static int? ValidateOutputCap(string protocol, int? value)
    {
        if (value is null)
        {
            if (protocol == ProviderProtocols.AnthropicMessages)
                throw new ArgumentException("Anthropic requires an output cap.", nameof(value));
            return null;
        }
        if (value is < 1 or > 128000) throw new ArgumentException("Output cap is invalid.", nameof(value));
        if (protocol != ProviderProtocols.AnthropicMessages) throw new ArgumentException("Only Anthropic uses an output cap.", nameof(value));
        if (value > 8192) throw new ArgumentException("Output cap exceeds 8192.", nameof(value));
        return value;
    }

    private static string Require(string? value, int maximum, string parameterName, bool allowEmpty = false)
    {
        if (value is null || value.Length > maximum || value.Contains('\0')
            || value.Any(char.IsControl) || (!allowEmpty && string.IsNullOrWhiteSpace(value)))
        {
            throw new ArgumentException("Provider bootstrap value is invalid.", parameterName);
        }

        return value;
    }
}

public static class ProtectedProviderBootstrap
{
    private const int MaxSerializedBytes = 16 * 1024;
    private const int MaxProtectedBytes = 64 * 1024;
    private const string FilePrefix = "provider-bootstrap-";

    /// <summary>
    /// Purpose-bound DPAPI additional entropy. DPAPI CurrentUser already scopes
    /// to the user on this machine; the entropy keeps the bootstrap file useless
    /// to every other consumer of that user token that does not share this exact
    /// purpose string. Bootstrap files are one-shot (written before a child
    /// starts, deleted right after the read), so the legacy fallback below only
    /// needs to cover files left behind by an upgrade, not steady state.
    /// </summary>
    public static readonly byte[] DpapiEntropy =
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("Ru.Provider.Bootstrap.v2\n"));

    public static string Write(string sessionRoot, ProviderBootstrapData data)
    {
        EnsureWindowsDpapi();
        ArgumentNullException.ThrowIfNull(data);
        var root = ResolveRoot(sessionRoot);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, FilePrefix + Guid.NewGuid().ToString("N") + ".bin");
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            backend = data.Backend,
            protocol = data.Protocol,
            endpoint = data.Endpoint.ToString(),
            model = data.Model,
            credential = data.Credential,
            request_options = data.RequestOptions?.DeepClone(),
            maximum_output_tokens = data.MaximumOutputTokens,
        });
        if (json.Length > MaxSerializedBytes)
        {
            throw new ArgumentException("Provider bootstrap exceeds its byte cap.", nameof(data));
        }

        var protectedBytes = ProtectedData.Protect(json, DpapiEntropy, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(json);
        // The owner-only DACL is applied *at creation*: hardening after the
        // write leaves a window where the credential file carries the folder's
        // inherited ACEs, which is exactly what OwnerOnlyFile documents as
        // unacceptable. FileSystemAclExtensions.Create applies the security
        // descriptor to the create call itself, so no plaintext or bare-ACL
        // state ever exists on disk.
        var fileInfo = new FileInfo(path);
        using (var stream = fileInfo.Create(
                   FileMode.CreateNew,
                   FileSystemRights.Write | FileSystemRights.Read | FileSystemRights.Delete,
                   FileShare.None,
                   4096,
                   FileOptions.WriteThrough,
                   BuildOwnerOnlyFileSecurity()))
        {
            stream.Write(protectedBytes);
            stream.Flush(flushToDisk: true);
        }

        CryptographicOperations.ZeroMemory(protectedBytes);
        return path;
    }

    public static ProviderBootstrapData ReadAndDelete(string sessionRoot, string path)
    {
        EnsureWindowsDpapi();
        var root = ResolveRoot(sessionRoot);
        var fullPath = ResolveOwnedPath(root, path);
        try
        {
            var protectedBytes = ReadBoundedBytes(fullPath, MaxProtectedBytes);
            byte[] json;
            try
            {
                json = ProtectedData.Unprotect(protectedBytes, DpapiEntropy, DataProtectionScope.CurrentUser);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // A bootstrap file written before the purpose entropy existed.
                // This file is deleted below either way, so the fallback never
                // needs to persist anything.
                json = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            }
            if (json.Length > MaxSerializedBytes)
            {
                throw new InvalidDataException("Provider bootstrap exceeds its byte cap.");
            }

            var data = JsonSerializer.Deserialize<BootstrapPayload>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                })
                ?? throw new InvalidDataException("Provider bootstrap is invalid.");
            return new ProviderBootstrapData(
                data.Backend,
                data.Protocol,
                new Uri(data.Endpoint, UriKind.Absolute),
                data.Model,
                data.Credential,
                data.RequestOptions,
                data.MaximumOutputTokens);
        }
        finally
        {
            Delete(root, fullPath);
        }
    }

    private static byte[] ReadBoundedBytes(string path, int maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        using var output = new MemoryStream(Math.Min(maximumBytes, 81920));
        var buffer = new byte[Math.Min(maximumBytes, 81920)];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length > maximumBytes - read)
                throw new InvalidDataException("Provider bootstrap exceeds its byte cap.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    public static void Delete(string sessionRoot, string path)
    {
        var root = ResolveRoot(sessionRoot);
        var fullPath = ResolveOwnedPath(root, path);
        if (!File.Exists(fullPath))
        {
            return;
        }

        try
        {
            File.SetAttributes(fullPath, FileAttributes.Normal);
        }
        catch (FileNotFoundException)
        {
            return;
        }

        File.Delete(fullPath);
    }

    private static string ResolveRoot(string sessionRoot)
    {
        if (string.IsNullOrWhiteSpace(sessionRoot))
        {
            throw new ArgumentException("Session root is required.", nameof(sessionRoot));
        }

        var root = Path.GetFullPath(sessionRoot);
        if (File.Exists(root) || (Directory.Exists(root) && IsReparsePoint(root)))
        {
            throw new ArgumentException("Session root must be an owned directory.", nameof(sessionRoot));
        }

        return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string ResolveOwnedPath(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Bootstrap path is required.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        var prefix = root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Bootstrap path is outside the owned session root.", nameof(path));
        }

        if (File.Exists(fullPath) && IsReparsePoint(fullPath))
        {
            throw new ArgumentException("Bootstrap path cannot be a filesystem link.", nameof(path));
        }

        return fullPath;
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static FileSecurity BuildOwnerOnlyFileSecurity()
    {
        var owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Current Windows identity is unavailable.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Delete,
            AccessControlType.Allow));
        return security;
    }

    private static void EnsureWindowsDpapi()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Provider bootstrap protection requires Windows DPAPI.");
        }
    }

    private sealed record BootstrapPayload(
        string Backend,
        string Protocol,
        string Endpoint,
        string Model,
        string Credential,
        [property: JsonPropertyName("request_options")] JsonObject? RequestOptions,
        [property: JsonPropertyName("maximum_output_tokens")] int? MaximumOutputTokens);
}

#pragma warning restore CA1416
