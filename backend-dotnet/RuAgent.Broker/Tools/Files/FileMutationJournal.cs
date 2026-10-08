namespace RuAgent.Broker.Tools.Files;

public sealed class FileMutationJournal : IDisposable
{
    public const int MaxEntries = 64;
    public const int MaxTotalBytes = 16 * 1024 * 1024;

    private readonly List<JournalEntry> _entries = new();
    private long _totalBytes;
    private bool _disposed;

    private sealed record JournalEntry(
        string CanonicalPath,
        string BeforeHash,
        byte[]? BeforeBytes,
        bool WasMissing,
        string? AppliedHash = null);

    public int Count => _entries.Count;

    public long TotalBytes => _totalBytes;

    public void Record(string canonicalPath, string beforeHash, byte[]? beforeBytes, bool wasMissing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(beforeHash);
        if (_entries.Count >= MaxEntries)
        {
            throw new InvalidOperationException("journal_too_large");
        }

        var bytesLength = beforeBytes?.Length ?? 0;
        if (_totalBytes + bytesLength > MaxTotalBytes)
        {
            throw new InvalidOperationException("journal_too_large");
        }

        var copy = beforeBytes is null ? null : (byte[])beforeBytes.Clone();
        _entries.Add(new JournalEntry(canonicalPath, beforeHash, copy, wasMissing));
        _totalBytes += bytesLength;
    }

    public void Commit()
    {
        _entries.Clear();
        _totalBytes = 0;
    }

    public void MarkApplied(string afterHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(afterHash);
        _entries[^1] = _entries[^1] with { AppliedHash = afterHash };
    }

    public void Rollback()
    {
        // Rollback participates in the same process-wide mutation gate as normal
        // file adapters. Without this, a concurrent adapter call could interleave
        // between the rollback existence check and its replacement/delete.
        FileMutationGuard.WithLock(() =>
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];
                if (entry.AppliedHash is null) continue;
                try
                {
                    if (entry.WasMissing)
                    {
                        FileMutationGuard.DeleteIfHashMatches(entry.CanonicalPath, entry.AppliedHash);
                    }
                    else
                    {
                        var bytes = entry.BeforeBytes ?? Array.Empty<byte>();
                        FileMutationGuard.ReplaceIfHashMatches(entry.CanonicalPath, bytes, entry.AppliedHash);
                    }
                }
                catch
                {
                    // Rollback is best effort; preserve the original execution
                    // error and let reconciliation inspect any partial state.
                }
            }
            return 0;
        });

        _entries.Clear();
        _totalBytes = 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _entries.Clear();
        _totalBytes = 0;
    }
}
