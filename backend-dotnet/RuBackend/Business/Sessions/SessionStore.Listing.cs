namespace RuBackend.Business.Sessions;

public sealed partial class SessionStore
{
    private const int MaximumListedSessions = 500;
    private const int MaximumInspectedSessionFiles = 2000;
    private const int MaximumParsedSessionFiles = 1000;
    private const long MaximumUncachedListingBytes = 32L * 1024 * 1024;
    private static readonly TimeSpan MaximumListingDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SummaryCacheFreshness = TimeSpan.FromMinutes(1);

    private readonly object _summaryCacheLock = new();
    private readonly Dictionary<string, CachedSessionSummary> _summaryCache = new(StringComparer.Ordinal);

    private sealed record CachedSessionSummary(
        long Length,
        long LastWriteTimeUtcTicks,
        long CreationTimeUtcTicks,
        DateTimeOffset CachedAt,
        SessionSummary Summary);

    private readonly record struct SessionFileCandidate(
        string Id,
        SessionFileSignature Signature);

    private readonly record struct SessionFileSignature(
        long Length,
        long LastWriteTimeUtcTicks,
        long CreationTimeUtcTicks);
    public async Task<IReadOnlyList<SessionSummary>> ListAsync(
        CancellationToken cancellationToken)
    {
        var result = await ListWithMetadataAsync(cancellationToken).ConfigureAwait(false);
        return result.Sessions;
    }

    public async Task<SessionListResult> ListWithMetadataAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await ReadSessionSummariesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return new SessionListResult([], IsTruncated: false);
        }
        catch (UnauthorizedAccessException)
        {
            return new SessionListResult([], IsTruncated: false);
        }
    }

    private async Task<SessionListResult> ReadSessionSummariesAsync(
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_sessionDirectory))
        {
            ClearSummaryCache();
            return new SessionListResult([], IsTruncated: false);
        }

        using var listingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        listingCancellation.CancelAfter(MaximumListingDuration);
        var listingToken = listingCancellation.Token;
        var candidates = new List<SessionFileCandidate>();
        var isTruncated = false;
        var metadataScanCompleted = true;
        try
        {
            candidates = CollectSessionFileCandidates(listingToken, out isTruncated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            isTruncated = true;
            metadataScanCompleted = false;
        }

        var selected = candidates.Take(MaximumParsedSessionFiles).ToArray();
        isTruncated |= candidates.Count > selected.Length;
        var summaries = new List<SessionSummary>(Math.Min(selected.Length, MaximumListedSessions));
        var nextCache = new Dictionary<string, CachedSessionSummary>(StringComparer.Ordinal);
        var now = _clock.GetUtcNow();
        var remainingBytes = MaximumUncachedListingBytes;
        foreach (var candidate in selected)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (TryGetFreshCachedSummary(candidate, now, out var cached))
            {
                nextCache[candidate.Id] = cached;
                summaries.Add(cached.Summary);
                continue;
            }

            if (listingToken.IsCancellationRequested)
            {
                isTruncated = true;
                break;
            }

            if (candidate.Signature.Length > remainingBytes)
            {
                isTruncated = true;
                continue;
            }

            remainingBytes -= candidate.Signature.Length;
            var byteAllowance = remainingBytes + candidate.Signature.Length;
            try
            {
                long bytesRead = 0;
                var summary = await ReadSummaryAsync(
                    candidate,
                    now,
                    byteAllowance,
                    bytes => bytesRead += bytes,
                    listingToken).ConfigureAwait(false);
                remainingBytes += candidate.Signature.Length - bytesRead;
                if (summary is null)
                {
                    if (bytesRead >= byteAllowance
                        || TryGetFileSignature(SessionPath(candidate.Id), out var currentSignature)
                        && currentSignature.Length > byteAllowance)
                    {
                        isTruncated = true;
                    }

                    continue;
                }

                nextCache[candidate.Id] = summary;
                summaries.Add(summary.Summary);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                isTruncated = true;
                break;
            }
        }

        if (metadataScanCompleted)
        {
            ReplaceSummaryCache(nextCache);
        }
        var ordered = summaries
            .OrderByDescending(session => session.UpdatedAt)
            .Take(MaximumListedSessions)
            .ToArray();
        isTruncated |= summaries.Count > ordered.Length;
        return new SessionListResult(ordered, isTruncated);
    }

    private List<SessionFileCandidate> CollectSessionFileCandidates(
        CancellationToken cancellationToken,
        out bool isTruncated)
    {
        var candidates = new List<SessionFileCandidate>();
        var inspectedFiles = 0;
        isTruncated = false;
        foreach (var path in Directory.EnumerateFiles(_sessionDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++inspectedFiles > MaximumInspectedSessionFiles)
            {
                isTruncated = true;
                break;
            }

            var id = Path.GetFileNameWithoutExtension(path);
            if (IsValidSessionId(id) && TryGetFileSignature(path, out var signature))
            {
                candidates.Add(new SessionFileCandidate(id, signature));
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Signature.LastWriteTimeUtcTicks)
            .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
            .ToList();
    }

    private bool TryGetFreshCachedSummary(
        SessionFileCandidate candidate,
        DateTimeOffset now,
        out CachedSessionSummary cached)
    {
        lock (_summaryCacheLock)
        {
            if (_summaryCache.TryGetValue(candidate.Id, out var found)
                && HasSameSignature(found, candidate.Signature)
                && now - found.CachedAt < SummaryCacheFreshness)
            {
                cached = found;
                return true;
            }
        }

        cached = null!;
        return false;
    }

    private async Task<CachedSessionSummary?> ReadSummaryAsync(
        SessionFileCandidate candidate,
        DateTimeOffset now,
        long maximumBytes,
        Action<long> bytesRead,
        CancellationToken cancellationToken)
    {
        var document = await LoadUnlockedAsync(
            candidate.Id,
            cancellationToken,
            maximumBytes,
            bytesRead).ConfigureAwait(false);
        return document is null
            ? null
            : new CachedSessionSummary(
                candidate.Signature.Length,
                candidate.Signature.LastWriteTimeUtcTicks,
                candidate.Signature.CreationTimeUtcTicks,
                now,
                ToSummary(document));
    }

    private void ReplaceSummaryCache(
        IReadOnlyDictionary<string, CachedSessionSummary> nextCache)
    {
        lock (_summaryCacheLock)
        {
            _summaryCache.Clear();
            foreach (var entry in nextCache)
            {
                _summaryCache[entry.Key] = entry.Value;
            }
        }
    }

    private static SessionSummary ToSummary(SessionDocument document) => new(
        document.Id,
        document.Title,
        document.CreatedAt,
        document.UpdatedAt,
        document.Messages.Count,
        document.ParentSessionId,
        document.ForkedFromMessageId,
        LastAgentActivityAt: document.LastAgentActivityAt);

    private static bool TryGetFileSignature(string path, out SessionFileSignature signature)
    {
        try
        {
            var file = new FileInfo(path);
            file.Refresh();
            if (!file.Exists)
            {
                signature = default;
                return false;
            }

            signature = new SessionFileSignature(
                file.Length,
                file.LastWriteTimeUtc.Ticks,
                file.CreationTimeUtc.Ticks);
            return true;
        }
        catch (IOException)
        {
            signature = default;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            signature = default;
            return false;
        }
    }

    private static bool HasSameSignature(
        CachedSessionSummary cached,
        SessionFileSignature signature) =>
        cached.Length == signature.Length
        && cached.LastWriteTimeUtcTicks == signature.LastWriteTimeUtcTicks
        && cached.CreationTimeUtcTicks == signature.CreationTimeUtcTicks;

    private void ClearSummaryCache()
    {
        lock (_summaryCacheLock)
        {
            _summaryCache.Clear();
        }
    }

    private void InvalidateSummaryCache(string sessionId)
    {
        lock (_summaryCacheLock)
        {
            _summaryCache.Remove(sessionId);
        }
    }

}
