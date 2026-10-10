using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace RuAgent.Broker.Tools.Files;

/// <summary>
/// Instance-scoped in-memory store of one-time file recovery payloads, shared
/// by the delete and restore adapters of a single execution service. It used
/// to be static state on <see cref="FileRestoreAdapter"/>, so two runtimes in
/// one process could resolve each other's recovery ids; a store per service
/// keeps those ids opaque even in-process. Semantics per record stay identical:
/// 30-minute TTL, 4096 cap, one-time reserve-then-restore.
/// </summary>
internal sealed class InMemoryRecoveryStore
{
    private const int MaxRecoveryRecords = 4096;
    private static readonly TimeSpan RecoveryTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ExternalConsumedTtl = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, RecoveryRecord> _records = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _externalConsumed = new();

    internal sealed record RecoveryRecord(
        string CanonicalPath,
        byte[] Content,
        string Sha256,
        DateTimeOffset CreatedAt,
        bool IsConsumed);

    // For testing: allow injecting a recovery directly (simulates Host gateway having stored recovery)
    internal string RegisterForTest(string canonicalPath, byte[] content, string sha256)
    {
        ArgumentNullException.ThrowIfNull(content);
        ValidateRecoveryHash(content, sha256);
        PruneRecords(DateTimeOffset.UtcNow);
        if (content.Length > FileDeleteAdapter.MaxRecoveryBytes)
        {
            throw new ArgumentException("recovery_too_large", nameof(content));
        }

        var id = "payload_" + Guid.NewGuid().ToString("N");
        var record = new RecoveryRecord(canonicalPath, (byte[])content.Clone(), sha256, DateTimeOffset.UtcNow, false);
        _records[id] = record;
        PruneRecords(DateTimeOffset.UtcNow);
        return id;
    }

    internal void RegisterWithId(string recoveryId, string canonicalPath, byte[] content, string sha256, int maxRecoveryBytes = FileToolLimits.MaxRecoveryBytes)
    {
        ArgumentNullException.ThrowIfNull(content);
        ValidateRecoveryHash(content, sha256);
        PruneRecords(DateTimeOffset.UtcNow);
        if (content.Length > FileToolLimits.ValidateRecoveryBytes(maxRecoveryBytes))
        {
            throw new ArgumentException("recovery_too_large", nameof(content));
        }

        var record = new RecoveryRecord(canonicalPath, (byte[])content.Clone(), sha256, DateTimeOffset.UtcNow, false);
        _records[recoveryId] = record;
        PruneRecords(DateTimeOffset.UtcNow);
    }

    internal void ClearForTest()
    {
        _records.Clear();
        _externalConsumed.Clear();
    }

    internal bool IsAvailable(string recoveryId)
    {
        PruneRecords(DateTimeOffset.UtcNow);
        return _records.TryGetValue(recoveryId, out var rec)
            && !rec.IsConsumed;
    }

    /// <summary>
    /// Peeks the restore target without reserving or consuming the record, so
    /// the dispatcher can select the session-granted adapter root for an
    /// external restore. Returns false for unknown, consumed, or missing ids;
    /// callers then fall back to the lease toolset, which raises the native
    /// recovery error. Never throws.
    /// </summary>
    internal bool TryGetCanonicalPath(string? recoveryId, out string? canonicalPath)
    {
        canonicalPath = null;
        if (string.IsNullOrWhiteSpace(recoveryId))
        {
            return false;
        }

        if (_records.TryGetValue(recoveryId, out var record) && !record.IsConsumed)
        {
            canonicalPath = record.CanonicalPath;
            return true;
        }

        return false;
    }

    internal bool TryGet(string recoveryId, [NotNullWhen(true)] out RecoveryRecord? record)
    {
        PruneRecords(DateTimeOffset.UtcNow);
        return _records.TryGetValue(recoveryId, out record);
    }

    /// <summary>
    /// One-time reservation: atomically flips the record to consumed before the
    /// restore writes anything, so concurrent restores cannot both succeed.
    /// Returns false when another caller reserved or removed the record first.
    /// </summary>
    internal bool TryReserve(string recoveryId, RecoveryRecord record, [NotNullWhen(true)] out RecoveryRecord? reserved)
    {
        reserved = record with { IsConsumed = true };
        return _records.TryUpdate(recoveryId, reserved, record);
    }

    internal void Release(string recoveryId, RecoveryRecord record, RecoveryRecord reserved) =>
        _records.TryUpdate(recoveryId, record, reserved);

    internal bool TryRemove(string recoveryId) => _records.TryRemove(recoveryId, out _);

    /// <summary>
    /// True when the id belongs to this store's in-memory records: such an id
    /// must be consumed through <see cref="TryGet"/>/<see cref="TryReserve"/>,
    /// never through a caller-supplied byte array.
    /// </summary>
    internal bool ContainsRecord(string recoveryId) => _records.ContainsKey(recoveryId);

    // ExecuteWithBytes is the adapter boundary used by persistence-backed hosts. Keep
    // a short-lived replay ledger for those opaque ids as well; otherwise a caller could
    // invoke that public API repeatedly with the same recovery bytes.
    internal bool TryConsumeExternal(string recoveryId)
    {
        PruneExternalConsumed(DateTimeOffset.UtcNow);
        return _externalConsumed.TryAdd(recoveryId, DateTimeOffset.UtcNow);
    }

    internal void ReleaseExternal(string recoveryId) => _externalConsumed.TryRemove(recoveryId, out _);

    private void PruneExternalConsumed(DateTimeOffset now)
    {
        var cutoff = now - ExternalConsumedTtl;
        foreach (var entry in _externalConsumed)
        {
            if (entry.Value <= cutoff)
            {
                _externalConsumed.TryRemove(new KeyValuePair<string, DateTimeOffset>(entry.Key, entry.Value));
            }
        }

        if (_externalConsumed.Count <= MaxRecoveryRecords)
        {
            return;
        }

        foreach (var entry in _externalConsumed
                     .OrderBy(pair => pair.Value)
                     .Take(_externalConsumed.Count - MaxRecoveryRecords))
        {
            _externalConsumed.TryRemove(new KeyValuePair<string, DateTimeOffset>(entry.Key, entry.Value));
        }
    }

    private void PruneRecords(DateTimeOffset now)
    {
        var cutoff = now - RecoveryTtl;
        foreach (var entry in _records)
        {
            if (entry.Value.CreatedAt <= cutoff)
            {
                _records.TryRemove(new KeyValuePair<string, RecoveryRecord>(entry.Key, entry.Value));
            }
        }

        if (_records.Count <= MaxRecoveryRecords)
        {
            return;
        }

        foreach (var entry in _records
                     .OrderBy(pair => pair.Value.CreatedAt)
                     .Take(_records.Count - MaxRecoveryRecords))
        {
            _records.TryRemove(new KeyValuePair<string, RecoveryRecord>(entry.Key, entry.Value));
        }
    }

    internal static void ValidateRecoveryHash(byte[] content, string sha256)
    {
        if (!FileHashService.IsValidSha256(sha256)
            || !string.Equals(FileHashService.ComputeBytesHash(content), sha256, StringComparison.Ordinal))
        {
            throw new ArgumentException("invalid_recovery_hash");
        }
    }
}
