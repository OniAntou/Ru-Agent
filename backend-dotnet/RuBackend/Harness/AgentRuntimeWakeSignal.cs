namespace RuBackend.Harness;

/// <summary>
/// Wakes the runtime loop as soon as a run may have work, instead of leaving it to the
/// next poll. The loop waited out a fixed tick before it noticed a new run, a granted
/// trust or a resolved approval: up to a second when idle and a quarter second when busy,
/// added to the user's wait each time.
/// </summary>
/// <remarks>
/// A wake is only a hint. Several wakes before the loop runs collapse into one pass, and
/// the loop still polls on its timer, so a state change no caller signals is picked up as
/// before. Waking without work costs one ordinary loop pass.
/// </remarks>
public sealed class AgentRuntimeWakeSignal
{
    private readonly SemaphoreSlim _pending = new(0, 1);

    public void Wake()
    {
        try
        {
            _pending.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake is already pending; the next pass covers this one too.
        }
    }

    /// <summary>Waits for a wake or the poll interval, whichever comes first.</summary>
    public Task WaitAsync(TimeSpan pollInterval, CancellationToken cancellationToken) =>
        _pending.WaitAsync(pollInterval, cancellationToken);
}
