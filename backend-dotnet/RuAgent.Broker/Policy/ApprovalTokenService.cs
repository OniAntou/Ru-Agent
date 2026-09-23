using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace RuAgent.Broker.Policy;

public sealed record ApprovalBinding(
    string RequestId,
    string SessionId,
    string ToolId,
    string ToolVersion,
    string ArgumentsHash,
    DateTimeOffset ExpiresAt,
    long SnapshotVersion = 0,
    string IntentHash = "");

public sealed class ApprovalTokenService
{
    private readonly ConcurrentDictionary<string, ApprovalBinding> _tokens = new(StringComparer.Ordinal);

    public string Issue(
        string requestId,
        string sessionId,
        string toolId,
        string toolVersion,
        string argumentsHash,
        TimeSpan lifetime,
        long snapshotVersion = 0,
        string intentHash = "")
    {
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromSeconds(60))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        DropExpired();
        _tokens[hash] = new ApprovalBinding(
            requestId,
            sessionId,
            toolId,
            toolVersion,
            argumentsHash,
            DateTimeOffset.UtcNow.Add(lifetime),
            snapshotVersion,
            intentHash ?? string.Empty);
        return token;
    }

    public bool TryConsume(string token, ApprovalBinding expected, out ApprovalBinding? binding)
    {
        binding = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        if (!_tokens.TryGetValue(hash, out var current)
            || current.ExpiresAt <= DateTimeOffset.UtcNow
            || !SameIdentity(current, expected))
        {
            return false;
        }

        if (!_tokens.TryRemove(new KeyValuePair<string, ApprovalBinding>(hash, current)))
        {
            return false;
        }

        binding = current;
        return true;
    }

    public void Clear() => _tokens.Clear();

    /// <summary>
    /// Forgets tokens whose lifetime elapsed without being consumed. Without
    /// this, every issued-but-unused token kept its binding (request id,
    /// session id, argument and intent hashes) in the dictionary until
    /// <see cref="Clear"/> or process exit.
    /// </summary>
    private void DropExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _tokens)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _tokens.TryRemove(
                    new KeyValuePair<string, ApprovalBinding>(pair.Key, pair.Value));
            }
        }
    }

    private static bool SameIdentity(ApprovalBinding left, ApprovalBinding right)
    {
        if (!string.Equals(left.RequestId, right.RequestId, StringComparison.Ordinal)
            || !string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal)
            || !string.Equals(left.ToolId, right.ToolId, StringComparison.Ordinal)
            || !string.Equals(left.ToolVersion, right.ToolVersion, StringComparison.Ordinal))
        {
            return false;
        }

        if (left.SnapshotVersion != right.SnapshotVersion)
        {
            return false;
        }

        var leftHash = left.IntentHash ?? string.Empty;
        var rightHash = right.IntentHash ?? string.Empty;
        if (leftHash.Length != rightHash.Length)
        {
            // Constant-time compare requires same length; treat length mismatch as not equal without timing leak.
            // Use FixedTimeEquals on padded bytes: if lengths differ, return false directly (still bounded).
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(left.ArgumentsHash),
                Encoding.UTF8.GetBytes(right.ArgumentsHash)))
        {
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(leftHash),
                Encoding.UTF8.GetBytes(rightHash)))
        {
            return false;
        }

        return true;
    }
}
