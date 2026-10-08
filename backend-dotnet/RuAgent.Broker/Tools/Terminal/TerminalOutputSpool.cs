using System.Text;

namespace RuAgent.Broker.Tools.Terminal;

/// <summary>
/// Session-scoped retention of full redacted terminal output, keyed by the
/// tool request that produced it. Head/tail receipts stay bounded, while the
/// swallowed middle remains pageable without re-running a non-deterministic
/// command. Output past the in-memory window spills to a session-scoped file
/// so the middle is never lost; entries are already redacted, carry no new
/// capability (the same session already received them), and die with the
/// session or the next Broker start.
/// </summary>
public sealed class TerminalOutputSpool
{
    /// <summary>
    /// Retained outputs per session. Past this the oldest spool is evicted, so
    /// a chatty session degrades to re-running instead of growing memory.
    /// </summary>
    public const int MaxSpoolsPerSession = 32;

    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedList<SpoolEntry>> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SpoolGeneration> _generations = new(StringComparer.Ordinal);
    private readonly string _rootDirectory;
    private readonly long _maxSessionBytes;

    public TerminalOutputSpool(
        string? rootDirectory = null,
        long maxSessionBytes = Protocol.TerminalLimits.MaxSpoolSessionBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSessionBytes);
        _rootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ru",
            "spool");
        _maxSessionBytes = maxSessionBytes;
    }

    /// <summary>
    /// One writer for this session and spool id, carrying its live claim
    /// against the session byte quota so concurrent spill writers cannot
    /// outgrow the quota between Store-time evictions.
    /// </summary>
    public SpoolFileWriter CreateWriter(string sessionId, string spoolId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(spoolId);
        lock (_gate)
        {
            var generation = GetGeneration(sessionId);
            return new SpoolFileWriter(
                CreateFilePath(sessionId, generation.Id, spoolId),
                Protocol.TerminalLimits.MaxSpoolFileBytes,
                new SpoolByteBudget(this, sessionId, generation));
        }
    }

    internal bool RunIfCurrent(SpoolGeneration generation, Action action)
    {
        lock (generation.Gate)
        {
            if (!generation.IsActive)
            {
                return false;
            }

            action();
            return true;
        }
    }

    internal long ReserveSessionBytes(string sessionId, SpoolGeneration generation, long bytes)
    {
        lock (_gate)
        {
            if (!IsCurrent(sessionId, generation))
            {
                return 0;
            }

            var available = _maxSessionBytes - StoredBytes(sessionId) - generation.LiveBytes;
            if (available <= 0)
            {
                return 0;
            }

            var granted = Math.Min(available, bytes);
            generation.LiveBytes += granted;
            return granted;
        }
    }

    internal void ReleaseSessionBytes(string sessionId, SpoolGeneration generation, long bytes)
    {
        lock (_gate)
        {
            ReleaseSessionBytesLocked(sessionId, generation, bytes);
        }
    }

    internal void CleanupGeneration(string sessionId, SpoolGeneration generation)
    {
        lock (generation.Gate)
        {
            if (!generation.IsActive)
            {
                TryDeleteDirectory(SessionDirectory(sessionId, generation.Id));
            }
        }
    }

    public void Store(string sessionId, string spoolId, string text) =>
        Store(sessionId, spoolId, text, spill: null);

    /// <summary>
    /// Stores a finished writer's retention: the window, the spill file when one
    /// opened, and the writer's truncation flag even when no file exists.
    /// </summary>
    public void Store(string sessionId, string spoolId, SpoolFileWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Store(
            sessionId,
            spoolId,
            writer.Window,
            writer.FilePath is null
                ? null
                : new SpoolSpill(writer.FilePath, writer.TotalChars, writer.TotalBytes, writer.Truncated),
            writer.Truncated,
            writer.Budget);
    }

    public void Store(string sessionId, string spoolId, string text, SpoolSpill? spill, bool truncated = false) =>
        Store(sessionId, spoolId, text, spill, truncated, budget: null);

    private void Store(
        string sessionId,
        string spoolId,
        string text,
        SpoolSpill? spill,
        bool truncated,
        SpoolByteBudget? budget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(spoolId);
        ArgumentNullException.ThrowIfNull(text);
        var retained = text.Length > Protocol.TerminalLimits.MaxSpoolChars
            ? text.Substring(0, Protocol.TerminalLimits.MaxSpoolChars)
            : text;
        var entry = new SpoolEntry(
            spoolId,
            retained,
            spill?.FilePath,
            spill?.TotalChars ?? retained.Length,
            spill?.TotalBytes ?? 0,
            spill?.Truncated ?? truncated);
        List<SpoolEntry>? discarded = null;
        if (budget is null)
        {
            lock (_gate)
            {
                StoreEntryLocked(sessionId, spoolId, entry, ref discarded);
            }
        }
        else
        {
            var generation = budget.Generation;
            lock (generation.Gate)
            {
                if (!generation.IsActive)
                {
                    return;
                }

                lock (_gate)
                {
                    if (!IsCurrent(sessionId, generation))
                    {
                        return;
                    }

                    ReleaseSessionBytesLocked(sessionId, generation, budget.TakeClaim());
                    StoreEntryLocked(sessionId, spoolId, entry, ref discarded);
                }
            }
        }

        if (discarded is not null)
        {
            foreach (var item in discarded)
            {
                DeleteFile(item);
            }
        }
    }

    /// <summary>
    /// Full retained text; a spilled entry returns only its in-memory window,
    /// since the file half is paged by <see cref="TryReadPage"/>.
    /// </summary>
    public bool TryRead(string sessionId, string spoolId, out string text)
    {
        text = string.Empty;
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(spoolId))
        {
            return false;
        }

        lock (_gate)
        {
            var entry = Find(sessionId, spoolId);
            if (entry is null)
            {
                return false;
            }

            text = entry.Text;
            return true;
        }
    }

    public bool TryReadPage(string sessionId, string spoolId, int offset, int limit, out SpoolPage page)
    {
        page = new SpoolPage(string.Empty, 0, null, false);
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(spoolId))
        {
            return false;
        }

        SpoolEntry? entry;
        lock (_gate)
        {
            entry = Find(sessionId, spoolId);
        }

        if (entry is null)
        {
            return false;
        }

        var total = entry.TotalChars;
        var start = Math.Clamp(offset, 0, total);
        var length = Math.Clamp(limit, 0, total - start);
        string text;
        try
        {
            text = entry.FilePath is null
                ? entry.Text.Substring(start, length)
                : SpoolPageReader.Read(entry.Text, entry.FilePath, start, length);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        page = new SpoolPage(text, total, entry.FilePath, entry.Truncated);
        return true;
    }

    public bool TryGetSpill(string sessionId, string spoolId, out string? filePath)
    {
        filePath = null;
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(spoolId))
        {
            return false;
        }

        lock (_gate)
        {
            var entry = Find(sessionId, spoolId);
            filePath = entry?.FilePath;
            return entry is not null;
        }
    }

    /// <summary>
    /// Path one spill file for this session would use. The session directory
    /// is created when the first frame actually spills.
    /// </summary>
    public string CreateFilePath(string sessionId, string spoolId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(spoolId);
        lock (_gate)
        {
            return CreateFilePath(sessionId, GetGeneration(sessionId).Id, spoolId);
        }
    }

    public void RemoveSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        List<SpoolEntry>? discarded = null;
        SpoolGeneration? generation;
        lock (_gate)
        {
            _generations.TryGetValue(sessionId, out generation);
            if (generation is null)
            {
                if (_sessions.Remove(sessionId, out var spools))
                {
                    discarded = [.. spools];
                }
            }
        }

        if (generation is not null)
        {
            lock (generation.Gate)
            {
                lock (_gate)
                {
                    if (IsCurrent(sessionId, generation))
                    {
                        _generations.Remove(sessionId);
                        generation.IsActive = false;
                        generation.LiveBytes = 0;
                        if (_sessions.Remove(sessionId, out var spools))
                        {
                            discarded = [.. spools];
                        }
                    }
                }
            }
        }

        if (discarded is not null)
        {
            foreach (var entry in discarded)
            {
                DeleteFile(entry);
            }
        }

        if (generation is not null)
        {
            CleanupGeneration(sessionId, generation);
        }
    }

    /// <summary>
    /// Drops every spilled file from a previous Broker process: spool ids are
    /// per-process and nothing else would ever clear them.
    /// </summary>
    public void CleanStaleRoot()
    {
        try
        {
            if (Directory.Exists(_rootDirectory))
            {
                Directory.Delete(_rootDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private SpoolEntry? Find(string sessionId, string spoolId)
    {
        if (!_sessions.TryGetValue(sessionId, out var spools))
        {
            return null;
        }

        foreach (var entry in spools)
        {
            if (string.Equals(entry.SpoolId, spoolId, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }

    private void StoreEntryLocked(
        string sessionId,
        string spoolId,
        SpoolEntry entry,
        ref List<SpoolEntry>? discarded)
    {
        if (!_sessions.TryGetValue(sessionId, out var spools))
        {
            spools = new LinkedList<SpoolEntry>();
            _sessions[sessionId] = spools;
        }

        for (var node = spools.First; node is not null; node = node.Next)
        {
            if (string.Equals(node.Value.SpoolId, spoolId, StringComparison.Ordinal))
            {
                (discarded ??= []).Add(node.Value);
                spools.Remove(node);
                break;
            }
        }

        spools.AddLast(entry);
        while (spools.Count > MaxSpoolsPerSession)
        {
            (discarded ??= []).Add(spools.First!.Value);
            spools.RemoveFirst();
        }

        discarded = EvictPastByteQuota(spools, discarded);
    }

    private long StoredBytes(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var spools))
        {
            return 0;
        }

        var total = 0L;
        foreach (var entry in spools)
        {
            total += entry.TotalBytes;
        }

        return total;
    }

    private List<SpoolEntry>? EvictPastByteQuota(
        LinkedList<SpoolEntry> spools,
        List<SpoolEntry>? discarded)
    {
        var retainedBytes = 0L;
        foreach (var entry in spools)
        {
            retainedBytes += entry.TotalBytes;
        }

        while (retainedBytes > _maxSessionBytes)
        {
            var oldest = spools.First!.Value;
            (discarded ??= []).Add(oldest);
            spools.RemoveFirst();
            retainedBytes -= oldest.TotalBytes;
        }

        return discarded;
    }

    private static void DeleteFile(SpoolEntry? entry)
    {
        if (entry?.FilePath is null)
        {
            return;
        }

        try
        {
            File.Delete(entry.FilePath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string CreateFilePath(string sessionId, Guid generation, string spoolId) =>
        Path.Combine(SessionDirectory(sessionId, generation), SanitizeName(spoolId) + ".log");

    private string SessionDirectory(string sessionId, Guid generation) =>
        Path.Combine(_rootDirectory, SanitizeName(sessionId), generation.ToString("N"));

    private SpoolGeneration GetGeneration(string sessionId)
    {
        if (!_generations.TryGetValue(sessionId, out var generation))
        {
            generation = new SpoolGeneration(Guid.NewGuid());
            _generations.Add(sessionId, generation);
        }

        return generation;
    }

    private bool IsCurrent(string sessionId, SpoolGeneration generation) =>
        generation.IsActive
        && _generations.TryGetValue(sessionId, out var current)
        && ReferenceEquals(current, generation);

    private void ReleaseSessionBytesLocked(string sessionId, SpoolGeneration generation, long bytes)
    {
        if (bytes <= 0 || !IsCurrent(sessionId, generation))
        {
            return;
        }

        generation.LiveBytes = Math.Max(0, generation.LiveBytes - bytes);
    }

    private static string SanitizeName(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, 64));
        foreach (var character in value)
        {
            if (builder.Length == 64)
            {
                break;
            }

            if (char.IsLetterOrDigit(character) || character is '-' or '_')
            {
                builder.Append(character);
            }
        }

        return builder.Length == 0 ? Guid.NewGuid().ToString("N") : builder.ToString();
    }

    private sealed record SpoolEntry(
        string SpoolId,
        string Text,
        string? FilePath,
        int TotalChars,
        long TotalBytes,
        bool Truncated);

}

/// <summary>
/// One spilled spool file: the path the writer streamed to, how many characters
/// and UTF-8 bytes were recorded in total, and whether the retention ceiling
/// cut the tail.
/// </summary>
public sealed record SpoolSpill(string FilePath, int TotalChars, long TotalBytes, bool Truncated);

/// <summary>
/// One page of retained output plus the metadata a receipt reports: the total
/// recorded characters, the spill file when the output overflowed the window,
/// and whether the retention ceiling was reached.
/// </summary>
public sealed record SpoolPage(string Text, int TotalChars, string? FilePath, bool Truncated);
