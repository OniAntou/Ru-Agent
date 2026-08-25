using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace RuAgent.Protocol;

/// <summary>
/// Produces a deterministic, collision-resistant fingerprint for a fully resolved
/// terminal execution identity. JSON array framing preserves argument boundaries,
/// including control characters that are legal inside an individual argument.
/// </summary>
public static class TerminalIntentFingerprint
{
    public const string Version = "2";

    public static string Compute(
        string scope,
        string resolvedExecutable,
        IReadOnlyList<string> arguments,
        string canonicalWorkingDirectory,
        bool networkIntent,
        int timeoutMs,
        TerminalOperationClass operationClass)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedExecutable);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalWorkingDirectory);

        return ComputeCore(
            [Version, scope],
            resolvedExecutable,
            arguments,
            canonicalWorkingDirectory,
            networkIntent,
            timeoutMs,
            operationClass);
    }

    /// <summary>
    /// Capability-scoped variant: binds the advertised tool identity and version so a
    /// prepared dispatch can never be replayed under a different capability contract.
    /// </summary>
    public static string ComputeForCapability(
        string toolId,
        string toolVersion,
        string resolvedExecutable,
        IReadOnlyList<string> arguments,
        string canonicalWorkingDirectory,
        bool networkIntent,
        int timeoutMs,
        TerminalOperationClass operationClass)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedExecutable);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalWorkingDirectory);

        return ComputeCore(
            [Version, "host-capability", toolId, toolVersion],
            resolvedExecutable,
            arguments,
            canonicalWorkingDirectory,
            networkIntent,
            timeoutMs,
            operationClass);
    }

    private static string ComputeCore(
        IReadOnlyList<string> headerFields,
        string resolvedExecutable,
        IReadOnlyList<string> arguments,
        string canonicalWorkingDirectory,
        bool networkIntent,
        int timeoutMs,
        TerminalOperationClass operationClass)
    {
        var executableIdentity = NormalizePathIdentity(resolvedExecutable);
        var workingDirectoryIdentity = NormalizePathIdentity(canonicalWorkingDirectory);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var headerField in headerFields)
            {
                writer.WriteStringValue(headerField);
            }

            writer.WriteStringValue(executableIdentity);
            writer.WriteStartArray();
            foreach (var argument in arguments)
            {
                writer.WriteStringValue(argument);
            }

            writer.WriteEndArray();
            writer.WriteStringValue(workingDirectoryIdentity);
            writer.WriteBooleanValue(networkIntent);
            writer.WriteStringValue(timeoutMs.ToString(CultureInfo.InvariantCulture));
            writer.WriteStringValue(operationClass.ToString());
            writer.WriteEndArray();
        }

        var hash = SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizePathIdentity(string path)
    {
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() ? canonical.ToUpperInvariant() : canonical;
    }
}
