using System.Security.Cryptography;
using System.Text;

namespace RuAgent.Broker.Tools.Paths;

public enum PathClassification
{
    InsideWorkspace = 0,
    OutsideWorkspace = 1,
    Protected = 2,
}

public sealed record ResolvedPath(
    string InputPath,
    string CanonicalPath,
    PathClassification Classification,
    string CanonicalIntentHash,
    bool IsAbsoluteInput)
{
    public bool IsInsideWorkspace => Classification == PathClassification.InsideWorkspace;

    public bool IsOutsideWorkspace => Classification == PathClassification.OutsideWorkspace;

    public bool IsProtected => Classification == PathClassification.Protected;

    public static string ComputeHash(string canonicalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        var normalized = canonicalPath.ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
