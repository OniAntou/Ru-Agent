namespace RuAgent.Harness.Persistence.Privacy;

public interface IHarnessKeyStore
{
    /// <summary>
    /// Returns a fresh caller-owned key buffer. The caller must zero it after use.
    /// </summary>
    ValueTask<byte[]> GetOrCreateKeyAsync(
        CancellationToken cancellationToken);
}
