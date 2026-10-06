namespace RuAgent.Broker.Tools.Terminal;

/// <summary>
/// Session-scoped retention of full redacted terminal output, keyed by the
/// tool request that produced it. Head/tail receipts stay bounded, while the
/// swallowed middle remains pageable without re-running a non-deterministic
/// command. Entries are already redacted, carry no new capability (the same
/// session already received them), and die with the session.
/// </summary>
public sealed class TerminalOutputSpool
{
    /// <summary>
    /// Retained outputs per session. Past this the oldest spool is evicted, so
    /// a chatty session degrades to re-running instead of growing memory.
    /// </summary>
    public const int MaxSpoolsPerSession = 16;

    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedList<SpoolEntry>> _sessions = new(StringComparer.Ordinal);

    public void Store(string sessionId, string spoolId, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(spoolId);
        ArgumentNullException.ThrowIfNull(text);
        var retained = text.Length > Protocol.TerminalLimits.MaxSpoolChars
            ? text.Substring(0, Protocol.TerminalLimits.MaxSpoolChars)
            : text;
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
                    spools.Remove(node);
                    break;
                }
            }

            spools.AddLast(new SpoolEntry(spoolId, retained));
            while (spools.Count > MaxSpoolsPerSession)
            {
                spools.RemoveFirst();
            }
        }
    }

    public bool TryRead(string sessionId, string spoolId, out string text)
    {
        text = string.Empty;
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(spoolId))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var spools))
            {
                return false;
            }

            foreach (var entry in spools)
            {
                if (string.Equals(entry.SpoolId, spoolId, StringComparison.Ordinal))
                {
                    text = entry.Text;
                    return true;
                }
            }

            return false;
        }
    }

    public void RemoveSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        lock (_gate)
        {
            _sessions.Remove(sessionId);
        }
    }

    private sealed record SpoolEntry(string SpoolId, string Text);
}
