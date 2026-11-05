namespace RuAgent.Broker.Tools.Files;

/// <summary>
/// Files a session created with <c>write_file</c>, with the content hash this session last
/// left in each one. A later <c>write_file</c> to such a file may replace it without
/// <c>overwrite=true</c> while the file still holds exactly that content: the only bytes
/// replaced are the session's own output, so create-only still protects every file the
/// session did not create and every change made by someone else.
/// </summary>
/// <remarks>
/// Before this, rewriting a file the run had just created failed with <c>file_exists</c>
/// and cost a read plus a retry: two model round trips for no protection. The record is
/// in memory, session-scoped and bounded; a restart or eviction falls back to create-only.
/// </remarks>
internal sealed class SessionAuthoredFiles(int capacity = 4096)
{
    private const int MaximumKeyChars = 4224;

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _hashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _order = new();
    private readonly int _capacity = Math.Max(1, capacity);

    /// <summary>True when this session created <paramref name="canonicalPath"/> and it still holds the session's last write.</summary>
    public bool IsUnchangedSinceOwnWrite(string sessionId, string canonicalPath, string currentHash)
    {
        if (Key(sessionId, canonicalPath) is not { } key || FileHashService.IsMissing(currentHash)) return false;
        lock (_gate)
            return _hashes.TryGetValue(key, out var hash) && string.Equals(hash, currentHash, StringComparison.Ordinal);
    }

    /// <summary>
    /// Records a successful mutation. A file enters the record only when this mutation created
    /// it; a tracked file follows each later write or edit; anything else is left untracked.
    /// </summary>
    public void RecordMutation(string sessionId, string canonicalPath, string? beforeHash, string? afterHash)
    {
        if (Key(sessionId, canonicalPath) is not { } key) return;
        lock (_gate)
        {
            var created = beforeHash is not null && FileHashService.IsMissing(beforeHash);
            if (afterHash is null || FileHashService.IsMissing(afterHash))
            {
                _hashes.Remove(key);
                return;
            }

            if (_hashes.ContainsKey(key))
            {
                _hashes[key] = afterHash;
                return;
            }

            if (!created) return;
            _hashes[key] = afterHash;
            _order.Enqueue(key);
            while (_order.Count > _capacity) _hashes.Remove(_order.Dequeue());
        }
    }

    /// <summary>Forgets a file, e.g. after the session deleted it.</summary>
    public void Forget(string sessionId, string canonicalPath)
    {
        if (Key(sessionId, canonicalPath) is not { } key) return;
        lock (_gate) _hashes.Remove(key);
    }

    private static string? Key(string sessionId, string canonicalPath) =>
        string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(canonicalPath)
            || canonicalPath.Length > MaximumKeyChars
            ? null
            : sessionId + "\n" + canonicalPath;
}
