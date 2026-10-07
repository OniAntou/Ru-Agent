using System.Security.Cryptography;
using System.Text;
using RuAgent.Broker.Tools.Paths;

namespace RuAgent.Broker.Tools.Files;

public static class FileHashService
{
    public const string Missing = "missing";

    private const int StreamBufferSize = 81920;

    public static bool IsValidSha256(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (var c in value)
        {
            if ((c < '0' || c > '9') && (c < 'a' || c > 'f'))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsMissing(string value) => string.Equals(value, Missing, StringComparison.Ordinal);

    public static void ValidateExpectedSha256(string expected)
    {
        if (IsMissing(expected))
        {
            return;
        }

        if (!IsValidSha256(expected))
        {
            throw new ArgumentException("invalid_expected_sha256");
        }
    }

    public static string ComputeBytesHash(ReadOnlySpan<byte> bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string ComputeStringHash(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return ComputeBytesHash(Encoding.UTF8.GetBytes(content));
    }

    public static string ComputeFileHash(string canonicalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        if (!File.Exists(canonicalPath))
        {
            return Missing;
        }

        HardLinkGuard.RequireSingleLink(canonicalPath);
        using var stream = HardLinkGuard.OpenRead(
            canonicalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.SequentialScan);
        return ComputeStreamHash(stream);
    }

    internal static string ComputeStreamHash(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var sha = SHA256.Create();
        var buffer = new byte[StreamBufferSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            sha.TransformBlock(buffer, 0, read, null, 0);
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var hash = sha.Hash!;
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static async ValueTask<string> ComputeStreamHashAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[StreamBufferSize];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string GetCurrentHash(string canonicalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        if (!File.Exists(canonicalPath))
        {
            if (Directory.Exists(canonicalPath))
            {
                throw new IOException("is_directory");
            }

            return Missing;
        }

        return ComputeFileHash(canonicalPath);
    }
}
