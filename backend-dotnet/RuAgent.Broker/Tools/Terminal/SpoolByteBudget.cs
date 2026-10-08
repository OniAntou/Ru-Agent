namespace RuAgent.Broker.Tools.Terminal;

internal sealed class SpoolGeneration(Guid id)
{
    internal object Gate { get; } = new();

    internal Guid Id { get; } = id;

    internal bool IsActive { get; set; } = true;

    internal long LiveBytes { get; set; }
}

/// <summary>
/// One in-flight writer's live claim against its session's spool byte quota.
/// </summary>
public sealed class SpoolByteBudget
{
    private readonly TerminalOutputSpool _spool;
    private long _claimed;

    internal SpoolByteBudget(TerminalOutputSpool spool, string sessionId, SpoolGeneration generation)
    {
        _spool = spool;
        SessionId = sessionId;
        Generation = generation;
    }

    internal string SessionId { get; }

    internal SpoolGeneration Generation { get; }

    internal bool RunIfCurrent(Action action) => _spool.RunIfCurrent(Generation, action);

    internal void CleanupIfStale() => _spool.CleanupGeneration(SessionId, Generation);

    internal long TryReserve(long bytes)
    {
        if (bytes <= 0)
        {
            return 0;
        }

        var granted = _spool.ReserveSessionBytes(SessionId, Generation, bytes);
        if (granted > 0)
        {
            Interlocked.Add(ref _claimed, granted);
        }

        return granted;
    }

    /// <summary>Drops every live claim this writer holds; safe to call twice.</summary>
    public void Release()
    {
        lock (Generation.Gate)
        {
            var claimed = Interlocked.Exchange(ref _claimed, 0);
            _spool.ReleaseSessionBytes(SessionId, Generation, claimed);
        }
    }

    internal long TakeClaim() => Interlocked.Exchange(ref _claimed, 0);
}
