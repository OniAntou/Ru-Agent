namespace RuAgent.Hosting;

/// <summary>
/// Work the host is currently driving. Zero means the host may idle out; a registered
/// run (running or parked) keeps it alive so a detached turn keeps progressing.
/// </summary>
public interface IHostIdleProbe
{
    int ActiveWorkCount { get; }
}
