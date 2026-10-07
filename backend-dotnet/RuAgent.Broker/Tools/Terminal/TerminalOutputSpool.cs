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
    private readonly string _rootDirectory;

    public TerminalOutputSpool(string? rootDirectory = null)
    {
        _rootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ru",
            "spool");
    }

    public void Store(string sessionId, string spoolId, string text) =>
        Store(sessionId, spoolId, text, spill: null);

    public void Store(string sessionId, string spoolId, string text, SpoolSpill? spill)
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
            spill?.Truncated ?? false);
        List<SpoolEntry>? discarded = null;
        lock (_gate)
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
                : ReadSpilledPage(entry, start, length);
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
        return Path.Combine(SessionDirectory(sessionId), SanitizeName(spoolId) + ".log");
    }

    public void RemoveSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        List<SpoolEntry>? discarded = null;
        lock (_gate)
        {
            if (_sessions.Remove(sessionId, out var spools))
            {
                discarded = [.. spools];
            }
        }

        if (discarded is not null)
        {
            foreach (var entry in discarded)
            {
                DeleteFile(entry);
            }
        }

        TryDeleteDirectory(SessionDirectory(sessionId));
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

    private static string ReadSpilledPage(SpoolEntry entry, int start, int length)
    {
        var builder = new StringBuilder(length);
        if (start < entry.Text.Length)
        {
            builder.Append(entry.Text, start, Math.Min(length, entry.Text.Length - start));
        }

        var remaining = length - builder.Length;
        if (remaining > 0 && entry.FilePath is not null)
        {
            using var reader = new StreamReader(entry.FilePath, Encoding.UTF8);
            Skip(reader, start + builder.Length - entry.Text.Length);
            var buffer = new char[Math.Min(remaining, 4096)];
            while (remaining > 0)
            {
                var read = reader.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                if (read <= 0)
                {
                    break;
                }

                builder.Append(buffer, 0, read);
                remaining -= read;
            }
        }

        return builder.ToString();
    }

    private static void Skip(StreamReader reader, int count)
    {
        var buffer = new char[4096];
        while (count > 0)
        {
            var read = reader.Read(buffer, 0, Math.Min(buffer.Length, count));
            if (read <= 0)
            {
                break;
            }

            count -= read;
        }
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

    private string SessionDirectory(string sessionId) => Path.Combine(_rootDirectory, SanitizeName(sessionId));

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

    private sealed record SpoolEntry(string SpoolId, string Text, string? FilePath, int TotalChars, bool Truncated);
}

/// <summary>
/// One spilled spool file: the path the writer streamed to, how many characters
/// were recorded in total, and whether the retention ceiling cut the tail.
/// </summary>
public sealed record SpoolSpill(string FilePath, int TotalChars, bool Truncated);

/// <summary>
/// One page of retained output plus the metadata a receipt reports: the total
/// recorded characters, the spill file when the output overflowed the window,
/// and whether the retention ceiling was reached.
/// </summary>
public sealed record SpoolPage(string Text, int TotalChars, string? FilePath, bool Truncated);
